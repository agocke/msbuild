// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Construction;

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
            TableRange properties,
            int sourceId)
        {
            ConditionId = conditionId;
            Properties = properties;
            SourceId = sourceId;
        }

        internal int ConditionId { get; }

        internal TableRange Properties { get; }

        internal int SourceId { get; }
    }

    internal readonly struct PropertyTemplate
    {
        internal PropertyTemplate(
            int nameStringId,
            int conditionId,
            int valueExpressionId,
            int sourceId)
        {
            NameStringId = nameStringId;
            ConditionId = conditionId;
            ValueExpressionId = valueExpressionId;
            SourceId = sourceId;
        }

        internal int NameStringId { get; }

        internal int ConditionId { get; }

        internal int ValueExpressionId { get; }

        internal int SourceId { get; }
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
            int valueExpressionId,
            int sourceId)
        {
            NameStringId = nameStringId;
            ConditionId = conditionId;
            ValueExpressionId = valueExpressionId;
            SourceId = sourceId;
        }

        internal int NameStringId { get; }

        internal int ConditionId { get; }

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
        private int _handle;

        private EvaluationModule(
            ProjectRootElement source,
            int version,
            ModuleHeader header,
            ModuleElement[] elements,
            PropertyGroupTemplate[] propertyGroups,
            PropertyTemplate[] properties,
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
            Properties = properties;
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

        internal PropertyTemplate[] Properties { get; }

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
            out EvaluationModule module)
        {
            int version = source.Version;
            using var measurement =
                EvaluationPerformanceInstrumentation.Measure(
                    EvaluationPerformanceMetric.ModuleLowering);
            module = new Builder(source, version).Build();
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
            private readonly List<ModuleElement> _elements = new List<ModuleElement>();
            private readonly List<PropertyGroupTemplate> _propertyGroups =
                new List<PropertyGroupTemplate>();
            private readonly List<PropertyTemplate> _properties =
                new List<PropertyTemplate>();
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

            internal Builder(ProjectRootElement source, int version)
            {
                _source = source;
                _version = version;
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
                    _properties.ToArray(),
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
                int start = _properties.Count;
                foreach (ProjectPropertyElement property in propertyGroup.Properties)
                {
                    int propertySourceId = AddSource(property);
                    int propertyConditionId = AddCondition(
                        property.Condition,
                        propertySourceId);
                    _properties.Add(new PropertyTemplate(
                        GetStringId(property.Name),
                        propertyConditionId,
                        AddExpression(property.Value, propertySourceId),
                        propertySourceId));

                    var operation = new PropertyAssignmentOperation(property);
                    _propertyAssignments.Add(operation);
                    _propertyAssignmentsByElement.Add(property, operation);
                }

                ConditionOperation conditionOperation =
                    ConditionOperation.CreateForPropertyGroup(propertyGroup);
                _propertyGroupConditionOperations.Add(conditionOperation);
                _propertyGroupConditions.Add(propertyGroup, conditionOperation);
                _propertyGroups.Add(new PropertyGroupTemplate(
                    conditionId,
                    new TableRange(start, _properties.Count - start),
                    sourceId));
                return _propertyGroups.Count - 1;
            }

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

        internal bool SupportsReplay =>
            EvaluationReplayEligibility.SupportsCondition(Source.Condition);
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

        internal bool SupportsReplay =>
            EvaluationReplayEligibility.SupportsCondition(Source.Condition);

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
        internal static bool SupportsCondition(string condition)
        {
            return condition?.IndexOf(
                "Exists",
                StringComparison.OrdinalIgnoreCase) < 0;
        }
    }

    internal sealed class PropertyAssignmentReplayCache
    {
        private readonly ConcurrentDictionary<EvaluationOperationId, CacheEntry> _entries =
            new ConcurrentDictionary<EvaluationOperationId, CacheEntry>();

        internal Lease Enter(EvaluationOperationId operation)
        {
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            return new Lease(entry);
        }

        internal sealed class Lease : IDisposable
        {
            private readonly CacheEntry _entry;
            private bool _disposed;

            internal Lease(CacheEntry entry)
            {
                _entry = entry;
                System.Threading.Monitor.Enter(_entry.Lock);
            }

            internal bool TryFind(
                Func<string, string> readProperty,
                out PropertyAssignmentVariant variant)
            {
                foreach (PropertyAssignmentVariant candidate in _entry.Variants)
                {
                    if (candidate.Matches(readProperty))
                    {
                        variant = candidate;
                        return true;
                    }
                }

                variant = null;
                return false;
            }

            internal PropertyAssignmentVariant Add(
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
                var variant = new PropertyAssignmentVariant(
                    observations.MoveToImmutable(),
                    assigned,
                    evaluatedValueEscaped,
                    conditionedProperties);
                _entry.Variants.Add(variant);
                return variant;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    System.Threading.Monitor.Exit(_entry.Lock);
                }
            }
        }

        internal sealed class CacheEntry
        {
            internal object Lock { get; } = new object();

            internal List<PropertyAssignmentVariant> Variants { get; } =
                new List<PropertyAssignmentVariant>();
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

        internal IReadOnlyDictionary<string, string> DependencyValues =>
            _dependencyValues;
    }

    internal sealed class ConditionReplayCache
    {
        private readonly ConcurrentDictionary<EvaluationOperationId, CacheEntry> _entries =
            new ConcurrentDictionary<EvaluationOperationId, CacheEntry>();

        internal Lease Enter(EvaluationOperationId operation)
        {
            CacheEntry entry = _entries.GetOrAdd(
                operation,
                static _ => new CacheEntry());
            return new Lease(entry);
        }

        internal sealed class Lease : IDisposable
        {
            private readonly CacheEntry _entry;
            private bool _disposed;

            internal Lease(CacheEntry entry)
            {
                _entry = entry;
                System.Threading.Monitor.Enter(_entry.Lock);
            }

            internal bool TryFind(
                Func<string, string> readProperty,
                out ConditionVariant variant)
            {
                foreach (ConditionVariant candidate in _entry.Variants)
                {
                    if (candidate.Matches(readProperty))
                    {
                        variant = candidate;
                        return true;
                    }
                }

                variant = null;
                return false;
            }

            internal void Add(
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
                _entry.Variants.Add(new ConditionVariant(
                    observations.MoveToImmutable(),
                    result,
                    conditionedProperties));
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    System.Threading.Monitor.Exit(_entry.Lock);
                }
            }
        }

        internal sealed class CacheEntry
        {
            internal object Lock { get; } = new object();

            internal List<ConditionVariant> Variants { get; } =
                new List<ConditionVariant>();
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
