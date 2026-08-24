// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal readonly struct EvaluationOperationId : IEquatable<EvaluationOperationId>
    {
        internal EvaluationOperationId(
            string modulePath,
            int line,
            int column,
            int moduleVersion,
            string kind,
            string name)
        {
            ModulePath = modulePath ?? string.Empty;
            Line = line;
            Column = column;
            ModuleVersion = moduleVersion;
            Kind = kind ?? string.Empty;
            Name = name ?? string.Empty;
        }

        internal string ModulePath { get; }

        internal int Line { get; }

        internal int Column { get; }

        internal int ModuleVersion { get; }

        internal string Kind { get; }

        internal string Name { get; }

        internal static EvaluationOperationId Create(
            ProjectElement element,
            string kind,
            string name = null,
            ElementLocation location = null)
        {
            ElementLocation operationLocation = location ?? element.Location;
            string modulePath =
                operationLocation?.File ??
                element.ContainingProject?.FullPath ??
                string.Empty;
            return new EvaluationOperationId(
                modulePath,
                operationLocation?.Line ?? 0,
                operationLocation?.Column ?? 0,
                element.ContainingProject?.Version ?? 0,
                kind,
                name);
        }

        public bool Equals(EvaluationOperationId other) =>
            Line == other.Line &&
            Column == other.Column &&
            ModuleVersion == other.ModuleVersion &&
            StringComparer.Ordinal.Equals(ModulePath, other.ModulePath) &&
            StringComparer.Ordinal.Equals(Kind, other.Kind) &&
            StringComparer.Ordinal.Equals(Name, other.Name);

        public override bool Equals(object obj) =>
            obj is EvaluationOperationId other && Equals(other);

        public override int GetHashCode()
        {
            int hash = StringComparer.Ordinal.GetHashCode(ModulePath);
            hash = (hash * 397) ^ Line;
            hash = (hash * 397) ^ Column;
            hash = (hash * 397) ^ ModuleVersion;
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Kind);
            hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
            return hash;
        }
    }

    internal sealed class EvaluationModuleCache
    {
        private readonly ConcurrentDictionary<ProjectRootElement, EvaluationModule> _modules =
            new ConcurrentDictionary<ProjectRootElement, EvaluationModule>(
                ProjectRootElementReferenceComparer.Instance);
        private readonly object _moduleHandleLock = new object();
        private EvaluationModule[] _modulesByHandle = new EvaluationModule[64];
        private int _nextModuleHandle;
        private long _hits;
        private long _misses;
        private long _lowerings;

        internal PropertyIdentityTable PropertyIdentities { get; } =
            new PropertyIdentityTable();

        internal PropertyAssignmentOperation GetPropertyAssignment(
            ProjectPropertyElement element)
        {
            return GetModule(element).GetPropertyAssignment(element);
        }

        internal ConditionOperation GetPropertyGroupCondition(
            ProjectPropertyGroupElement element)
        {
            return GetModule(element).GetPropertyGroupCondition(element);
        }

        internal EvaluationModule GetModule(ProjectRootElement project)
        {
            while (true)
            {
                int version = project.Version;
                if (_modules.TryGetValue(project, out EvaluationModule existing) &&
                    existing.Version == version)
                {
                    Interlocked.Increment(ref _hits);
                    return existing;
                }

                Interlocked.Increment(ref _misses);
                if (!EvaluationModule.TryCreate(
                        project,
                        PropertyIdentities,
                        out EvaluationModule candidate))
                {
                    continue;
                }

                lock (_moduleHandleLock)
                {
                    if (project.Version != candidate.Version)
                    {
                        continue;
                    }

                    if (!_modules.TryGetValue(project, out existing))
                    {
                        Register(candidate);
                        if (!_modules.TryAdd(project, candidate))
                        {
                            Unregister(candidate);
                            throw new InvalidOperationException(
                                "Evaluation module publication was modified outside the publication lock.");
                        }

                        if (project.Version == candidate.Version)
                        {
                            Interlocked.Increment(ref _lowerings);
                            return candidate;
                        }

                        Remove(project, candidate);
                        continue;
                    }

                    if (existing.Version == candidate.Version)
                    {
                        Interlocked.Increment(ref _hits);
                        return existing;
                    }

                    Register(candidate);
                    if (!_modules.TryUpdate(project, candidate, existing))
                    {
                        Unregister(candidate);
                        throw new InvalidOperationException(
                            "Evaluation module publication was modified outside the publication lock.");
                    }

                    if (project.Version == candidate.Version)
                    {
                        Interlocked.Increment(ref _lowerings);
                        return candidate;
                    }

                    Remove(project, candidate);
                }
            }
        }

        internal EvaluationModule GetModule(int handle)
        {
            EvaluationModule[] modules = Volatile.Read(ref _modulesByHandle);
            if ((uint)handle < (uint)modules.Length)
            {
                EvaluationModule module = Volatile.Read(ref modules[handle]);
                if (module is not null)
                {
                    return module;
                }
            }

            throw new InvalidOperationException(
                $"Evaluation module handle {handle} is not registered.");
        }

        internal ModuleEvaluationCacheMetrics GetMetrics() =>
            new ModuleEvaluationCacheMetrics(
                Interlocked.Read(ref _hits),
                Interlocked.Read(ref _misses),
                Interlocked.Read(ref _lowerings));

        private EvaluationModule GetModule(ProjectElement element)
        {
            ProjectRootElement containingProject = element.ContainingProject;
            return GetModule(containingProject);
        }

        private void Remove(
            ProjectRootElement project,
            EvaluationModule candidate)
        {
            ((ICollection<KeyValuePair<ProjectRootElement, EvaluationModule>>)_modules)
                .Remove(new KeyValuePair<ProjectRootElement, EvaluationModule>(
                    project,
                    candidate));
        }

        private void Register(EvaluationModule module)
        {
            int handle = checked(++_nextModuleHandle);
            module.AssignHandle(handle);

            EvaluationModule[] modules = _modulesByHandle;
            if (handle >= modules.Length)
            {
                int newLength = modules.Length;
                while (handle >= newLength)
                {
                    newLength = checked(newLength * 2);
                }

                Array.Resize(ref modules, newLength);
                Volatile.Write(ref _modulesByHandle, modules);
            }

            if (modules[handle] is not null)
            {
                throw new InvalidOperationException(
                    $"Evaluation module handle {handle} is already registered.");
            }

            Volatile.Write(ref modules[handle], module);
        }

        private void Unregister(EvaluationModule module)
        {
            EvaluationModule[] modules = _modulesByHandle;
            if ((uint)module.Handle < (uint)modules.Length &&
                ReferenceEquals(modules[module.Handle], module))
            {
                Volatile.Write(
                    ref modules[module.Handle],
                    null);
            }
        }

        private sealed class ProjectRootElementReferenceComparer :
            IEqualityComparer<ProjectRootElement>
        {
            internal static ProjectRootElementReferenceComparer Instance { get; } =
                new ProjectRootElementReferenceComparer();

            public bool Equals(
                ProjectRootElement x,
                ProjectRootElement y) => ReferenceEquals(x, y);

            public int GetHashCode(ProjectRootElement obj) =>
                RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal readonly struct ModuleEvaluationCacheMetrics
    {
        internal ModuleEvaluationCacheMetrics(
            long hits,
            long misses,
            long lowerings)
        {
            Hits = hits;
            Misses = misses;
            Lowerings = lowerings;
        }

        internal long Hits { get; }

        internal long Misses { get; }

        internal long Lowerings { get; }
    }

    internal enum ModuleElementKind : byte
    {
        PropertyGroup,
        ItemGroup,
        ItemDefinitionGroup,
        Target,
        Import,
        ImportGroup,
        UsingTask,
        Choose,
    }

    internal readonly struct TableRange
    {
        internal TableRange(int start, int count)
        {
            Start = start;
            Count = count;
        }

        internal int Start { get; }

        internal int Count { get; }
    }

    internal readonly struct ModuleElement
    {
        internal ModuleElement(ModuleElementKind kind, int localIndex)
        {
            Kind = kind;
            LocalIndex = localIndex;
        }

        internal ModuleElementKind Kind { get; }

        internal int LocalIndex { get; }
    }

    internal readonly struct ModuleHeader
    {
        internal ModuleHeader(
            int rootSourceId,
            string directoryPath,
            int initialTargetsExpressionId,
            int defaultTargetsExpressionId,
            int treatAsLocalPropertyExpressionId,
            TableRange topImplicitImports,
            TableRange rootElements,
            TableRange bottomImplicitImports,
            bool supportsReturns)
        {
            RootSourceId = rootSourceId;
            DirectoryPath = directoryPath;
            InitialTargetsExpressionId = initialTargetsExpressionId;
            DefaultTargetsExpressionId = defaultTargetsExpressionId;
            TreatAsLocalPropertyExpressionId = treatAsLocalPropertyExpressionId;
            TopImplicitImports = topImplicitImports;
            RootElements = rootElements;
            BottomImplicitImports = bottomImplicitImports;
            SupportsReturns = supportsReturns;
        }

        internal int RootSourceId { get; }

        internal string DirectoryPath { get; }

        internal int InitialTargetsExpressionId { get; }

        internal int DefaultTargetsExpressionId { get; }

        internal int TreatAsLocalPropertyExpressionId { get; }

        internal TableRange TopImplicitImports { get; }

        internal TableRange RootElements { get; }

        internal TableRange BottomImplicitImports { get; }

        internal bool SupportsReturns { get; }
    }

    internal readonly struct ExpressionCallSite
    {
        internal ExpressionCallSite(int expressionStringId, int sourceId)
        {
            ExpressionStringId = expressionStringId;
            SourceId = sourceId;
        }

        internal int ExpressionStringId { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ConditionCallSite
    {
        internal ConditionCallSite(int conditionStringId, int sourceId)
        {
            ConditionStringId = conditionStringId;
            SourceId = sourceId;
        }

        internal int ConditionStringId { get; }

        internal int SourceId { get; }
    }

    internal readonly struct PropertyGroupTemplate
    {
        internal PropertyGroupTemplate(
            int conditionId,
            int compiledConditionId,
            TableRange properties,
            TableRange propertySegments,
            int sourceId)
        {
            ConditionId = conditionId;
            CompiledConditionId = compiledConditionId;
            Properties = properties;
            PropertySegments = propertySegments;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal int CompiledConditionId { get; }

        internal TableRange Properties { get; }

        internal TableRange PropertySegments { get; }

        internal int SourceId { get; }
    }

    internal enum PropertySegmentKind : byte
    {
        Scalar,
        CompiledEffectBatch,
    }

    internal readonly struct PropertySegmentTemplate
    {
        internal PropertySegmentTemplate(
            PropertySegmentKind kind,
            TableRange properties,
            TableRange externalPropertyReads,
            TableRange instructions = default)
        {
            Kind = kind;
            Properties = properties;
            ExternalPropertyReads = externalPropertyReads;
            Instructions = instructions;
        }

        internal PropertySegmentKind Kind { get; }

        internal TableRange Properties { get; }

        internal TableRange ExternalPropertyReads { get; }

        internal TableRange Instructions { get; }

        internal ConstantPropertySegmentState ConstantState { get; private init; }

        internal PropertySegmentTemplate WithInstructions(
            TableRange instructions,
            bool isConstant) =>
            new PropertySegmentTemplate(
                Kind,
                Properties,
                ExternalPropertyReads,
                instructions)
            {
                ConstantState = isConstant
                    ? new ConstantPropertySegmentState()
                    : null,
            };
    }

    internal readonly struct PropertyTemplate
    {
        internal PropertyTemplate(
            PropertyId propertyId,
            int nameStringId,
            int conditionId,
            int compiledConditionId,
            int valueExpressionId,
            int constantValueStringId,
            TableRange compiledValueParts,
            bool requiresExpansion,
            bool isDeadStore,
            int sourceId)
        {
            PropertyId = propertyId;
            NameStringId = nameStringId;
            ConditionId = conditionId;
            CompiledConditionId = compiledConditionId;
            ValueExpressionId = valueExpressionId;
            ConstantValueStringId = constantValueStringId;
            CompiledValueParts = compiledValueParts;
            RequiresExpansion = requiresExpansion;
            IsDeadStore = isDeadStore;
            SourceId = sourceId;
        }

        internal PropertyId PropertyId { get; }

        internal int NameStringId { get; }

        internal int ConditionId { get; }

        internal int CompiledConditionId { get; }

        internal int ValueExpressionId { get; }

        internal int ConstantValueStringId { get; }

        internal TableRange CompiledValueParts { get; }

        internal bool RequiresExpansion { get; }

        internal bool IsDeadStore { get; }

        internal int SourceId { get; }

        internal PropertyTemplate AsDeadStore() =>
            new PropertyTemplate(
                PropertyId,
                NameStringId,
                ConditionId,
                CompiledConditionId,
                ValueExpressionId,
                ConstantValueStringId,
                CompiledValueParts,
                RequiresExpansion,
                isDeadStore: true,
                SourceId);
    }

    internal enum CompiledPropertyValuePartKind : byte
    {
        Literal,
        PropertyReference,
        ExternalPropertyReference,
        ContextualPropertyReference,
        Function,
    }

    internal readonly struct CompiledPropertyValuePart
    {
        internal CompiledPropertyValuePart(
            CompiledPropertyValuePartKind kind,
            int value)
        {
            Kind = kind;
            Value = value;
        }

        internal CompiledPropertyValuePartKind Kind { get; }

        internal int Value { get; }
    }

    internal enum CompiledPropertyFunctionKind : byte
    {
        Add,
        EnsureTrailingSlash,
        Escape,
        GetDirectoryNameOfFileAbove,
        GetTargetFrameworkIdentifier,
        GetTargetFrameworkVersion,
        GetTargetPlatformIdentifier,
        GetTargetPlatformVersion,
        GetToolsDirectory32,
        IsRunningFromVisualStudio,
        NormalizeDirectory,
        NormalizePath,
        PathCombine,
        PathDirectorySeparatorChar,
        PathGetDirectoryName,
        PathGetFullPath,
        RuntimeInformationProcessArchitectureLowerInvariant,
        RuntimeInformationRuntimeIdentifier,
        StringContains,
        StringEndsWith,
        StringEquals,
        StringLastIndexOf,
        StringReplace,
        StringStartsWith,
        StringSubstring,
        StringToLower,
        StringToLowerInvariant,
        StringToUpper,
        StringToUpperInvariant,
        StringTrim,
        StringTrimEnd,
        StringTrimStart,
        Subtract,
        ValueOrDefault,
        VersionBuild,
        VersionLessThan,
        VersionParseToStringTwo,
    }

    internal readonly struct CompiledPropertyFunction
    {
        internal CompiledPropertyFunction(
            CompiledPropertyFunctionKind kind,
            TableRange receiver,
            TableRange arguments,
            int expressionStringId)
        {
            Kind = kind;
            Receiver = receiver;
            Arguments = arguments;
            ExpressionStringId = expressionStringId;
        }

        internal CompiledPropertyFunctionKind Kind { get; }

        internal TableRange Receiver { get; }

        internal TableRange Arguments { get; }

        internal int ExpressionStringId { get; }
    }

    internal readonly struct CompiledPropertyFunctionArgument
    {
        internal CompiledPropertyFunctionArgument(TableRange valueParts)
        {
            ValueParts = valueParts;
        }

        internal TableRange ValueParts { get; }
    }

    internal enum PropertyInstructionKind : byte
    {
        BranchIfPropertyConditionFalse,
        SetLiteral,
        SetValue,
        SetExpandedValue,
        AppendLiteral,
        AppendLocalProperty,
        AppendExternalProperty,
        AppendContextualProperty,
        AppendFunction,
    }

    internal enum CompiledConditionKind : byte
    {
        Equal,
        NotEqual,
    }

    internal enum CompiledConditionInstructionKind : byte
    {
        BranchIfComparisonFalse,
        BranchIfComparisonTrue,
        ReturnComparison,
        ReturnFalse,
        ReturnTrue,
    }

    internal readonly struct CompiledConditionInstruction
    {
        internal CompiledConditionInstruction(
            CompiledConditionInstructionKind kind,
            int argument0 = 0,
            int argument1 = 0)
        {
            Kind = kind;
            Argument0 = argument0;
            Argument1 = argument1;
        }

        internal CompiledConditionInstructionKind Kind { get; }

        internal int Argument0 { get; }

        internal int Argument1 { get; }
    }

    internal enum CompiledConditionOperandKind : byte
    {
        Literal,
        Property,
        ExpandedValue,
        Metadata,
    }

    internal enum CompiledConditionValuePartKind : byte
    {
        Literal,
        Property,
    }

    internal readonly struct CompiledConditionValuePart
    {
        internal CompiledConditionValuePart(
            CompiledConditionValuePartKind kind,
            int value)
        {
            Kind = kind;
            Value = value;
        }

        internal CompiledConditionValuePartKind Kind { get; }

        internal int Value { get; }
    }

    internal readonly struct CompiledConditionOperand
    {
        internal CompiledConditionOperand(
            CompiledConditionOperandKind kind,
            int value,
            int count = 0)
        {
            Kind = kind;
            Value = value;
            Count = count;
        }

        internal CompiledConditionOperandKind Kind { get; }

        internal int Value { get; }

        internal int Count { get; }
    }

    internal readonly struct CompiledConditionComparison
    {
        internal CompiledConditionComparison(
            CompiledConditionKind kind,
            CompiledConditionOperand left,
            CompiledConditionOperand right,
            int leftRawStringId,
            int rightRawStringId)
        {
            Kind = kind;
            Left = left;
            Right = right;
            LeftRawStringId = leftRawStringId;
            RightRawStringId = rightRawStringId;
        }

        internal CompiledConditionKind Kind { get; }

        internal CompiledConditionOperand Left { get; }

        internal CompiledConditionOperand Right { get; }

        internal int LeftRawStringId { get; }

        internal int RightRawStringId { get; }
    }

    internal readonly struct CompiledCondition
    {
        internal CompiledCondition(
            TableRange instructions,
            int sourceId)
        {
            Instructions = instructions;
            SourceId = sourceId;
        }

        internal TableRange Instructions { get; }

        internal int SourceId { get; }
    }

    internal readonly struct PropertyInstruction
    {
        internal PropertyInstruction(
            PropertyInstructionKind kind,
            int argument0,
            int argument1 = 0)
        {
            Kind = kind;
            Argument0 = argument0;
            Argument1 = argument1;
        }

        internal PropertyInstructionKind Kind { get; }

        internal int Argument0 { get; }

        internal int Argument1 { get; }
    }

    internal readonly struct CompiledPropertyExternalRead
    {
        internal CompiledPropertyExternalRead(
            PropertyId propertyId,
            int nameStringId)
        {
            PropertyId = propertyId;
            NameStringId = nameStringId;
        }

        internal PropertyId PropertyId { get; }

        internal int NameStringId { get; }
    }

    internal readonly struct ImportGroupTemplate
    {
        internal ImportGroupTemplate(
            int conditionId,
            TableRange imports,
            int sourceId)
        {
            ConditionId = conditionId;
            Imports = imports;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal TableRange Imports { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ImportTemplate
    {
        internal ImportTemplate(
            int projectExpressionId,
            int sourceId)
        {
            ProjectExpressionId = projectExpressionId;
            SourceId = sourceId;
        }

        internal int ProjectExpressionId { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ChooseTemplate
    {
        internal ChooseTemplate(TableRange arms, int sourceId)
        {
            Arms = arms;
            SourceId = sourceId;
        }

        internal TableRange Arms { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ChooseArmTemplate
    {
        internal ChooseArmTemplate(
            int conditionId,
            TableRange children,
            int sourceId,
            bool isOtherwise)
        {
            ConditionId = conditionId;
            Children = children;
            SourceId = sourceId;
            IsOtherwise = isOtherwise;
        }

        internal int ConditionId { get; }

        internal TableRange Children { get; }

        internal int SourceId { get; }

        internal bool IsOtherwise { get; }
    }

    internal readonly struct DeferredElementRef
    {
        internal DeferredElementRef(int moduleHandle, int localIndex)
        {
            ModuleHandle = moduleHandle;
            LocalIndex = localIndex;
        }

        internal int ModuleHandle { get; }

        internal int LocalIndex { get; }
    }

    internal readonly struct ItemGroupTemplate
    {
        internal ItemGroupTemplate(
            int conditionId,
            TableRange items,
            int sourceId)
        {
            ConditionId = conditionId;
            Items = items;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal TableRange Items { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ItemTemplate
    {
        internal ItemTemplate(
            int itemTypeStringId,
            ItemOperationKind operationKind,
            int conditionId,
            int includeExpressionId,
            int excludeExpressionId,
            int removeExpressionId,
            int updateExpressionId,
            int matchOnMetadataExpressionId,
            int matchOnMetadataOptionsStringId,
            TableRange metadata,
            int sourceId)
        {
            ItemTypeStringId = itemTypeStringId;
            OperationKind = operationKind;
            ConditionId = conditionId;
            IncludeExpressionId = includeExpressionId;
            ExcludeExpressionId = excludeExpressionId;
            RemoveExpressionId = removeExpressionId;
            UpdateExpressionId = updateExpressionId;
            MatchOnMetadataExpressionId = matchOnMetadataExpressionId;
            MatchOnMetadataOptionsStringId = matchOnMetadataOptionsStringId;
            Metadata = metadata;
            SourceId = sourceId;
        }

        internal int ItemTypeStringId { get; }

        internal ItemOperationKind OperationKind { get; }

        internal int ConditionId { get; }

        internal int IncludeExpressionId { get; }

        internal int ExcludeExpressionId { get; }

        internal int RemoveExpressionId { get; }

        internal int UpdateExpressionId { get; }

        internal int MatchOnMetadataExpressionId { get; }

        internal int MatchOnMetadataOptionsStringId { get; }

        internal TableRange Metadata { get; }

        internal int SourceId { get; }
    }

    internal enum ItemOperationKind : byte
    {
        Include,
        Remove,
        Update,
    }

    internal readonly struct ItemDefinitionGroupTemplate
    {
        internal ItemDefinitionGroupTemplate(
            int conditionId,
            TableRange itemDefinitions,
            int sourceId)
        {
            ConditionId = conditionId;
            ItemDefinitions = itemDefinitions;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal TableRange ItemDefinitions { get; }

        internal int SourceId { get; }
    }

    internal readonly struct ItemDefinitionTemplate
    {
        internal ItemDefinitionTemplate(
            int itemTypeStringId,
            int conditionId,
            TableRange metadata,
            int sourceId)
        {
            ItemTypeStringId = itemTypeStringId;
            ConditionId = conditionId;
            Metadata = metadata;
            SourceId = sourceId;
        }

        internal int ItemTypeStringId { get; }

        internal int ConditionId { get; }

        internal TableRange Metadata { get; }

        internal int SourceId { get; }
    }

    internal readonly struct MetadataTemplate
    {
        internal MetadataTemplate(
            int nameStringId,
            int conditionId,
            int compiledConditionId,
            int valueExpressionId,
            int sourceId)
        {
            NameStringId = nameStringId;
            ConditionId = conditionId;
            CompiledConditionId = compiledConditionId;
            ValueExpressionId = valueExpressionId;
            SourceId = sourceId;
        }

        internal int NameStringId { get; }

        internal int ConditionId { get; }

        internal int CompiledConditionId { get; }

        internal int ValueExpressionId { get; }

        internal int SourceId { get; }
    }

    internal readonly struct UsingTaskTemplate
    {
        internal UsingTaskTemplate(
            int conditionId,
            int taskNameExpressionId,
            int taskFactoryExpressionId,
            int assemblyFileExpressionId,
            int assemblyNameExpressionId,
            int runtimeExpressionId,
            int architectureExpressionId,
            int overrideExpressionId,
            int sourceId)
        {
            ConditionId = conditionId;
            TaskNameExpressionId = taskNameExpressionId;
            TaskFactoryExpressionId = taskFactoryExpressionId;
            AssemblyFileExpressionId = assemblyFileExpressionId;
            AssemblyNameExpressionId = assemblyNameExpressionId;
            RuntimeExpressionId = runtimeExpressionId;
            ArchitectureExpressionId = architectureExpressionId;
            OverrideExpressionId = overrideExpressionId;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal int TaskNameExpressionId { get; }

        internal int TaskFactoryExpressionId { get; }

        internal int AssemblyFileExpressionId { get; }

        internal int AssemblyNameExpressionId { get; }

        internal int RuntimeExpressionId { get; }

        internal int ArchitectureExpressionId { get; }

        internal int OverrideExpressionId { get; }

        internal int SourceId { get; }
    }

    internal readonly struct TargetTemplate
    {
        internal TargetTemplate(
            int nameStringId,
            int beforeTargetsExpressionId,
            int afterTargetsExpressionId,
            int sourceId)
        {
            NameStringId = nameStringId;
            BeforeTargetsExpressionId = beforeTargetsExpressionId;
            AfterTargetsExpressionId = afterTargetsExpressionId;
            SourceId = sourceId;
        }

        internal int NameStringId { get; }

        internal int BeforeTargetsExpressionId { get; }

        internal int AfterTargetsExpressionId { get; }

        internal int SourceId { get; }
    }

    internal sealed class EvaluationModule
    {
        private readonly ImmutableDictionary<ProjectPropertyElement, PropertyAssignmentOperation>
            _propertyAssignments;
        private readonly ImmutableDictionary<ProjectPropertyGroupElement, ConditionOperation>
            _propertyGroupConditions;
        private readonly string[] _strings;
        private readonly ProjectElement[] _sources;
        private readonly ConcurrentDictionary<long, PropertyDelta>
            _constantPropertyDeltas =
                new ConcurrentDictionary<long, PropertyDelta>();
        private int _handle;

        private EvaluationModule(
            ProjectRootElement source,
            int version,
            ModuleHeader header,
            ModuleElement[] elements,
            PropertyGroupTemplate[] propertyGroups,
            PropertySegmentTemplate[] propertySegments,
            PropertyTemplate[] properties,
            CompiledPropertyValuePart[] compiledPropertyValueParts,
            CompiledPropertyFunction[] compiledPropertyFunctions,
            CompiledPropertyFunctionArgument[] compiledPropertyFunctionArguments,
            PropertyInstruction[] propertyInstructions,
            CompiledPropertyExternalRead[] compiledPropertyExternalReads,
            CompiledCondition[] compiledConditions,
            CompiledConditionInstruction[] compiledConditionInstructions,
            CompiledConditionComparison[] compiledConditionComparisons,
            CompiledPropertyExternalRead[] compiledConditionPropertyReads,
            CompiledConditionValuePart[] compiledConditionValueParts,
            ImportGroupTemplate[] importGroups,
            ImportTemplate[] imports,
            ChooseTemplate[] chooses,
            ChooseArmTemplate[] chooseArms,
            ItemGroupTemplate[] itemGroups,
            ItemTemplate[] items,
            ItemDefinitionGroupTemplate[] itemDefinitionGroups,
            ItemDefinitionTemplate[] itemDefinitions,
            MetadataTemplate[] metadata,
            UsingTaskTemplate[] usingTasks,
            TargetTemplate[] targets,
            ExpressionCallSite[] expressions,
            ConditionCallSite[] conditions,
            string[] strings,
            ProjectElement[] sources,
            ImmutableArray<PropertyAssignmentOperation> propertyAssignments,
            ImmutableArray<ConditionOperation> propertyGroupConditionOperations,
            ImmutableDictionary<ProjectPropertyElement, PropertyAssignmentOperation>
                propertyAssignmentsByElement,
            ImmutableDictionary<ProjectPropertyGroupElement, ConditionOperation>
                propertyGroupConditions)
        {
            Source = source;
            Version = version;
            Header = header;
            Elements = elements;
            PropertyGroups = propertyGroups;
            PropertySegments = propertySegments;
            Properties = properties;
            CompiledPropertyValueParts = compiledPropertyValueParts;
            CompiledPropertyFunctions = compiledPropertyFunctions;
            CompiledPropertyFunctionArguments =
                compiledPropertyFunctionArguments;
            EvaluationPerformanceInstrumentation
                .RecordCompiledPropertyModuleShape(
                    compiledPropertyValueParts.Length,
                    compiledPropertyFunctions.Length,
                    compiledPropertyFunctionArguments.Length);
            PropertyInstructions = propertyInstructions;
            CompiledPropertyExternalReads = compiledPropertyExternalReads;
            CompiledConditions = compiledConditions;
            CompiledConditionInstructions = compiledConditionInstructions;
            CompiledConditionComparisons = compiledConditionComparisons;
            CompiledConditionPropertyReads =
                compiledConditionPropertyReads;
            CompiledConditionValueParts =
                compiledConditionValueParts;
            ImportGroups = importGroups;
            Imports = imports;
            Chooses = chooses;
            ChooseArms = chooseArms;
            ItemGroups = itemGroups;
            Items = items;
            ItemDefinitionGroups = itemDefinitionGroups;
            ItemDefinitions = itemDefinitions;
            Metadata = metadata;
            UsingTasks = usingTasks;
            Targets = targets;
            ExpressionCallSites = expressions;
            ConditionCallSites = conditions;
            _strings = strings;
            _sources = sources;
            PropertyAssignments = propertyAssignments;
            PropertyGroupConditionOperations = propertyGroupConditionOperations;
            _propertyAssignments = propertyAssignmentsByElement;
            _propertyGroupConditions = propertyGroupConditions;
        }

        internal ProjectRootElement Source { get; }

        internal int Version { get; }

        internal int Handle => Volatile.Read(ref _handle);

        internal ModuleHeader Header { get; }

        internal ModuleElement[] Elements { get; }

        internal PropertyGroupTemplate[] PropertyGroups { get; }

        internal PropertySegmentTemplate[] PropertySegments { get; }

        internal PropertyTemplate[] Properties { get; }

        internal CompiledPropertyValuePart[] CompiledPropertyValueParts { get; }

        internal CompiledPropertyFunction[] CompiledPropertyFunctions { get; }

        internal CompiledPropertyFunctionArgument[] CompiledPropertyFunctionArguments { get; }

        internal PropertyInstruction[] PropertyInstructions { get; }

        internal CompiledPropertyExternalRead[] CompiledPropertyExternalReads { get; }

        internal CompiledCondition[] CompiledConditions { get; }

        internal CompiledConditionInstruction[] CompiledConditionInstructions { get; }

        internal CompiledConditionComparison[] CompiledConditionComparisons { get; }

        internal CompiledPropertyExternalRead[] CompiledConditionPropertyReads { get; }

        internal CompiledConditionValuePart[] CompiledConditionValueParts { get; }

        internal ImportGroupTemplate[] ImportGroups { get; }

        internal ImportTemplate[] Imports { get; }

        internal ChooseTemplate[] Chooses { get; }

        internal ChooseArmTemplate[] ChooseArms { get; }

        internal ItemGroupTemplate[] ItemGroups { get; }

        internal ItemTemplate[] Items { get; }

        internal ItemDefinitionGroupTemplate[] ItemDefinitionGroups { get; }

        internal ItemDefinitionTemplate[] ItemDefinitions { get; }

        internal MetadataTemplate[] Metadata { get; }

        internal UsingTaskTemplate[] UsingTasks { get; }

        internal TargetTemplate[] Targets { get; }

        internal ExpressionCallSite[] ExpressionCallSites { get; }

        internal ConditionCallSite[] ConditionCallSites { get; }

        internal ImmutableArray<PropertyAssignmentOperation> PropertyAssignments { get; }

        internal ImmutableArray<ConditionOperation> PropertyGroupConditionOperations { get; }

        internal string GetExpressionValue(int expressionId)
        {
            if (expressionId == 0)
            {
                return string.Empty;
            }

            ExpressionCallSite callSite = ExpressionCallSites[expressionId];
            return _strings[callSite.ExpressionStringId];
        }

        internal string GetConditionValue(int conditionId)
        {
            if (conditionId == 0)
            {
                return string.Empty;
            }

            ConditionCallSite callSite = ConditionCallSites[conditionId];
            return _strings[callSite.ConditionStringId];
        }

        internal ProjectElement GetSource(int sourceId) => _sources[sourceId];

        internal string GetStringValue(int stringId) => _strings[stringId];

        internal PropertyDelta GetConstantPropertyDelta(TableRange properties)
        {
            long key = ((long)properties.Start << 32) |
                       (uint)properties.Count;
            return _constantPropertyDeltas.GetOrAdd(
                key,
                _ => CreateConstantPropertyDelta(properties));
        }

        internal PropertyDelta CreateConstantPropertyDelta(
            TableRange properties)
        {
            int entryCount = 0;
            for (int offset = 0; offset < properties.Count; offset++)
            {
                if (!Properties[properties.Start + offset].IsDeadStore)
                {
                    entryCount++;
                }
            }

            var entries = new PropertyDeltaEntry[entryCount];
            int entryIndex = 0;
            for (int offset = 0; offset < properties.Count; offset++)
            {
                PropertyTemplate property =
                    Properties[properties.Start + offset];
                if (property.IsDeadStore)
                {
                    continue;
                }

                entries[entryIndex++] = new PropertyDeltaEntry(
                    property.PropertyId,
                    GetStringValue(property.NameStringId),
                    new PropertyValueRef(
                        GetStringValue(property.ConstantValueStringId),
                        new SourceId(Handle, property.SourceId),
                        PropertyFlags.None));
            }

            return new PropertyDelta(entries);
        }

        internal PropertyAssignmentOperation GetPropertyAssignment(
            ProjectPropertyElement element)
        {
            if (_propertyAssignments.TryGetValue(element, out PropertyAssignmentOperation operation))
            {
                return operation;
            }

            throw new InvalidOperationException(
                "The project XML changed after its evaluation module was lowered.");
        }

        internal ConditionOperation GetPropertyGroupCondition(
            ProjectPropertyGroupElement element)
        {
            if (_propertyGroupConditions.TryGetValue(
                    element,
                    out ConditionOperation operation))
            {
                return operation;
            }

            throw new InvalidOperationException(
                "The project XML changed after its evaluation module was lowered.");
        }

        internal static bool TryCreate(
            ProjectRootElement source,
            PropertyIdentityTable propertyIdentities,
            out EvaluationModule module)
        {
            int version = source.Version;
            using var measurement =
                EvaluationPerformanceInstrumentation.Measure(
                    EvaluationPerformanceMetric.ModuleLowering);
            module = new Builder(
                source,
                version,
                propertyIdentities).Build();
            if (source.Version != version)
            {
                module = null;
                return false;
            }

            return true;
        }

        internal void AssignHandle(int handle)
        {
            if (handle <= 0 ||
                Interlocked.CompareExchange(ref _handle, handle, 0) != 0)
            {
                throw new InvalidOperationException(
                    "An evaluation module handle can only be assigned once.");
            }
        }

        private sealed class Builder
        {
            private readonly ProjectRootElement _source;
            private readonly int _version;
            private readonly PropertyIdentityTable _propertyIdentities;
            private readonly List<ModuleElement> _elements = new List<ModuleElement>();
            private readonly List<PropertyGroupTemplate> _propertyGroups =
                new List<PropertyGroupTemplate>();
            private readonly List<PropertySegmentTemplate> _propertySegments =
                new List<PropertySegmentTemplate>();
            private readonly List<PropertyTemplate> _properties =
                new List<PropertyTemplate>();
            private readonly List<CompiledPropertyValuePart>
                _compiledPropertyValueParts =
                    new List<CompiledPropertyValuePart>();
            private readonly List<CompiledPropertyFunction>
                _compiledPropertyFunctions =
                    new List<CompiledPropertyFunction>();
            private readonly List<CompiledPropertyFunctionArgument>
                _compiledPropertyFunctionArguments =
                    new List<CompiledPropertyFunctionArgument>();
            private readonly List<PropertyInstruction>
                _propertyInstructions =
                    new List<PropertyInstruction>();
            private readonly List<CompiledPropertyExternalRead>
                _compiledPropertyExternalReads =
                    new List<CompiledPropertyExternalRead>();
            private readonly List<CompiledCondition> _compiledConditions =
                new List<CompiledCondition> { default };
            private readonly List<CompiledConditionInstruction>
                _compiledConditionInstructions =
                    new List<CompiledConditionInstruction>();
            private readonly List<CompiledConditionComparison>
                _compiledConditionComparisons =
                    new List<CompiledConditionComparison>();
            private readonly List<CompiledPropertyExternalRead>
                _compiledConditionPropertyReads =
                    new List<CompiledPropertyExternalRead>();
            private readonly List<CompiledConditionValuePart>
                _compiledConditionValueParts =
                    new List<CompiledConditionValuePart>();
            private readonly List<ImportGroupTemplate> _importGroups =
                new List<ImportGroupTemplate>();
            private readonly List<ImportTemplate> _imports =
                new List<ImportTemplate>();
            private readonly List<ChooseTemplate> _chooses =
                new List<ChooseTemplate>();
            private readonly List<ChooseArmTemplate> _chooseArms =
                new List<ChooseArmTemplate>();
            private readonly List<ItemGroupTemplate> _itemGroups =
                new List<ItemGroupTemplate>();
            private readonly List<ItemTemplate> _items =
                new List<ItemTemplate>();
            private readonly List<ItemDefinitionGroupTemplate> _itemDefinitionGroups =
                new List<ItemDefinitionGroupTemplate>();
            private readonly List<ItemDefinitionTemplate> _itemDefinitions =
                new List<ItemDefinitionTemplate>();
            private readonly List<MetadataTemplate> _metadata =
                new List<MetadataTemplate>();
            private readonly List<UsingTaskTemplate> _usingTasks =
                new List<UsingTaskTemplate>();
            private readonly List<TargetTemplate> _targets =
                new List<TargetTemplate>();
            private readonly List<ExpressionCallSite> _expressions =
                new List<ExpressionCallSite> { default };
            private readonly List<ConditionCallSite> _conditions =
                new List<ConditionCallSite> { default };
            private readonly List<string> _strings = new List<string> { null };
            private readonly Dictionary<string, int> _stringIds =
                new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<ProjectElement> _sources =
                new List<ProjectElement> { null };
            private readonly ImmutableArray<PropertyAssignmentOperation>.Builder
                _propertyAssignments =
                ImmutableArray.CreateBuilder<PropertyAssignmentOperation>();
            private readonly ImmutableDictionary<
                ProjectPropertyElement,
                PropertyAssignmentOperation>.Builder _propertyAssignmentsByElement =
                ImmutableDictionary.CreateBuilder<
                    ProjectPropertyElement,
                    PropertyAssignmentOperation>();
            private readonly ImmutableDictionary<
                ProjectPropertyGroupElement,
                ConditionOperation>.Builder _propertyGroupConditions =
                ImmutableDictionary.CreateBuilder<
                    ProjectPropertyGroupElement,
                    ConditionOperation>();
            private readonly ImmutableArray<ConditionOperation>.Builder
                _propertyGroupConditionOperations =
                ImmutableArray.CreateBuilder<ConditionOperation>();
            private bool _supportsReturns;

            internal Builder(
                ProjectRootElement source,
                int version,
                PropertyIdentityTable propertyIdentities)
            {
                _source = source;
                _version = version;
                _propertyIdentities = propertyIdentities;
            }

            internal EvaluationModule Build()
            {
                int rootSourceId = AddSource(_source);
                int initialTargetsExpressionId =
                    AddExpression(_source.InitialTargets, rootSourceId);
                int defaultTargetsExpressionId =
                    AddExpression(_source.DefaultTargets, rootSourceId);
                int treatAsLocalPropertyExpressionId =
                    AddExpression(_source.TreatAsLocalProperty, rootSourceId);

                List<ProjectImportElement> implicitImports =
                    _source.GetImplicitImportNodes(_source);
                TableRange topImplicitImports = AddImplicitImports(
                    implicitImports,
                    ImplicitImportLocation.Top);
                TableRange rootElements = LowerElements(_source.ChildrenEnumerable);
                TableRange bottomImplicitImports = AddImplicitImports(
                    implicitImports,
                    ImplicitImportLocation.Bottom);

                var header = new ModuleHeader(
                    rootSourceId,
                    _source.DirectoryPath,
                    initialTargetsExpressionId,
                    defaultTargetsExpressionId,
                    treatAsLocalPropertyExpressionId,
                    topImplicitImports,
                    rootElements,
                    bottomImplicitImports,
                    _supportsReturns);

                return new EvaluationModule(
                    _source,
                    _version,
                    header,
                    _elements.ToArray(),
                    _propertyGroups.ToArray(),
                    _propertySegments.ToArray(),
                    _properties.ToArray(),
                    _compiledPropertyValueParts.ToArray(),
                    _compiledPropertyFunctions.ToArray(),
                    _compiledPropertyFunctionArguments.ToArray(),
                    _propertyInstructions.ToArray(),
                    _compiledPropertyExternalReads.ToArray(),
                    _compiledConditions.ToArray(),
                    _compiledConditionInstructions.ToArray(),
                    _compiledConditionComparisons.ToArray(),
                    _compiledConditionPropertyReads.ToArray(),
                    _compiledConditionValueParts.ToArray(),
                    _importGroups.ToArray(),
                    _imports.ToArray(),
                    _chooses.ToArray(),
                    _chooseArms.ToArray(),
                    _itemGroups.ToArray(),
                    _items.ToArray(),
                    _itemDefinitionGroups.ToArray(),
                    _itemDefinitions.ToArray(),
                    _metadata.ToArray(),
                    _usingTasks.ToArray(),
                    _targets.ToArray(),
                    _expressions.ToArray(),
                    _conditions.ToArray(),
                    _strings.ToArray(),
                    _sources.ToArray(),
                    _propertyAssignments.ToImmutable(),
                    _propertyGroupConditionOperations.ToImmutable(),
                    _propertyAssignmentsByElement.ToImmutable(),
                    _propertyGroupConditions.ToImmutable());
            }

            private TableRange AddImplicitImports(
                IEnumerable<ProjectImportElement> imports,
                ImplicitImportLocation location)
            {
                int start = _imports.Count;
                foreach (ProjectImportElement import in imports)
                {
                    if (import.ImplicitImportLocation == location)
                    {
                        AddImport(import);
                    }
                }

                return new TableRange(start, _imports.Count - start);
            }

            private TableRange LowerElements(
                IEnumerable<ProjectElement> sourceElements)
            {
                var siblings = new List<ModuleElement>();
                foreach (ProjectElement element in sourceElements)
                {
                    switch (element)
                    {
                        case ProjectPropertyGroupElement propertyGroup:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.PropertyGroup,
                                AddPropertyGroup(propertyGroup)));
                            break;
                        case ProjectItemGroupElement itemGroup:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.ItemGroup,
                                AddItemGroup(itemGroup)));
                            break;
                        case ProjectItemDefinitionGroupElement itemDefinitionGroup:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.ItemDefinitionGroup,
                                AddItemDefinitionGroup(itemDefinitionGroup)));
                            break;
                        case ProjectTargetElement target:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.Target,
                                AddTarget(target)));
                            break;
                        case ProjectImportElement import:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.Import,
                                AddImport(import)));
                            break;
                        case ProjectImportGroupElement importGroup:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.ImportGroup,
                                AddImportGroup(importGroup)));
                            break;
                        case ProjectUsingTaskElement usingTask:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.UsingTask,
                                AddUsingTask(usingTask)));
                            break;
                        case ProjectChooseElement choose:
                            siblings.Add(new ModuleElement(
                                ModuleElementKind.Choose,
                                AddChoose(choose)));
                            break;
                        case ProjectExtensionsElement:
                        case ProjectSdkElement:
                            break;
                        default:
                            InternalError.Throw("Unexpected child type");
                            break;
                    }
                }

                int start = _elements.Count;
                _elements.AddRange(siblings);
                return new TableRange(start, siblings.Count);
            }

            private int AddPropertyGroup(ProjectPropertyGroupElement propertyGroup)
            {
                int sourceId = AddSource(propertyGroup);
                int conditionId = AddCondition(propertyGroup.Condition, sourceId);
                int compiledConditionId = AddCompiledCondition(
                    propertyGroup.Condition,
                    sourceId);
                int propertyStart = _properties.Count;
                int segmentStart = _propertySegments.Count;
                int currentSegmentStart = propertyStart;
                int currentExternalReadStart =
                    _compiledPropertyExternalReads.Count;
                PropertySegmentKind? currentSegmentKind = null;
                var lastCompiledAssignments =
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
                var observedCompiledAssignments = new HashSet<int>();
                var externalReads =
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (ProjectPropertyElement property in propertyGroup.Properties)
                {
                    int propertySourceId = AddSource(property);
                    int propertyConditionId = AddCondition(
                        property.Condition,
                        propertySourceId);
                    int compiledPropertyConditionId =
                        AddCompiledCondition(
                            property.Condition,
                            propertySourceId);
                    int constantValueStringId = 0;
                    List<CompiledPropertyValuePart> compiledValueParts = null;
                    List<PendingCompiledPropertyFunction>
                        compiledFunctions = null;
                    List<int> referencedAssignments = null;
                    bool requiresExpansion = false;
                    bool isCompiledEffect =
                        compiledPropertyConditionId >= 0;
                    if (isCompiledEffect &&
                        !TryCompilePropertyValue(
                            property.Value,
                            lastCompiledAssignments,
                            out constantValueStringId,
                            out compiledValueParts,
                            out compiledFunctions,
                            out referencedAssignments))
                    {
                        requiresExpansion = true;
                    }
                    PropertySegmentKind segmentKind =
                        isCompiledEffect
                            ? PropertySegmentKind.CompiledEffectBatch
                            : PropertySegmentKind.Scalar;
                    if (currentSegmentKind is not null &&
                        currentSegmentKind != segmentKind)
                    {
                        _propertySegments.Add(new PropertySegmentTemplate(
                            currentSegmentKind.Value,
                            new TableRange(
                                currentSegmentStart,
                                _properties.Count - currentSegmentStart),
                            new TableRange(
                                currentExternalReadStart,
                                _compiledPropertyExternalReads.Count -
                                currentExternalReadStart)));
                        currentSegmentStart = _properties.Count;
                        currentExternalReadStart =
                            _compiledPropertyExternalReads.Count;
                        externalReads.Clear();
                    }

                    currentSegmentKind = segmentKind;
                    TableRange compiledValuePartRange =
                        isCompiledEffect && !requiresExpansion
                            ? AddCompiledPropertyValueParts(
                                compiledValueParts,
                                compiledFunctions,
                                externalReads)
                            : default;
                    if (isCompiledEffect && !requiresExpansion)
                    {
                        if (referencedAssignments is not null)
                        {
                            foreach (int referencedAssignment
                                     in referencedAssignments)
                            {
                                observedCompiledAssignments.Add(
                                    referencedAssignment);
                            }
                        }

                        if (lastCompiledAssignments.TryGetValue(
                                property.Name,
                                out int previousAssignment) &&
                            compiledPropertyConditionId == 0 &&
                            _properties[previousAssignment]
                                .CompiledConditionId == 0 &&
                            !observedCompiledAssignments.Contains(
                                previousAssignment))
                        {
                            _properties[previousAssignment] =
                                _properties[previousAssignment].AsDeadStore();
                        }

                        lastCompiledAssignments[property.Name] =
                            _properties.Count;
                    }
                    else
                    {
                        lastCompiledAssignments.Clear();
                        observedCompiledAssignments.Clear();
                    }

                    _properties.Add(new PropertyTemplate(
                        _propertyIdentities.GetOrCreate(property.Name),
                        GetStringId(property.Name),
                        propertyConditionId,
                        compiledPropertyConditionId,
                        AddExpression(property.Value, propertySourceId),
                        constantValueStringId,
                        compiledValuePartRange,
                        requiresExpansion,
                        isDeadStore: false,
                        propertySourceId));

                    var operation = new PropertyAssignmentOperation(property);
                    _propertyAssignments.Add(operation);
                    _propertyAssignmentsByElement.Add(property, operation);
                }

                if (currentSegmentKind is not null)
                {
                    _propertySegments.Add(new PropertySegmentTemplate(
                        currentSegmentKind.Value,
                        new TableRange(
                            currentSegmentStart,
                            _properties.Count - currentSegmentStart),
                        new TableRange(
                            currentExternalReadStart,
                            _compiledPropertyExternalReads.Count -
                            currentExternalReadStart)));
                }

                for (int i = segmentStart;
                     i < _propertySegments.Count;
                     i++)
                {
                    PropertySegmentTemplate segment =
                        _propertySegments[i];
                    if (segment.Kind ==
                        PropertySegmentKind.CompiledEffectBatch)
                    {
                        _propertySegments[i] =
                            segment.WithInstructions(
                                AddPropertyInstructions(
                                    segment.Properties,
                                    out bool isConstant),
                                isConstant);
                    }
                }

                ConditionOperation conditionOperation =
                    ConditionOperation.CreateForPropertyGroup(propertyGroup);
                _propertyGroupConditionOperations.Add(conditionOperation);
                _propertyGroupConditions.Add(propertyGroup, conditionOperation);
                _propertyGroups.Add(new PropertyGroupTemplate(
                    conditionId,
                    compiledConditionId,
                    new TableRange(
                        propertyStart,
                        _properties.Count - propertyStart),
                    new TableRange(
                        segmentStart,
                        _propertySegments.Count - segmentStart),
                    sourceId));
                return _propertyGroups.Count - 1;
            }

            private int AddCompiledCondition(
                string condition,
                int sourceId,
                ParserOptions parserOptions =
                    ParserOptions.AllowProperties)
            {
                if (string.IsNullOrEmpty(condition))
                {
                    return 0;
                }

                GenericExpressionNode expression;
                try
                {
                    expression = new Parser().Parse(
                        condition,
                        parserOptions,
                        _sources[sourceId].ConditionLocation);
                }
                catch (InvalidProjectFileException)
                {
                    return -1;
                }

                if (expression.PotentialAndOrConflict())
                {
                    return -1;
                }

                int comparisonStart = _compiledConditionComparisons.Count;
                int propertyReadStart =
                    _compiledConditionPropertyReads.Count;
                int valuePartStart =
                    _compiledConditionValueParts.Count;
                var instructions =
                    new List<CompiledConditionInstruction>();
                bool compiled;
                if (expression is EqualExpressionNode or
                    NotEqualExpressionNode)
                {
                    compiled = TryAddCompiledConditionComparison(
                        expression,
                        out int comparisonId);
                    if (compiled)
                    {
                        instructions.Add(
                            new CompiledConditionInstruction(
                                CompiledConditionInstructionKind
                                    .ReturnComparison,
                                comparisonId));
                    }
                }
                else
                {
                    var falseBranches = new List<int>();
                    compiled = TryEmitBranchIfFalse(
                        expression,
                        instructions,
                        falseBranches);
                    if (compiled)
                    {
                        instructions.Add(
                            new CompiledConditionInstruction(
                                CompiledConditionInstructionKind
                                    .ReturnTrue));
                        int falseTarget = instructions.Count;
                        instructions.Add(
                            new CompiledConditionInstruction(
                                CompiledConditionInstructionKind
                                    .ReturnFalse));
                        PatchConditionBranches(
                            instructions,
                            falseBranches,
                            falseTarget);
                    }
                }

                if (!compiled)
                {
                    _compiledConditionComparisons.RemoveRange(
                        comparisonStart,
                        _compiledConditionComparisons.Count -
                        comparisonStart);
                    _compiledConditionPropertyReads.RemoveRange(
                        propertyReadStart,
                        _compiledConditionPropertyReads.Count -
                        propertyReadStart);
                    _compiledConditionValueParts.RemoveRange(
                        valuePartStart,
                        _compiledConditionValueParts.Count -
                        valuePartStart);
                    return -1;
                }

                int instructionStart =
                    _compiledConditionInstructions.Count;
                _compiledConditionInstructions.AddRange(instructions);
                _compiledConditions.Add(new CompiledCondition(
                    new TableRange(
                        instructionStart,
                        instructions.Count),
                    sourceId));
                return _compiledConditions.Count - 1;
            }

            private bool TryEmitBranchIfFalse(
                GenericExpressionNode expression,
                List<CompiledConditionInstruction> instructions,
                List<int> targetBranches)
            {
                if (expression is AndExpressionNode and)
                {
                    return TryEmitBranchIfFalse(
                            and.LeftChild,
                            instructions,
                            targetBranches) &&
                        TryEmitBranchIfFalse(
                            and.RightChild,
                            instructions,
                            targetBranches);
                }

                if (expression is OrExpressionNode or)
                {
                    var trueBranches = new List<int>();
                    if (!TryEmitBranchIfTrue(
                            or.LeftChild,
                            instructions,
                            trueBranches) ||
                        !TryEmitBranchIfFalse(
                            or.RightChild,
                            instructions,
                            targetBranches))
                    {
                        return false;
                    }

                    PatchConditionBranches(
                        instructions,
                        trueBranches,
                        instructions.Count);
                    return true;
                }

                if (!TryAddCompiledConditionComparison(
                        expression,
                        out int comparisonId))
                {
                    return false;
                }

                targetBranches.Add(instructions.Count);
                instructions.Add(new CompiledConditionInstruction(
                    CompiledConditionInstructionKind
                        .BranchIfComparisonFalse,
                    comparisonId));
                return true;
            }

            private bool TryEmitBranchIfTrue(
                GenericExpressionNode expression,
                List<CompiledConditionInstruction> instructions,
                List<int> targetBranches)
            {
                if (expression is OrExpressionNode or)
                {
                    return TryEmitBranchIfTrue(
                            or.LeftChild,
                            instructions,
                            targetBranches) &&
                        TryEmitBranchIfTrue(
                            or.RightChild,
                            instructions,
                            targetBranches);
                }

                if (expression is AndExpressionNode and)
                {
                    var falseBranches = new List<int>();
                    if (!TryEmitBranchIfFalse(
                            and.LeftChild,
                            instructions,
                            falseBranches) ||
                        !TryEmitBranchIfTrue(
                            and.RightChild,
                            instructions,
                            targetBranches))
                    {
                        return false;
                    }

                    PatchConditionBranches(
                        instructions,
                        falseBranches,
                        instructions.Count);
                    return true;
                }

                if (!TryAddCompiledConditionComparison(
                        expression,
                        out int comparisonId))
                {
                    return false;
                }

                targetBranches.Add(instructions.Count);
                instructions.Add(new CompiledConditionInstruction(
                    CompiledConditionInstructionKind
                        .BranchIfComparisonTrue,
                    comparisonId));
                return true;
            }

            private static void PatchConditionBranches(
                List<CompiledConditionInstruction> instructions,
                List<int> branches,
                int target)
            {
                foreach (int branch in branches)
                {
                    CompiledConditionInstruction instruction =
                        instructions[branch];
                    instructions[branch] =
                        new CompiledConditionInstruction(
                            instruction.Kind,
                            instruction.Argument0,
                            target - branch);
                }
            }

            private bool TryAddCompiledConditionComparison(
                GenericExpressionNode expression,
                out int comparisonId)
            {
                CompiledConditionKind kind;
                if (expression is EqualExpressionNode equal)
                {
                    kind = CompiledConditionKind.Equal;
                    expression = equal;
                }
                else if (expression is NotEqualExpressionNode notEqual)
                {
                    kind = CompiledConditionKind.NotEqual;
                    expression = notEqual;
                }
                else
                {
                    comparisonId = 0;
                    return false;
                }

                var comparison = (OperatorExpressionNode)expression;
                if (comparison.LeftChild is not StringExpressionNode left ||
                    comparison.RightChild is not StringExpressionNode right ||
                    !TryCompileConditionOperand(left, out CompiledConditionOperand leftOperand) ||
                    !TryCompileConditionOperand(right, out CompiledConditionOperand rightOperand))
                {
                    comparisonId = 0;
                    return false;
                }

                _compiledConditionComparisons.Add(
                    new CompiledConditionComparison(
                        kind,
                        leftOperand,
                        rightOperand,
                        GetStringId(left.UnexpandedValue),
                        GetStringId(right.UnexpandedValue)));
                comparisonId =
                    _compiledConditionComparisons.Count - 1;
                return true;
            }

            private bool TryCompileConditionOperand(
                StringExpressionNode operand,
                out CompiledConditionOperand compiledOperand)
            {
                string value = operand.UnexpandedValue;
                if (value.StartsWith("%(", StringComparison.Ordinal))
                {
                    int metadataEnd = 2;
                    if (ExpressionShredder.TryParseMetadataExpression(
                            value,
                            ref metadataEnd,
                            value.Length,
                            out string itemType,
                            out string metadataName) &&
                        metadataEnd == value.Length)
                    {
                        compiledOperand =
                            new CompiledConditionOperand(
                                CompiledConditionOperandKind.Metadata,
                                GetStringId(itemType ?? string.Empty),
                                GetStringId(metadataName));
                        return true;
                    }
                }

                if (ConditionEvaluator.TryGetSingleProperty(
                        value.AsSpan(),
                        0,
                        value.Length,
                        out ReadOnlySpan<char> propertyNameSpan))
                {
                    string propertyName = propertyNameSpan.ToString();
                    if (!IsValidPropertyName(propertyName) ||
                        IsContextualPropertyName(propertyName) ||
                        propertyName.Equals(
                            "MSBuildToolsVersion",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        compiledOperand = default;
                        return false;
                    }

                    int readIndex =
                        _compiledConditionPropertyReads.Count;
                    _compiledConditionPropertyReads.Add(
                        new CompiledPropertyExternalRead(
                            _propertyIdentities.GetOrCreate(
                                propertyName),
                            GetStringId(propertyName)));
                    compiledOperand = new CompiledConditionOperand(
                        CompiledConditionOperandKind.Property,
                        readIndex);
                    return true;
                }

                if (operand.IsExpandable)
                {
                    return TryCompileExpandedConditionOperand(
                        value,
                        out compiledOperand);
                }

                compiledOperand = new CompiledConditionOperand(
                    CompiledConditionOperandKind.Literal,
                    GetStringId(value));
                return true;
            }

            private bool TryCompileExpandedConditionOperand(
                string value,
                out CompiledConditionOperand compiledOperand)
            {
                if (value.Contains("@(", StringComparison.Ordinal) ||
                    value.Contains("%(", StringComparison.Ordinal))
                {
                    compiledOperand = default;
                    return false;
                }

                int partStart = _compiledConditionValueParts.Count;
                int sourceIndex = 0;
                int propertyStart = value.IndexOf(
                    "$(",
                    StringComparison.Ordinal);
                while (propertyStart >= 0)
                {
                    if (propertyStart > sourceIndex)
                    {
                        _compiledConditionValueParts.Add(
                            new CompiledConditionValuePart(
                                CompiledConditionValuePartKind.Literal,
                                GetStringId(value.Substring(
                                    sourceIndex,
                                    propertyStart - sourceIndex))));
                    }

                    int propertyEnd = value.IndexOf(
                        ')',
                        propertyStart + 2);
                    if (propertyEnd < 0)
                    {
                        _compiledConditionValueParts.RemoveRange(
                            partStart,
                            _compiledConditionValueParts.Count -
                            partStart);
                        compiledOperand = default;
                        return false;
                    }

                    string propertyName = value.Substring(
                        propertyStart + 2,
                        propertyEnd - propertyStart - 2);
                    if (!IsValidPropertyName(propertyName) ||
                        IsContextualPropertyName(propertyName) ||
                        propertyName.Equals(
                            "MSBuildToolsVersion",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _compiledConditionValueParts.RemoveRange(
                            partStart,
                            _compiledConditionValueParts.Count -
                            partStart);
                        compiledOperand = default;
                        return false;
                    }

                    int readIndex =
                        _compiledConditionPropertyReads.Count;
                    _compiledConditionPropertyReads.Add(
                        new CompiledPropertyExternalRead(
                            _propertyIdentities.GetOrCreate(
                                propertyName),
                            GetStringId(propertyName)));
                    _compiledConditionValueParts.Add(
                        new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Property,
                            readIndex));
                    sourceIndex = propertyEnd + 1;
                    propertyStart = value.IndexOf(
                        "$(",
                        sourceIndex,
                        StringComparison.Ordinal);
                }

                if (sourceIndex < value.Length)
                {
                    _compiledConditionValueParts.Add(
                        new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Literal,
                            GetStringId(value.Substring(sourceIndex))));
                }

                int partCount =
                    _compiledConditionValueParts.Count - partStart;
                if (partCount == 0)
                {
                    compiledOperand = default;
                    return false;
                }

                compiledOperand = new CompiledConditionOperand(
                    CompiledConditionOperandKind.ExpandedValue,
                    partStart,
                    partCount);
                return true;
            }

            private sealed class PendingCompiledPropertyFunction
            {
                internal PendingCompiledPropertyFunction(
                    CompiledPropertyFunctionKind kind,
                    List<CompiledPropertyValuePart> receiver,
                    List<List<CompiledPropertyValuePart>> arguments,
                    int expressionStringId)
                {
                    Kind = kind;
                    Receiver = receiver;
                    Arguments = arguments;
                    ExpressionStringId = expressionStringId;
                }

                internal CompiledPropertyFunctionKind Kind { get; }

                internal List<CompiledPropertyValuePart> Receiver { get; }

                internal List<List<CompiledPropertyValuePart>> Arguments { get; }

                internal int ExpressionStringId { get; }
            }

            private bool TryCompilePropertyValue(
                string value,
                Dictionary<string, int> locallyDefinedProperties,
                out int constantValueStringId,
                out List<CompiledPropertyValuePart> compiledValueParts,
                out List<PendingCompiledPropertyFunction> compiledFunctions,
                out List<int> referencedAssignments)
            {
                if (!value.Contains("$(", StringComparison.Ordinal))
                {
                    constantValueStringId = GetStringId(
                        FileUtilities.MaybeAdjustFilePath(value));
                    compiledValueParts = null;
                    compiledFunctions = null;
                    referencedAssignments = null;
                    return true;
                }

                compiledFunctions =
                    new List<PendingCompiledPropertyFunction>();
                referencedAssignments = new List<int>();
                if (!TryCompilePropertyValueParts(
                        value,
                        locallyDefinedProperties,
                        compiledFunctions,
                        referencedAssignments,
                        adjustLiteralPaths: true,
                        allowNonStringFunctions: true,
                        out compiledValueParts))
                {
                    constantValueStringId = 0;
                    compiledValueParts = null;
                    compiledFunctions = null;
                    referencedAssignments = null;
                    return false;
                }

                constantValueStringId = 0;
                return true;
            }

            private bool TryCompilePropertyValueParts(
                string value,
                Dictionary<string, int> locallyDefinedProperties,
                List<PendingCompiledPropertyFunction> compiledFunctions,
                List<int> referencedAssignments,
                bool adjustLiteralPaths,
                bool allowNonStringFunctions,
                out List<CompiledPropertyValuePart> parts)
            {
                parts = new List<CompiledPropertyValuePart>();
                int sourceIndex = 0;
                int propertyStart = value.IndexOf(
                    "$(",
                    StringComparison.Ordinal);
                while (propertyStart >= 0)
                {
                    AddCompiledLiteral(
                        value,
                        sourceIndex,
                        propertyStart - sourceIndex,
                        adjustLiteralPaths,
                        parts);

                    int propertyEnd =
                        FindClosingParenthesis(value, propertyStart + 2);
                    if (propertyEnd < 0)
                    {
                        parts = null;
                        return false;
                    }

                    string propertyBody = value.Substring(
                        propertyStart + 2,
                        propertyEnd - propertyStart - 2);
                    if (IsValidPropertyName(propertyBody))
                    {
                        AddCompiledPropertyReference(
                            propertyBody,
                            locallyDefinedProperties,
                            referencedAssignments,
                            parts);
                    }
                    else if (!TryCompilePropertyFunction(
                                 propertyBody,
                                 locallyDefinedProperties,
                                 compiledFunctions,
                                 referencedAssignments,
                                 allowNonStringFunctions,
                                 out int functionIndex))
                    {
                        parts = null;
                        return false;
                    }
                    else
                    {
                        parts.Add(new CompiledPropertyValuePart(
                            CompiledPropertyValuePartKind.Function,
                            functionIndex));
                    }

                    sourceIndex = propertyEnd + 1;
                    propertyStart = value.IndexOf(
                        "$(",
                        sourceIndex,
                        StringComparison.Ordinal);
                }

                AddCompiledLiteral(
                    value,
                    sourceIndex,
                    value.Length - sourceIndex,
                    adjustLiteralPaths,
                    parts);
                return true;
            }

            private void AddCompiledLiteral(
                string value,
                int start,
                int length,
                bool adjustFilePaths,
                List<CompiledPropertyValuePart> parts)
            {
                if (length == 0)
                {
                    return;
                }

                string literal = value.Substring(start, length);
                if (adjustFilePaths)
                {
                    literal = FileUtilities.MaybeAdjustFilePath(literal);
                }

                parts.Add(new CompiledPropertyValuePart(
                    CompiledPropertyValuePartKind.Literal,
                    GetStringId(literal)));
            }

            private void AddCompiledPropertyReference(
                string propertyName,
                Dictionary<string, int> locallyDefinedProperties,
                List<int> referencedAssignments,
                List<CompiledPropertyValuePart> parts)
            {
                if (IsContextualPropertyName(propertyName))
                {
                    parts.Add(new CompiledPropertyValuePart(
                        CompiledPropertyValuePartKind
                            .ContextualPropertyReference,
                        GetStringId(propertyName)));
                }
                else if (locallyDefinedProperties.TryGetValue(
                             propertyName,
                             out int referencedAssignment))
                {
                    referencedAssignments.Add(referencedAssignment);
                    parts.Add(new CompiledPropertyValuePart(
                        CompiledPropertyValuePartKind.PropertyReference,
                        referencedAssignment));
                }
                else
                {
                    parts.Add(new CompiledPropertyValuePart(
                        CompiledPropertyValuePartKind
                            .ExternalPropertyReference,
                        GetStringId(propertyName)));
                }
            }

            private bool TryCompilePropertyFunction(
                string body,
                Dictionary<string, int> locallyDefinedProperties,
                List<PendingCompiledPropertyFunction> compiledFunctions,
                List<int> referencedAssignments,
                bool allowNonStringFunctions,
                out int functionIndex)
            {
                const string intrinsicPrefix = "[MSBuild]::";
                const string pathPrefix = "[System.IO.Path]::";
                const string runtimeInformationPrefix =
                    "[System.Runtime.InteropServices.RuntimeInformation]::";
                const string versionPrefix = "[System.Version]::";
                string receiverName = null;
                string methodName;
                int argumentsStart;
                bool isIntrinsic;
                bool isPath;
                bool isRuntimeInformation;
                bool isVersion;
                if (body.StartsWith(
                        intrinsicPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    isIntrinsic = true;
                    isPath = false;
                    isRuntimeInformation = false;
                    isVersion = false;
                    int methodStart = intrinsicPrefix.Length;
                    argumentsStart = body.IndexOf('(', methodStart);
                    if (argumentsStart < 0)
                    {
                        functionIndex = 0;
                        return false;
                    }

                    methodName = body.Substring(
                            methodStart,
                            argumentsStart - methodStart)
                        .Trim();
                }
                else if (body.StartsWith(
                             pathPrefix,
                             StringComparison.OrdinalIgnoreCase))
                {
                    isIntrinsic = false;
                    isPath = true;
                    isRuntimeInformation = false;
                    isVersion = false;
                    int methodStart = pathPrefix.Length;
                    argumentsStart = body.IndexOf('(', methodStart);
                    int methodEnd = argumentsStart >= 0
                        ? argumentsStart
                        : body.Length;
                    methodName = body.Substring(
                            methodStart,
                            methodEnd - methodStart)
                        .Trim();
                }
                else if (body.StartsWith(
                             runtimeInformationPrefix,
                             StringComparison.OrdinalIgnoreCase))
                {
                    isIntrinsic = false;
                    isPath = false;
                    isRuntimeInformation = true;
                    isVersion = false;
                    methodName = body.Substring(
                            runtimeInformationPrefix.Length)
                        .Trim();
                    argumentsStart = -1;
                }
                else if (body.StartsWith(
                             versionPrefix,
                             StringComparison.OrdinalIgnoreCase))
                {
                    isIntrinsic = false;
                    isPath = false;
                    isRuntimeInformation = false;
                    isVersion = true;
                    int methodStart = versionPrefix.Length;
                    argumentsStart = body.IndexOf('(', methodStart);
                    if (argumentsStart < 0)
                    {
                        functionIndex = 0;
                        return false;
                    }

                    methodName = body.Substring(
                            methodStart,
                            argumentsStart - methodStart)
                        .Trim();
                }
                else
                {
                    isIntrinsic = false;
                    isPath = false;
                    isRuntimeInformation = false;
                    isVersion = false;
                    int receiverEnd = body.IndexOf('.');
                    if (receiverEnd <= 0)
                    {
                        functionIndex = 0;
                        return false;
                    }

                    receiverName = body.Substring(0, receiverEnd).Trim();
                    if (!IsValidPropertyName(receiverName))
                    {
                        functionIndex = 0;
                        return false;
                    }

                    int methodStart = receiverEnd + 1;
                    argumentsStart = body.IndexOf('(', methodStart);
                    if (argumentsStart < 0)
                    {
                        functionIndex = 0;
                        return false;
                    }

                    methodName = body.Substring(
                            methodStart,
                            argumentsStart - methodStart)
                        .Trim();
                }

                int argumentsEnd = argumentsStart >= 0
                    ? FindClosingParenthesis(body, argumentsStart + 1)
                    : body.Length;
                if (argumentsEnd < 0)
                {
                    functionIndex = 0;
                    return false;
                }

                string remainder = argumentsStart >= 0
                    ? body.Substring(argumentsEnd + 1).Trim()
                    : string.Empty;
                if (
                    !TryGetCompiledPropertyFunctionKind(
                        isIntrinsic,
                        isPath,
                        isRuntimeInformation,
                        isVersion,
                        methodName,
                        remainder,
                        out CompiledPropertyFunctionKind kind,
                        out int minimumArgumentCount,
                        out int maximumArgumentCount,
                        out bool returnsString))
                {
                    functionIndex = 0;
                    return false;
                }

                bool isStaticProperty =
                    kind is
                        CompiledPropertyFunctionKind
                            .PathDirectorySeparatorChar or
                        CompiledPropertyFunctionKind
                            .RuntimeInformationProcessArchitectureLowerInvariant or
                        CompiledPropertyFunctionKind
                            .RuntimeInformationRuntimeIdentifier;
                if (isStaticProperty == (argumentsStart >= 0))
                {
                    functionIndex = 0;
                    return false;
                }

                if (!allowNonStringFunctions && !returnsString)
                {
                    functionIndex = 0;
                    return false;
                }

                if (!TrySplitFunctionArguments(
                        body,
                        argumentsStart >= 0
                            ? argumentsStart + 1
                            : argumentsEnd,
                        argumentsEnd,
                        out List<string> argumentValues) ||
                    argumentValues.Count < minimumArgumentCount ||
                    argumentValues.Count > maximumArgumentCount)
                {
                    functionIndex = 0;
                    return false;
                }

                List<CompiledPropertyValuePart> receiver = null;
                if (receiverName is not null)
                {
                    receiver = new List<CompiledPropertyValuePart>(1);
                    AddCompiledPropertyReference(
                        receiverName,
                        locallyDefinedProperties,
                        referencedAssignments,
                        receiver);
                }

                var arguments =
                    new List<List<CompiledPropertyValuePart>>(
                        argumentValues.Count);
                bool allowTypedFunctionArguments =
                    kind is CompiledPropertyFunctionKind.Add or
                        CompiledPropertyFunctionKind.Subtract;
                foreach (string argumentValue in argumentValues)
                {
                    if (argumentValue is null ||
                        !TryCompilePropertyValueParts(
                            argumentValue,
                            locallyDefinedProperties,
                            compiledFunctions,
                            referencedAssignments,
                            adjustLiteralPaths: false,
                            allowNonStringFunctions:
                                allowTypedFunctionArguments,
                            out List<CompiledPropertyValuePart>
                                argumentParts))
                    {
                        functionIndex = 0;
                        return false;
                    }

                    arguments.Add(argumentParts);
                }

                functionIndex = compiledFunctions.Count;
                compiledFunctions.Add(
                    new PendingCompiledPropertyFunction(
                        kind,
                        receiver,
                        arguments,
                        GetStringId(body)));
                return true;
            }

            private static bool TryGetCompiledPropertyFunctionKind(
                bool isIntrinsic,
                bool isPath,
                bool isRuntimeInformation,
                bool isVersion,
                string methodName,
                string remainder,
                out CompiledPropertyFunctionKind kind,
                out int minimumArgumentCount,
                out int maximumArgumentCount,
                out bool returnsString)
            {
                minimumArgumentCount = 0;
                maximumArgumentCount = 0;
                returnsString = true;
                if (isIntrinsic)
                {
                    if (remainder.Length != 0)
                    {
                        kind = default;
                        return false;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.NormalizeDirectory),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.NormalizeDirectory;
                        maximumArgumentCount = int.MaxValue;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.NormalizePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.NormalizePath;
                        maximumArgumentCount = int.MaxValue;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.EnsureTrailingSlash),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .EnsureTrailingSlash;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.ValueOrDefault),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.ValueOrDefault;
                        minimumArgumentCount = 2;
                        maximumArgumentCount = 2;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.Add),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.Add;
                        minimumArgumentCount = 2;
                        maximumArgumentCount = 2;
                        returnsString = false;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.Escape),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.Escape;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .GetDirectoryNameOfFileAbove),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .GetDirectoryNameOfFileAbove;
                        minimumArgumentCount = 2;
                        maximumArgumentCount = 2;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .GetTargetFrameworkIdentifier),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .GetTargetFrameworkIdentifier;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .GetTargetFrameworkVersion),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .GetTargetFrameworkVersion;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 2;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .GetTargetPlatformIdentifier),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .GetTargetPlatformIdentifier;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .GetTargetPlatformVersion),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .GetTargetPlatformVersion;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 2;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.GetToolsDirectory32),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.GetToolsDirectory32;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions
                                .IsRunningFromVisualStudio),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .IsRunningFromVisualStudio;
                        returnsString = false;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.Subtract),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.Subtract;
                        minimumArgumentCount = 2;
                        maximumArgumentCount = 2;
                        returnsString = false;
                        return true;
                    }

                    if (methodName.Equals(
                            nameof(IntrinsicFunctions.VersionLessThan),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.VersionLessThan;
                        minimumArgumentCount = 2;
                        maximumArgumentCount = 2;
                        returnsString = false;
                        return true;
                    }

                    kind = default;
                    return false;
                }

                if (isPath)
                {
                    if (remainder.Length != 0)
                    {
                        kind = default;
                        return false;
                    }

                    if (methodName.Equals(
                            nameof(Path.Combine),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.PathCombine;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = int.MaxValue;
                    }
                    else if (methodName.Equals(
                                 nameof(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .PathDirectorySeparatorChar;
                        returnsString = false;
                    }
                    else if (methodName.Equals(
                                 nameof(Path.GetDirectoryName),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .PathGetDirectoryName;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                    }
                    else if (methodName.Equals(
                                 nameof(Path.GetFullPath),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.PathGetFullPath;
                        minimumArgumentCount = 1;
                        maximumArgumentCount = 1;
                    }
                    else
                    {
                        kind = default;
                        return false;
                    }

                    return true;
                }

                if (isRuntimeInformation)
                {
                    if (remainder.Length != 0)
                    {
                        kind = default;
                        return false;
                    }

                    if (methodName.Equals(
                            "ProcessArchitecture.ToString().ToLowerInvariant",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .RuntimeInformationProcessArchitectureLowerInvariant;
                    }
                    else if (methodName.Equals(
                                 "RuntimeIdentifier",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .RuntimeInformationRuntimeIdentifier;
                    }
                    else
                    {
                        kind = default;
                        return false;
                    }

                    return true;
                }

                if (isVersion)
                {
                    if (!methodName.Equals(
                            "Parse",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = default;
                        return false;
                    }

                    minimumArgumentCount = 1;
                    maximumArgumentCount = 1;
                    if (remainder.Equals(
                            ".Build",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.VersionBuild;
                        returnsString = false;
                        return true;
                    }

                    if (remainder.Equals(
                            ".ToString(2)",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind
                                .VersionParseToStringTwo;
                        return true;
                    }

                    kind = default;
                    return false;
                }

                if (remainder.Length != 0)
                {
                    kind = default;
                    return false;
                }

                if (methodName.Equals(
                        nameof(string.ToLower),
                        StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringToLower;
                }
                else if (methodName.Equals(
                             nameof(string.ToLowerInvariant),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind =
                        CompiledPropertyFunctionKind.StringToLowerInvariant;
                }
                else if (methodName.Equals(
                             nameof(string.ToUpper),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringToUpper;
                }
                else if (methodName.Equals(
                             nameof(string.ToUpperInvariant),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind =
                        CompiledPropertyFunctionKind.StringToUpperInvariant;
                }
                else if (methodName.Equals(
                             nameof(string.Trim),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringTrim;
                    maximumArgumentCount = 1;
                }
                else if (methodName.Equals(
                             nameof(string.TrimEnd),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringTrimEnd;
                    maximumArgumentCount = 1;
                }
                else if (methodName.Equals(
                             nameof(string.TrimStart),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringTrimStart;
                    maximumArgumentCount = 1;
                }
                else if (methodName.Equals(
                             nameof(string.LastIndexOf),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind =
                        CompiledPropertyFunctionKind.StringLastIndexOf;
                    minimumArgumentCount = 1;
                    maximumArgumentCount = 1;
                    returnsString = false;
                }
                else if (methodName.Equals(
                             nameof(string.Substring),
                             StringComparison.OrdinalIgnoreCase))
                {
                    kind = CompiledPropertyFunctionKind.StringSubstring;
                    minimumArgumentCount = 1;
                    maximumArgumentCount = 2;
                }
                else
                {
                    minimumArgumentCount =
                        methodName.Equals(
                            nameof(string.Replace),
                            StringComparison.OrdinalIgnoreCase)
                            ? 2
                            : 1;
                    maximumArgumentCount = minimumArgumentCount;
                    returnsString = methodName.Equals(
                        nameof(string.Replace),
                        StringComparison.OrdinalIgnoreCase);
                    if (methodName.Equals(
                            nameof(string.Contains),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.StringContains;
                    }
                    else if (methodName.Equals(
                                 nameof(string.EndsWith),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.StringEndsWith;
                    }
                    else if (methodName.Equals(
                                 nameof(string.Equals),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.StringEquals;
                    }
                    else if (methodName.Equals(
                                 nameof(string.Replace),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind = CompiledPropertyFunctionKind.StringReplace;
                    }
                    else if (methodName.Equals(
                                 nameof(string.StartsWith),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        kind =
                            CompiledPropertyFunctionKind.StringStartsWith;
                    }
                    else
                    {
                        kind = default;
                        return false;
                    }
                }

                return true;
            }

            private static bool TrySplitFunctionArguments(
                string expression,
                int start,
                int end,
                out List<string> arguments)
            {
                arguments = new List<string>();
                if (start == end)
                {
                    return true;
                }

                int argumentStart = start;
                for (int i = start; i < end; i++)
                {
                    char current = expression[i];
                    if (current is '\'' or '"' or '`')
                    {
                        int quoteEnd = expression.IndexOf(current, i + 1);
                        if (quoteEnd < 0 || quoteEnd >= end)
                        {
                            arguments = null;
                            return false;
                        }

                        i = quoteEnd;
                    }
                    else if (current == '$' &&
                             i + 1 < end &&
                             expression[i + 1] == '(')
                    {
                        int propertyEnd =
                            FindClosingParenthesis(expression, i + 2);
                        if (propertyEnd < 0 || propertyEnd >= end)
                        {
                            arguments = null;
                            return false;
                        }

                        i = propertyEnd;
                    }
                    else if (current == ',')
                    {
                        arguments.Add(ExtractFunctionArgument(
                            expression,
                            argumentStart,
                            i - argumentStart));
                        argumentStart = i + 1;
                    }
                }

                arguments.Add(ExtractFunctionArgument(
                    expression,
                    argumentStart,
                    end - argumentStart));
                return true;
            }

            private static string ExtractFunctionArgument(
                string expression,
                int start,
                int length)
            {
                string argument = expression.Substring(start, length).Trim();
                if (argument.Equals(
                        "null",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (argument.Length >= 2 &&
                    argument[0] == argument[argument.Length - 1] &&
                    argument[0] is '\'' or '"' or '`')
                {
                    return argument.Substring(1, argument.Length - 2);
                }

                return argument;
            }

            private static int FindClosingParenthesis(
                string expression,
                int start)
            {
                int nesting = 1;
                for (int i = start; i < expression.Length; i++)
                {
                    char current = expression[i];
                    if (current is '\'' or '"' or '`')
                    {
                        i = expression.IndexOf(current, i + 1);
                        if (i < 0)
                        {
                            return -1;
                        }
                    }
                    else if (current == '(')
                    {
                        nesting++;
                    }
                    else if (current == ')' && --nesting == 0)
                    {
                        return i;
                    }
                }

                return -1;
            }

            private TableRange AddCompiledPropertyValueParts(
                List<CompiledPropertyValuePart> parts,
                List<PendingCompiledPropertyFunction> functions,
                Dictionary<string, int> externalReads)
            {
                if (parts is null)
                {
                    return default;
                }

                int[] functionIds =
                    functions is null ? null : new int[functions.Count];
                if (functions is not null)
                {
                    for (int i = 0; i < functions.Count; i++)
                    {
                        PendingCompiledPropertyFunction function =
                            functions[i];
                        TableRange receiver = AddCompiledPropertyValuePartsCore(
                            function.Receiver,
                            functionIds,
                            externalReads);
                        int argumentStart =
                            _compiledPropertyFunctionArguments.Count;
                        foreach (List<CompiledPropertyValuePart> argument
                                 in function.Arguments)
                        {
                            _compiledPropertyFunctionArguments.Add(
                                new CompiledPropertyFunctionArgument(
                                    AddCompiledPropertyValuePartsCore(
                                        argument,
                                        functionIds,
                                        externalReads)));
                        }

                        functionIds[i] = _compiledPropertyFunctions.Count;
                        _compiledPropertyFunctions.Add(
                            new CompiledPropertyFunction(
                                function.Kind,
                                receiver,
                                new TableRange(
                                    argumentStart,
                                    function.Arguments.Count),
                                function.ExpressionStringId));
                    }
                }

                return AddCompiledPropertyValuePartsCore(
                    parts,
                    functionIds,
                    externalReads);
            }

            private TableRange AddCompiledPropertyValuePartsCore(
                List<CompiledPropertyValuePart> parts,
                int[] functionIds,
                Dictionary<string, int> externalReads)
            {
                if (parts is null)
                {
                    return default;
                }

                int start = _compiledPropertyValueParts.Count;
                foreach (CompiledPropertyValuePart part in parts)
                {
                    if (part.Kind ==
                        CompiledPropertyValuePartKind
                            .ExternalPropertyReference)
                    {
                        string propertyName = _strings[part.Value];
                        if (!externalReads.TryGetValue(
                                propertyName,
                                out int readIndex))
                        {
                            readIndex =
                                _compiledPropertyExternalReads.Count;
                            externalReads.Add(propertyName, readIndex);
                            _compiledPropertyExternalReads.Add(
                                new CompiledPropertyExternalRead(
                                    _propertyIdentities.GetOrCreate(
                                        propertyName),
                                    part.Value));
                        }

                        _compiledPropertyValueParts.Add(
                            new CompiledPropertyValuePart(
                                CompiledPropertyValuePartKind
                                    .ExternalPropertyReference,
                                readIndex));
                    }
                    else if (part.Kind ==
                             CompiledPropertyValuePartKind.Function)
                    {
                        _compiledPropertyValueParts.Add(
                            new CompiledPropertyValuePart(
                                CompiledPropertyValuePartKind.Function,
                                functionIds[part.Value]));
                    }
                    else
                    {
                        _compiledPropertyValueParts.Add(part);
                    }
                }

                return new TableRange(start, parts.Count);
            }

            private TableRange AddPropertyInstructions(
                TableRange properties,
                out bool isConstant)
            {
                int start = _propertyInstructions.Count;
                isConstant = true;
                for (int propertyIndex = properties.Start;
                     propertyIndex <
                     properties.Start + properties.Count;
                     propertyIndex++)
                {
                    PropertyTemplate property =
                        _properties[propertyIndex];
                    if (property.IsDeadStore)
                    {
                        continue;
                    }

                    if (property.CompiledConditionId > 0)
                    {
                        isConstant = false;
                        _propertyInstructions.Add(
                            new PropertyInstruction(
                                PropertyInstructionKind
                                    .BranchIfPropertyConditionFalse,
                                propertyIndex,
                                property.RequiresExpansion
                                    ? 1
                                    : 1 +
                                      property.CompiledValueParts.Count));
                    }

                    if (property.RequiresExpansion)
                    {
                        isConstant = false;
                        _propertyInstructions.Add(
                            new PropertyInstruction(
                                PropertyInstructionKind
                                    .SetExpandedValue,
                                propertyIndex));
                        continue;
                    }

                    if (property.CompiledValueParts.Count == 0)
                    {
                        _propertyInstructions.Add(
                            new PropertyInstruction(
                                PropertyInstructionKind.SetLiteral,
                                propertyIndex,
                                property.ConstantValueStringId));
                        continue;
                    }

                    isConstant = false;
                    _propertyInstructions.Add(
                        new PropertyInstruction(
                            PropertyInstructionKind.SetValue,
                            propertyIndex,
                            property.CompiledValueParts.Count));
                    for (int partIndex =
                             property.CompiledValueParts.Start;
                         partIndex <
                         property.CompiledValueParts.Start +
                         property.CompiledValueParts.Count;
                         partIndex++)
                    {
                        CompiledPropertyValuePart part =
                            _compiledPropertyValueParts[partIndex];
                        _propertyInstructions.Add(
                            new PropertyInstruction(
                                part.Kind switch
                                {
                                    CompiledPropertyValuePartKind.Literal =>
                                        PropertyInstructionKind.AppendLiteral,
                                    CompiledPropertyValuePartKind.PropertyReference =>
                                        PropertyInstructionKind.AppendLocalProperty,
                                    CompiledPropertyValuePartKind.ExternalPropertyReference =>
                                        PropertyInstructionKind.AppendExternalProperty,
                                    CompiledPropertyValuePartKind.ContextualPropertyReference =>
                                        PropertyInstructionKind.AppendContextualProperty,
                                    CompiledPropertyValuePartKind.Function =>
                                        PropertyInstructionKind.AppendFunction,
                                    _ => throw new InternalErrorException(
                                        "Unknown compiled property value part."),
                                },
                                part.Value));
                    }
                }

                return new TableRange(
                    start,
                    _propertyInstructions.Count - start);
            }

            private static bool IsValidPropertyName(string propertyName)
            {
                if (propertyName.Length == 0 ||
                    propertyName.StartsWith(
                        "Registry:",
                        StringComparison.OrdinalIgnoreCase) ||
                    !XmlUtilities.IsValidInitialElementNameCharacter(
                        propertyName[0]))
                {
                    return false;
                }

                for (int i = 1; i < propertyName.Length; i++)
                {
                    if (!XmlUtilities.IsValidSubsequentElementNameCharacter(
                            propertyName[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool IsContextualPropertyName(
                string propertyName) =>
                propertyName.Equals(
                    ReservedPropertyNames.thisFileDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals(
                    ReservedPropertyNames.thisFileDirectoryNoRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals(
                    ReservedPropertyNames.thisFile,
                    StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals(
                    ReservedPropertyNames.thisFileExtension,
                    StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals(
                    ReservedPropertyNames.thisFileFullPath,
                    StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals(
                    ReservedPropertyNames.thisFileName,
                    StringComparison.OrdinalIgnoreCase);

            private int AddImportGroup(ProjectImportGroupElement importGroup)
            {
                int sourceId = AddSource(importGroup);
                int start = _imports.Count;
                foreach (ProjectImportElement import in importGroup.Imports)
                {
                    AddImport(import);
                }

                _importGroups.Add(new ImportGroupTemplate(
                    AddCondition(importGroup.Condition, sourceId),
                    new TableRange(start, _imports.Count - start),
                    sourceId));
                return _importGroups.Count - 1;
            }

            private int AddImport(ProjectImportElement import)
            {
                int sourceId = AddSource(import);
                _imports.Add(new ImportTemplate(
                    AddExpression(import.Project, sourceId),
                    sourceId));
                return _imports.Count - 1;
            }

            private int AddChoose(ProjectChooseElement choose)
            {
                int sourceId = AddSource(choose);
                var arms = new List<ChooseArmTemplate>();
                foreach (ProjectWhenElement when in choose.WhenElements)
                {
                    int whenSourceId = AddSource(when);
                    arms.Add(new ChooseArmTemplate(
                        AddCondition(when.Condition, whenSourceId),
                        LowerElements(when.ChildrenEnumerable),
                        whenSourceId,
                        isOtherwise: false));
                }

                ProjectOtherwiseElement otherwise = choose.OtherwiseElement;
                if (otherwise is not null)
                {
                    int otherwiseSourceId = AddSource(otherwise);
                    arms.Add(new ChooseArmTemplate(
                        conditionId: 0,
                        LowerElements(otherwise.ChildrenEnumerable),
                        otherwiseSourceId,
                        isOtherwise: true));
                }

                int start = _chooseArms.Count;
                _chooseArms.AddRange(arms);
                _chooses.Add(new ChooseTemplate(
                    new TableRange(start, arms.Count),
                    sourceId));
                return _chooses.Count - 1;
            }

            private int AddTarget(ProjectTargetElement target)
            {
                _supportsReturns |= target.Returns is not null;
                int sourceId = AddSource(target);
                _targets.Add(new TargetTemplate(
                    GetStringId(target.Name),
                    AddExpression(target.BeforeTargets, sourceId),
                    AddExpression(target.AfterTargets, sourceId),
                    sourceId));
                return _targets.Count - 1;
            }

            private int AddItemGroup(ProjectItemGroupElement itemGroup)
            {
                int sourceId = AddSource(itemGroup);
                int start = _items.Count;
                foreach (ProjectItemElement item in itemGroup.Items)
                {
                    int itemSourceId = AddSource(item);
                    _items.Add(new ItemTemplate(
                        GetStringId(item.ItemType),
                        GetItemOperationKind(item),
                        AddCondition(item.Condition, itemSourceId),
                        AddExpression(item.Include, itemSourceId),
                        AddExpression(item.Exclude, itemSourceId),
                        AddExpression(item.Remove, itemSourceId),
                        AddExpression(item.Update, itemSourceId),
                        AddExpression(item.MatchOnMetadata, itemSourceId),
                        GetStringId(item.MatchOnMetadataOptions),
                        AddMetadata(item.MetadataEnumerable),
                        itemSourceId));
                }

                _itemGroups.Add(new ItemGroupTemplate(
                    AddCondition(itemGroup.Condition, sourceId),
                    new TableRange(start, _items.Count - start),
                    sourceId));
                return _itemGroups.Count - 1;
            }

            private int AddItemDefinitionGroup(
                ProjectItemDefinitionGroupElement itemDefinitionGroup)
            {
                int sourceId = AddSource(itemDefinitionGroup);
                int start = _itemDefinitions.Count;
                foreach (ProjectItemDefinitionElement itemDefinition
                         in itemDefinitionGroup.ItemDefinitions)
                {
                    int itemDefinitionSourceId = AddSource(itemDefinition);
                    _itemDefinitions.Add(new ItemDefinitionTemplate(
                        GetStringId(itemDefinition.ItemType),
                        AddCondition(
                            itemDefinition.Condition,
                            itemDefinitionSourceId),
                        AddMetadata(itemDefinition.Metadata),
                        itemDefinitionSourceId));
                }

                _itemDefinitionGroups.Add(new ItemDefinitionGroupTemplate(
                    AddCondition(itemDefinitionGroup.Condition, sourceId),
                    new TableRange(
                        start,
                        _itemDefinitions.Count - start),
                    sourceId));
                return _itemDefinitionGroups.Count - 1;
            }

            private TableRange AddMetadata(
                IEnumerable<ProjectMetadataElement> metadataElements)
            {
                int start = _metadata.Count;
                foreach (ProjectMetadataElement metadata in metadataElements)
                {
                    int sourceId = AddSource(metadata);
                    _metadata.Add(new MetadataTemplate(
                        GetStringId(metadata.Name),
                        AddCondition(metadata.Condition, sourceId),
                        AddCompiledCondition(
                            metadata.Condition,
                            sourceId,
                            ParserOptions.AllowAll),
                        AddExpression(metadata.Value, sourceId),
                        sourceId));
                }

                return new TableRange(start, _metadata.Count - start);
            }

            private static ItemOperationKind GetItemOperationKind(
                ProjectItemElement item)
            {
                if (item.IncludeLocation is not null)
                {
                    return ItemOperationKind.Include;
                }

                if (item.RemoveLocation is not null)
                {
                    return ItemOperationKind.Remove;
                }

                if (item.UpdateLocation is not null)
                {
                    return ItemOperationKind.Update;
                }

                throw new InvalidOperationException(
                    "An item operation must have Include, Remove, or Update.");
            }

            private int AddUsingTask(ProjectUsingTaskElement usingTask)
            {
                int sourceId = AddSource(usingTask);
                _usingTasks.Add(new UsingTaskTemplate(
                    AddCondition(usingTask.Condition, sourceId),
                    AddExpression(usingTask.TaskName, sourceId),
                    AddExpression(usingTask.TaskFactory, sourceId),
                    AddExpression(usingTask.AssemblyFile, sourceId),
                    AddExpression(usingTask.AssemblyName, sourceId),
                    AddExpression(usingTask.Runtime, sourceId),
                    AddExpression(usingTask.Architecture, sourceId),
                    AddExpression(usingTask.Override, sourceId),
                    sourceId));
                return _usingTasks.Count - 1;
            }

            private int AddExpression(string expression, int sourceId)
            {
                if (string.IsNullOrEmpty(expression))
                {
                    return 0;
                }

                _expressions.Add(new ExpressionCallSite(
                    GetStringId(expression),
                    sourceId));
                return _expressions.Count - 1;
            }

            private int AddCondition(string condition, int sourceId)
            {
                if (string.IsNullOrEmpty(condition))
                {
                    return 0;
                }

                _conditions.Add(new ConditionCallSite(
                    GetStringId(condition),
                    sourceId));
                return _conditions.Count - 1;
            }

            private int AddSource(ProjectElement source)
            {
                _sources.Add(source);
                return _sources.Count - 1;
            }

            private int GetStringId(string value)
            {
                if (value is null)
                {
                    return 0;
                }

                if (_stringIds.TryGetValue(value, out int id))
                {
                    return id;
                }

                _strings.Add(value);
                id = _strings.Count - 1;
                _stringIds.Add(value, id);
                return id;
            }
        }
    }

    internal sealed class PropertyAssignmentOperation
    {
        internal PropertyAssignmentOperation(ProjectPropertyElement source)
        {
            Source = source;
            Id = EvaluationOperationId.Create(
                source,
                "PropertyAssignment",
                source.Name);
        }

        internal ProjectPropertyElement Source { get; }

        internal EvaluationOperationId Id { get; }

        internal bool SupportsReplay(ProjectEvaluationMode evaluationMode) =>
            EvaluationReplayEligibility.SupportsCondition(
                Source.Condition,
                evaluationMode) &&
            EvaluationReplayEligibility.SupportsPropertyValue(
                Source.Value,
                evaluationMode);
    }

    internal sealed class ConditionOperation
    {
        private ConditionOperation(
            ProjectElement source,
            EvaluationOperationId id)
        {
            Source = source;
            Id = id;
        }

        internal ProjectElement Source { get; }

        internal EvaluationOperationId Id { get; }

        internal bool SupportsReplay(ProjectEvaluationMode evaluationMode) =>
            EvaluationReplayEligibility.SupportsCondition(
                Source.Condition,
                evaluationMode);

        internal static ConditionOperation CreateForPropertyGroup(
            ProjectPropertyGroupElement source)
        {
            return new ConditionOperation(
                source,
                EvaluationOperationId.Create(
                    source,
                    "PropertyGroupCondition",
                    location: source.ConditionLocation));
        }
    }

    internal static class EvaluationReplayEligibility
    {
        internal static bool SupportsCondition(
            string condition,
            ProjectEvaluationMode evaluationMode)
        {
            return condition?.IndexOf(
                       "Exists",
                       StringComparison.OrdinalIgnoreCase) < 0 &&
                SupportsExpansion(condition, evaluationMode);
        }

        internal static bool SupportsPropertyValue(
            string value,
            ProjectEvaluationMode evaluationMode) =>
            SupportsExpansion(value, evaluationMode);

        private static bool SupportsExpansion(
            string expression,
            ProjectEvaluationMode evaluationMode)
        {
            if (evaluationMode == ProjectEvaluationMode.Pure ||
                string.IsNullOrEmpty(expression))
            {
                return true;
            }

            // Classic evaluation permits ambient static and registry property
            // functions whose results are not represented by property reads.
            for (int propertyStart = expression.IndexOf(
                     "$(",
                     StringComparison.Ordinal);
                 propertyStart >= 0;
                 propertyStart = expression.IndexOf(
                     "$(",
                     propertyStart + 2,
                     StringComparison.Ordinal))
            {
                int bodyStart = propertyStart + 2;
                while (bodyStart < expression.Length &&
                       char.IsWhiteSpace(expression[bodyStart]))
                {
                    bodyStart++;
                }

                if (bodyStart < expression.Length &&
                    expression[bodyStart] == '[')
                {
                    return false;
                }

                const string registryPrefix = "Registry:";
                if (bodyStart + registryPrefix.Length <= expression.Length &&
                    expression.IndexOf(
                        registryPrefix,
                        bodyStart,
                        registryPrefix.Length,
                        StringComparison.OrdinalIgnoreCase) == bodyStart)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class PropertyAssignmentReplayCache
    {
        private readonly ConcurrentDictionary<EvaluationOperationId, CacheEntry> _entries =
            new ConcurrentDictionary<EvaluationOperationId, CacheEntry>();
        private long _hits;
        private long _misses;
        private long _publicationContentions;
        private long _publishedVariants;

        internal bool TryFind(
            EvaluationOperationId operation,
            Func<string, string> readProperty,
            out PropertyAssignmentVariant variant)
        {
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            foreach (PropertyAssignmentVariant candidate in entry.Snapshot)
            {
                if (candidate.Matches(readProperty))
                {
                    Interlocked.Increment(ref _hits);
                    EvaluationPerformanceInstrumentation.RecordEvent(
                        EvaluationPerformanceMetric.PropertyReplayCacheHit);
                    variant = candidate;
                    return true;
                }
            }

            Interlocked.Increment(ref _misses);
            EvaluationPerformanceInstrumentation.RecordEvent(
                EvaluationPerformanceMetric.PropertyReplayCacheMiss);
            variant = null;
            return false;
        }

        internal PropertyAssignmentVariant Publish(
            EvaluationOperationId operation,
            IReadOnlyDictionary<string, string> propertyReads,
            bool assigned,
            string evaluatedValueEscaped,
            ConditionedPropertiesDelta conditionedProperties)
        {
            var observations =
                ImmutableArray.CreateBuilder<PropertyObservation>(
                    propertyReads.Count);
            foreach (KeyValuePair<string, string> read in propertyReads)
            {
                observations.Add(new PropertyObservation(read.Key, read.Value));
            }

            observations.Sort(PropertyObservationComparer.Instance);
            var candidate = new PropertyAssignmentVariant(
                observations.MoveToImmutable(),
                assigned,
                evaluatedValueEscaped,
                conditionedProperties);
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            PropertyAssignmentVariant published = entry.Publish(
                candidate,
                out bool added);

            if (added)
            {
                Interlocked.Increment(ref _publishedVariants);
            }

            if (!added)
            {
                Interlocked.Increment(ref _publicationContentions);
                EvaluationPerformanceInstrumentation.RecordEvent(
                    EvaluationPerformanceMetric.PropertyReplayCacheContention);
            }

            return published;
        }

        internal EvaluationReplayCacheMetrics GetMetrics() =>
            new EvaluationReplayCacheMetrics(
                Interlocked.Read(ref _hits),
                Interlocked.Read(ref _misses),
                Interlocked.Read(ref _publicationContentions),
                Interlocked.Read(ref _publishedVariants));

        internal sealed class CacheEntry
        {
            private PropertyAssignmentVariant[] _variants =
                Array.Empty<PropertyAssignmentVariant>();

            internal PropertyAssignmentVariant[] Snapshot =>
                Volatile.Read(ref _variants);

            internal PropertyAssignmentVariant Publish(
                PropertyAssignmentVariant candidate,
                out bool added)
            {
                while (true)
                {
                    PropertyAssignmentVariant[] snapshot =
                        Volatile.Read(ref _variants);
                    foreach (PropertyAssignmentVariant existing in snapshot)
                    {
                        if (existing.HasSameInputs(candidate))
                        {
                            added = false;
                            return existing;
                        }
                    }

                    var updated =
                        new PropertyAssignmentVariant[snapshot.Length + 1];
                    Array.Copy(snapshot, updated, snapshot.Length);
                    updated[snapshot.Length] = candidate;
                    if (ReferenceEquals(
                            Interlocked.CompareExchange(
                                ref _variants,
                                updated,
                                snapshot),
                            snapshot))
                    {
                        added = true;
                        return candidate;
                    }
                }
            }
        }
    }

    internal sealed class PropertyAssignmentVariant
    {
        private readonly IReadOnlyDictionary<string, string> _dependencyValues;

        internal PropertyAssignmentVariant(
            ImmutableArray<PropertyObservation> dependencies,
            bool assigned,
            string evaluatedValueEscaped,
            ConditionedPropertiesDelta conditionedProperties)
        {
            Dependencies = dependencies;
            Assigned = assigned;
            EvaluatedValueEscaped = evaluatedValueEscaped;
            ConditionedProperties = conditionedProperties;
            var dependencyValues = new Dictionary<string, string>(
                dependencies.Length,
                StringComparer.OrdinalIgnoreCase);
            foreach (PropertyObservation dependency in dependencies)
            {
                dependencyValues.Add(dependency.Name, dependency.Value);
            }

            _dependencyValues = dependencyValues;
        }

        internal ImmutableArray<PropertyObservation> Dependencies { get; }

        internal bool Assigned { get; }

        internal string EvaluatedValueEscaped { get; }

        internal ConditionedPropertiesDelta ConditionedProperties { get; }

        internal bool Matches(Func<string, string> readProperty)
        {
            foreach (PropertyObservation dependency in Dependencies)
            {
                if (!StringComparer.Ordinal.Equals(
                        dependency.Value,
                        readProperty(dependency.Name)))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool HasSameInputs(PropertyAssignmentVariant other) =>
            PropertyObservationComparer.HaveSameValues(
                Dependencies,
                other.Dependencies);

        internal IReadOnlyDictionary<string, string> DependencyValues =>
            _dependencyValues;
    }

    internal sealed class ConditionReplayCache
    {
        private readonly ConcurrentDictionary<EvaluationOperationId, CacheEntry> _entries =
            new ConcurrentDictionary<EvaluationOperationId, CacheEntry>();
        private long _hits;
        private long _misses;
        private long _publicationContentions;
        private long _publishedVariants;

        internal bool TryFind(
            EvaluationOperationId operation,
            Func<string, string> readProperty,
            out ConditionVariant variant)
        {
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            foreach (ConditionVariant candidate in entry.Snapshot)
            {
                if (candidate.Matches(readProperty))
                {
                    Interlocked.Increment(ref _hits);
                    EvaluationPerformanceInstrumentation.RecordEvent(
                        EvaluationPerformanceMetric.ConditionReplayCacheHit);
                    variant = candidate;
                    return true;
                }
            }

            Interlocked.Increment(ref _misses);
            EvaluationPerformanceInstrumentation.RecordEvent(
                EvaluationPerformanceMetric.ConditionReplayCacheMiss);
            variant = null;
            return false;
        }

        internal void Publish(
            EvaluationOperationId operation,
            IReadOnlyDictionary<string, string> propertyReads,
            bool result,
            ConditionedPropertiesDelta conditionedProperties)
        {
            var observations =
                ImmutableArray.CreateBuilder<PropertyObservation>(
                    propertyReads.Count);
            foreach (KeyValuePair<string, string> read in propertyReads)
            {
                observations.Add(new PropertyObservation(read.Key, read.Value));
            }

            observations.Sort(PropertyObservationComparer.Instance);
            var candidate = new ConditionVariant(
                observations.MoveToImmutable(),
                result,
                conditionedProperties);
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            entry.Publish(
                candidate,
                out bool added);

            if (added)
            {
                Interlocked.Increment(ref _publishedVariants);
            }

            if (!added)
            {
                Interlocked.Increment(ref _publicationContentions);
                EvaluationPerformanceInstrumentation.RecordEvent(
                    EvaluationPerformanceMetric.ConditionReplayCacheContention);
            }
        }

        internal EvaluationReplayCacheMetrics GetMetrics() =>
            new EvaluationReplayCacheMetrics(
                Interlocked.Read(ref _hits),
                Interlocked.Read(ref _misses),
                Interlocked.Read(ref _publicationContentions),
                Interlocked.Read(ref _publishedVariants));

        internal sealed class CacheEntry
        {
            private ConditionVariant[] _variants =
                Array.Empty<ConditionVariant>();

            internal ConditionVariant[] Snapshot =>
                Volatile.Read(ref _variants);

            internal ConditionVariant Publish(
                ConditionVariant candidate,
                out bool added)
            {
                while (true)
                {
                    ConditionVariant[] snapshot =
                        Volatile.Read(ref _variants);
                    foreach (ConditionVariant existing in snapshot)
                    {
                        if (existing.HasSameInputs(candidate))
                        {
                            added = false;
                            return existing;
                        }
                    }

                    var updated = new ConditionVariant[snapshot.Length + 1];
                    Array.Copy(snapshot, updated, snapshot.Length);
                    updated[snapshot.Length] = candidate;
                    if (ReferenceEquals(
                            Interlocked.CompareExchange(
                                ref _variants,
                                updated,
                                snapshot),
                            snapshot))
                    {
                        added = true;
                        return candidate;
                    }
                }
            }
        }
    }

    internal sealed class ConditionVariant
    {
        private readonly IReadOnlyDictionary<string, string> _dependencyValues;

        internal ConditionVariant(
            ImmutableArray<PropertyObservation> dependencies,
            bool result,
            ConditionedPropertiesDelta conditionedProperties)
        {
            Dependencies = dependencies;
            Result = result;
            ConditionedProperties = conditionedProperties;
            var dependencyValues = new Dictionary<string, string>(
                dependencies.Length,
                StringComparer.OrdinalIgnoreCase);
            foreach (PropertyObservation dependency in dependencies)
            {
                dependencyValues.Add(dependency.Name, dependency.Value);
            }

            _dependencyValues = dependencyValues;
        }

        internal ImmutableArray<PropertyObservation> Dependencies { get; }

        internal bool Result { get; }

        internal ConditionedPropertiesDelta ConditionedProperties { get; }

        internal bool Matches(Func<string, string> readProperty)
        {
            foreach (PropertyObservation dependency in Dependencies)
            {
                if (!StringComparer.Ordinal.Equals(
                        dependency.Value,
                        readProperty(dependency.Name)))
                {
                    return false;
                }
            }

            return true;
        }

        internal bool HasSameInputs(ConditionVariant other) =>
            PropertyObservationComparer.HaveSameValues(
                Dependencies,
                other.Dependencies);

        internal IReadOnlyDictionary<string, string> DependencyValues =>
            _dependencyValues;
    }

    internal readonly struct PropertyObservation
    {
        internal PropertyObservation(string name, string value)
        {
            Name = name;
            Value = value;
        }

        internal string Name { get; }

        internal string Value { get; }
    }

    internal sealed class PropertyObservationComparer :
        IComparer<PropertyObservation>
    {
        internal static PropertyObservationComparer Instance { get; } = new();

        public int Compare(PropertyObservation x, PropertyObservation y) =>
            StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);

        internal static bool HaveSameValues(
            ImmutableArray<PropertyObservation> left,
            ImmutableArray<PropertyObservation> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        left[i].Name,
                        right[i].Name) ||
                    !StringComparer.Ordinal.Equals(
                        left[i].Value,
                        right[i].Value))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal readonly struct EvaluationReplayCacheMetrics
    {
        internal EvaluationReplayCacheMetrics(
            long hits,
            long misses,
            long publicationContentions,
            long publishedVariants)
        {
            Hits = hits;
            Misses = misses;
            PublicationContentions = publicationContentions;
            PublishedVariants = publishedVariants;
        }

        internal long Hits { get; }

        internal long Misses { get; }

        internal long PublicationContentions { get; }

        internal long PublishedVariants { get; }
    }

    internal sealed class ConditionedPropertiesDelta
    {
        internal static ConditionedPropertiesDelta Empty { get; } =
            new ConditionedPropertiesDelta(
                ImmutableArray<ConditionedPropertyValues>.Empty);

        internal ConditionedPropertiesDelta(
            ImmutableArray<ConditionedPropertyValues> values)
        {
            Values = values;
        }

        internal ImmutableArray<ConditionedPropertyValues> Values { get; }
    }

    internal readonly struct ConditionedPropertyValues
    {
        internal ConditionedPropertyValues(
            string name,
            ImmutableArray<string> values)
        {
            Name = name;
            Values = values;
        }

        internal string Name { get; }

        internal ImmutableArray<string> Values { get; }
    }
}
