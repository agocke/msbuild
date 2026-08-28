// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
#if NET
using System.Linq.Expressions;
#endif
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
using System.Runtime.Loader;
#endif
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Partially evaluates target children into source-only action templates.
    /// The residual plans are deliberately external to translated project state.
    /// </summary>
    internal sealed class CompiledTargetPlan
    {
        internal const string EnablePartialEvaluationEnvVarName =
            "MSBUILDENABLEACTIONGRAPHPARTIALEVALUATION";

        internal static bool IsPartialEvaluationEnabled =>
            Environment.GetEnvironmentVariable(EnablePartialEvaluationEnvVarName) == "1";

        private static readonly ConditionalWeakTable<ProjectInstance, PartiallyEvaluatedProject> s_projects = new();

        private readonly PartiallyEvaluatedProject _project;
        private readonly CompiledTargetSourceProgram _sourceProgram;
        private readonly CompiledTaskAction[] _taskActions;

        private CompiledTargetPlan(ProjectTargetInstance target, PartiallyEvaluatedProject project)
        {
            _project = project;
            _sourceProgram = CompiledTargetSourceProgram.GetOrCreate(target);
            _taskActions = _sourceProgram.TaskCount == 0
                ? null
                : new CompiledTaskAction[_sourceProgram.TaskCount];
        }

        internal static CompiledTargetPlan PartiallyEvaluate(ProjectInstance projectInstance, ProjectTargetInstance target)
        {
            ArgumentNullException.ThrowIfNull(projectInstance);
            ArgumentNullException.ThrowIfNull(target);

            PartiallyEvaluatedProject project =
                s_projects.GetValue(projectInstance, static _ => new PartiallyEvaluatedProject());
            return project.PartiallyEvaluate(target);
        }

        internal int ActionCount => _sourceProgram.ActionCount;

        internal bool HasCompiledCondition =>
            _sourceProgram.HasCompiledCondition;

        internal CompiledTargetSourceProgram SourceProgram =>
            _sourceProgram;

        internal CompiledTaskAction GetAction(int childIndex)
        {
            CompiledTargetSourceActionRecord sourceAction =
                _sourceProgram.GetAction(childIndex);
            if (sourceAction.TaskProgram == null)
            {
                return null;
            }

            int taskIndex = sourceAction.TaskIndex;
            CompiledTaskAction action =
                Volatile.Read(ref _taskActions[taskIndex]);
            if (action != null)
            {
                return action;
            }

            var candidate = new CompiledTaskAction(
                sourceAction.TaskProgram,
                _project.GetTaskRegistration(
                    sourceAction.TaskProgram.Name));
            return Interlocked.CompareExchange(
                ref _taskActions[taskIndex],
                candidate,
                null) ?? candidate;
        }

        internal CompiledTargetActionRecord GetActionRecord(
            int childIndex)
        {
            CompiledTargetSourceActionRecord sourceAction =
                _sourceProgram.GetAction(childIndex);
            return new CompiledTargetActionRecord(
                sourceAction.Kind,
                sourceAction.Child,
                GetAction(childIndex),
                sourceAction.PropertyGroupAction,
                sourceAction.ItemGroupAction);
        }

        internal bool TryEvaluateCondition(
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander,
            out bool result)
        {
            return _sourceProgram.TryEvaluateCondition(
                expander,
                out result);
        }

        internal async ValueTask<WorkUnitResult> ExecuteAsync(
            CompiledTargetExecutionFrame frame)
        {
            WorkUnitResultCode aggregatedTaskResult =
                WorkUnitResultCode.Success;
            WorkUnitActionCode finalActionCode =
                WorkUnitActionCode.Continue;
            WorkUnitResult lastResult = new(
                WorkUnitResultCode.Success,
                WorkUnitActionCode.Continue,
                null);

            for (int actionIndex = 0;
                 actionIndex < _sourceProgram.ActionCount &&
                 !frame.IsCancellationRequested;
                 actionIndex++)
            {
                lastResult = await frame.ExecuteAsync(
                    GetActionRecord(actionIndex));

                if (lastResult.ResultCode == WorkUnitResultCode.Failed)
                {
                    aggregatedTaskResult = WorkUnitResultCode.Failed;
                }
                else if (lastResult.ResultCode ==
                             WorkUnitResultCode.Success &&
                         aggregatedTaskResult !=
                             WorkUnitResultCode.Failed)
                {
                    aggregatedTaskResult = WorkUnitResultCode.Success;
                }

                if (lastResult.ActionCode == WorkUnitActionCode.Stop)
                {
                    finalActionCode = WorkUnitActionCode.Stop;
                    break;
                }
            }

            if (frame.IsCancellationRequested)
            {
                aggregatedTaskResult = WorkUnitResultCode.Canceled;
                finalActionCode = WorkUnitActionCode.Stop;
            }

            return new WorkUnitResult(
                aggregatedTaskResult,
                finalActionCode,
                lastResult.Exception);
        }

        private sealed class PartiallyEvaluatedProject
        {
            private readonly ConcurrentDictionary<ProjectTargetInstance, CompiledTargetPlan> _targets = new();
            private readonly ConcurrentDictionary<string, PartiallyEvaluatedTaskRegistration> _taskRegistrations =
                new(StringComparer.OrdinalIgnoreCase);

            internal CompiledTargetPlan PartiallyEvaluate(ProjectTargetInstance target) =>
                _targets.GetOrAdd(target, targetInstance => new CompiledTargetPlan(targetInstance, this));

            internal PartiallyEvaluatedTaskRegistration GetTaskRegistration(string taskName) =>
                _taskRegistrations.GetOrAdd(
                    taskName,
                    static _ => new PartiallyEvaluatedTaskRegistration());
        }
    }

    /// <summary>
    /// Source-only lowering shared by every project instance built from the same target instance.
    /// </summary>
    internal sealed class CompiledTargetSourceProgram
    {
        private static readonly ConditionalWeakTable<
            ProjectTargetInstance,
            CompiledTargetSourceProgram> s_programs = new();

        private readonly CompiledConditionProgram _condition;
        private readonly IElementLocation _conditionLocation;
        private readonly CompiledTargetSourceActionRecord[] _actions;

        private CompiledTargetSourceProgram(ProjectTargetInstance target)
        {
            if (!string.IsNullOrEmpty(target.Condition))
            {
                _condition = CompiledConditionProgram.TryCreate(
                    target.Condition,
                    target.ConditionLocation);
                _conditionLocation = target.ConditionLocation;
            }

            _actions =
                new CompiledTargetSourceActionRecord[
                    target.Children.Count];
            int taskIndex = 0;
            for (int childIndex = 0;
                 childIndex < target.Children.Count;
                 childIndex++)
            {
                ProjectTargetInstanceChild child =
                    target.Children[childIndex];
                _actions[childIndex] = child switch
                {
                    ProjectTaskInstance task =>
                        CompiledTargetSourceActionRecord.CreateTask(
                            task,
                            taskIndex++),
                    ProjectPropertyGroupTaskInstance propertyGroup =>
                        CompiledTargetSourceActionRecord.CreatePropertyGroup(
                            propertyGroup),
                    ProjectItemGroupTaskInstance itemGroup =>
                        CompiledTargetSourceActionRecord.CreateItemGroup(
                            itemGroup),
                    _ =>
                        CompiledTargetSourceActionRecord.CreateFallback(
                            child),
                };
            }

            TaskCount = taskIndex;
        }

        internal int ActionCount => _actions.Length;

        internal bool HasCompiledCondition => _condition != null;

        internal int TaskCount { get; }

        internal static CompiledTargetSourceProgram GetOrCreate(
            ProjectTargetInstance target) =>
            s_programs.GetValue(
                target,
                static targetInstance =>
                    new CompiledTargetSourceProgram(targetInstance));

        internal CompiledTargetSourceActionRecord GetAction(
            int childIndex) =>
            _actions[childIndex];

        internal bool TryEvaluateCondition(
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander,
            out bool result)
        {
            if (_condition == null)
            {
                result = false;
                return false;
            }

            result = _condition.Evaluate(
                new CompiledLookupExpressionEnvironment(expander),
                _conditionLocation);
            return true;
        }
    }

    internal enum CompiledTargetActionKind : byte
    {
        Task,
        PropertyGroup,
        ItemGroup,
        Fallback,
    }

    internal readonly struct CompiledTargetSourceActionRecord
    {
        private CompiledTargetSourceActionRecord(
            CompiledTargetActionKind kind,
            ProjectTargetInstanceChild child,
            CompiledTaskSourceProgram taskProgram,
            int taskIndex,
            CompiledPropertyGroupAction propertyGroupAction,
            CompiledItemGroupAction itemGroupAction)
        {
            Kind = kind;
            Child = child;
            TaskProgram = taskProgram;
            TaskIndex = taskIndex;
            PropertyGroupAction = propertyGroupAction;
            ItemGroupAction = itemGroupAction;
        }

        internal CompiledTargetActionKind Kind { get; }

        internal ProjectTargetInstanceChild Child { get; }

        internal CompiledTaskSourceProgram TaskProgram { get; }

        internal int TaskIndex { get; }

        internal CompiledPropertyGroupAction PropertyGroupAction { get; }

        internal CompiledItemGroupAction ItemGroupAction { get; }

        internal static CompiledTargetSourceActionRecord CreateTask(
            ProjectTaskInstance task,
            int taskIndex) =>
            new(
                CompiledTargetActionKind.Task,
                task,
                CompiledTaskSourceProgram.GetOrCreate(task),
                taskIndex,
                propertyGroupAction: null,
                itemGroupAction: null);

        internal static CompiledTargetSourceActionRecord CreatePropertyGroup(
            ProjectPropertyGroupTaskInstance propertyGroup) =>
            new(
                CompiledTargetActionKind.PropertyGroup,
                propertyGroup,
                taskProgram: null,
                taskIndex: -1,
                propertyGroupAction:
                    CompiledPropertyGroupAction.TryCreate(propertyGroup),
                itemGroupAction: null);

        internal static CompiledTargetSourceActionRecord CreateItemGroup(
            ProjectItemGroupTaskInstance itemGroup) =>
            new(
                CompiledTargetActionKind.ItemGroup,
                itemGroup,
                taskProgram: null,
                taskIndex: -1,
                propertyGroupAction: null,
                itemGroupAction:
                    CompiledItemGroupAction.TryCreate(itemGroup));

        internal static CompiledTargetSourceActionRecord CreateFallback(
            ProjectTargetInstanceChild child) =>
            new(
                CompiledTargetActionKind.Fallback,
                child,
                taskProgram: null,
                taskIndex: -1,
                propertyGroupAction: null,
                itemGroupAction: null);
    }

    /// <summary>
    /// Ordered residual action for one target child.
    /// </summary>
    internal readonly struct CompiledTargetActionRecord
    {
        internal CompiledTargetActionRecord(
            CompiledTargetActionKind kind,
            ProjectTargetInstanceChild child,
            CompiledTaskAction taskAction,
            CompiledPropertyGroupAction propertyGroupAction,
            CompiledItemGroupAction itemGroupAction)
        {
            Kind = kind;
            Child = child;
            TaskAction = taskAction;
            PropertyGroupAction = propertyGroupAction;
            ItemGroupAction = itemGroupAction;
        }

        internal CompiledTargetActionKind Kind { get; }

        internal ProjectTargetInstanceChild Child { get; }

        internal CompiledTaskAction TaskAction { get; }

        internal CompiledPropertyGroupAction PropertyGroupAction { get; }

        internal CompiledItemGroupAction ItemGroupAction { get; }
    }

    internal sealed class CompiledPropertyGroupAction
    {
        private readonly CompiledConditionProgram _condition;
        private readonly IElementLocation _conditionLocation;
        private readonly CompiledPropertyAssignment[] _assignments;

        private CompiledPropertyGroupAction(
            CompiledConditionProgram condition,
            IElementLocation conditionLocation,
            CompiledPropertyAssignment[] assignments)
        {
            _condition = condition;
            _conditionLocation = conditionLocation;
            _assignments = assignments;
        }

        internal int AssignmentCount => _assignments.Length;

        internal static CompiledPropertyGroupAction TryCreate(
            ProjectPropertyGroupTaskInstance propertyGroup)
        {
            CompiledConditionProgram condition = null;
            if (!string.IsNullOrEmpty(propertyGroup.Condition))
            {
                condition = CompiledConditionProgram.TryCreate(
                    propertyGroup.Condition,
                    propertyGroup.ConditionLocation);
                if (condition == null)
                {
                    return null;
                }
            }

            var assignments =
                new CompiledPropertyAssignment[
                    propertyGroup.Properties.Count];
            int assignmentIndex = 0;
            foreach (ProjectPropertyGroupTaskPropertyInstance property
                in propertyGroup.Properties)
            {
                if (ReservedPropertyNames.IsReservedProperty(property.Name))
                {
                    return null;
                }

                CompiledConditionProgram propertyCondition = null;
                if (!string.IsNullOrEmpty(property.Condition))
                {
                    propertyCondition = CompiledConditionProgram.TryCreate(
                        property.Condition,
                        property.ConditionLocation);
                    if (propertyCondition == null)
                    {
                        return null;
                    }
                }

                CompiledScalarProgram value =
                    CompiledScalarProgram.TryCreate(property.Value);
                if (value == null)
                {
                    return null;
                }

                assignments[assignmentIndex++] =
                    new CompiledPropertyAssignment(
                        property,
                        propertyCondition,
                        value);
            }

            return new CompiledPropertyGroupAction(
                condition,
                propertyGroup.ConditionLocation,
                assignments);
        }

        internal bool EvaluateCondition(
            ICompiledExpressionEnvironment environment) =>
            _condition?.Evaluate(environment, _conditionLocation) ?? true;

        internal CompiledPropertyAssignment GetAssignment(int index) =>
            _assignments[index];
    }

    internal readonly struct CompiledPropertyAssignment
    {
        internal CompiledPropertyAssignment(
            ProjectPropertyGroupTaskPropertyInstance property,
            CompiledConditionProgram condition,
            CompiledScalarProgram value)
        {
            Property = property;
            Condition = condition;
            Value = value;
        }

        internal ProjectPropertyGroupTaskPropertyInstance Property { get; }

        internal CompiledConditionProgram Condition { get; }

        internal CompiledScalarProgram Value { get; }
    }

    internal enum CompiledItemOperationKind : byte
    {
        Include,
        Remove,
        Modify,
    }

    internal sealed class CompiledItemGroupAction
    {
        private readonly CompiledConditionProgram _condition;
        private readonly IElementLocation _conditionLocation;
        private readonly CompiledItemOperation[] _operations;

        private CompiledItemGroupAction(
            CompiledConditionProgram condition,
            IElementLocation conditionLocation,
            CompiledItemOperation[] operations)
        {
            _condition = condition;
            _conditionLocation = conditionLocation;
            _operations = operations;
        }

        internal int OperationCount => _operations.Length;

        internal static CompiledItemGroupAction TryCreate(
            ProjectItemGroupTaskInstance itemGroup)
        {
            CompiledConditionProgram condition = null;
            if (!string.IsNullOrEmpty(itemGroup.Condition))
            {
                condition = CompiledConditionProgram.TryCreate(
                    itemGroup.Condition,
                    itemGroup.ConditionLocation);
                if (condition == null)
                {
                    return null;
                }
            }

            var operations =
                new CompiledItemOperation[itemGroup.Items.Count];
            int operationIndex = 0;
            foreach (ProjectItemGroupTaskItemInstance item
                in itemGroup.Items)
            {
                CompiledItemOperation operation =
                    CompiledItemOperation.TryCreate(item);
                if (operation == null)
                {
                    return null;
                }

                operations[operationIndex++] = operation;
            }

            return new CompiledItemGroupAction(
                condition,
                itemGroup.ConditionLocation,
                operations);
        }

        internal bool EvaluateCondition(
            ICompiledExpressionEnvironment environment) =>
            _condition?.EvaluateForItemGroup(
                environment,
                _conditionLocation) ?? true;

        internal CompiledItemOperation GetOperation(int index) =>
            _operations[index];
    }

    internal sealed class CompiledItemOperation
    {
        private CompiledItemOperation(
            ProjectItemGroupTaskItemInstance item,
            CompiledItemOperationKind kind,
            CompiledConditionProgram condition,
            CompiledScalarProgram include,
            CompiledScalarProgram exclude,
            CompiledScalarProgram remove,
            CompiledConditionProgram keepDuplicates,
            CompiledScalarProgram keepMetadata,
            CompiledScalarProgram removeMetadata,
            CompiledScalarProgram matchOnMetadata,
            MatchOnMetadataOptions matchOnMetadataOptions,
            CompiledItemMetadataAssignment[] metadata)
        {
            Item = item;
            Kind = kind;
            Condition = condition;
            Include = include;
            Exclude = exclude;
            Remove = remove;
            KeepDuplicates = keepDuplicates;
            KeepMetadata = keepMetadata;
            RemoveMetadata = removeMetadata;
            MatchOnMetadata = matchOnMetadata;
            MatchOnMetadataOptions = matchOnMetadataOptions;
            Metadata = metadata;
        }

        internal ProjectItemGroupTaskItemInstance Item { get; }

        internal CompiledItemOperationKind Kind { get; }

        internal CompiledConditionProgram Condition { get; }

        internal CompiledScalarProgram Include { get; }

        internal CompiledScalarProgram Exclude { get; }

        internal CompiledScalarProgram Remove { get; }

        internal CompiledConditionProgram KeepDuplicates { get; }

        internal CompiledScalarProgram KeepMetadata { get; }

        internal CompiledScalarProgram RemoveMetadata { get; }

        internal CompiledScalarProgram MatchOnMetadata { get; }

        internal MatchOnMetadataOptions MatchOnMetadataOptions { get; }

        internal CompiledItemMetadataAssignment[] Metadata { get; }

        internal static CompiledItemOperation TryCreate(
            ProjectItemGroupTaskItemInstance item)
        {
            CompiledConditionProgram condition = null;
            if (!string.IsNullOrEmpty(item.Condition))
            {
                condition = CompiledConditionProgram.TryCreate(
                    item.Condition,
                    item.ConditionLocation);
                if (condition == null)
                {
                    return null;
                }
            }

            CompiledItemOperationKind kind =
                item.Include.Length != 0 || item.Exclude.Length != 0
                    ? CompiledItemOperationKind.Include
                    : item.Remove.Length != 0
                        ? CompiledItemOperationKind.Remove
                        : CompiledItemOperationKind.Modify;

            CompiledScalarProgram include = null;
            CompiledScalarProgram exclude = null;
            CompiledScalarProgram remove = null;
            CompiledConditionProgram keepDuplicates = null;
            CompiledScalarProgram keepMetadata = null;
            CompiledScalarProgram removeMetadata = null;
            CompiledScalarProgram matchOnMetadata = null;
            MatchOnMetadataOptions matchOnMetadataOptions =
                MatchOnMetadataConstants.MatchOnMetadataOptionsDefaultValue;

            if (kind == CompiledItemOperationKind.Include)
            {
                if (!string.IsNullOrEmpty(item.MatchOnMetadata) ||
                    !string.IsNullOrEmpty(item.MatchOnMetadataOptions))
                {
                    return null;
                }

                if (!TryCompileItemSpecification(
                        item.Include,
                        allowItemVectors: true,
                        out include) ||
                    !TryCompileItemSpecification(
                        item.Exclude,
                        allowItemVectors: true,
                        out exclude) ||
                    !TryCompileItemSpecification(
                        item.KeepMetadata,
                        allowItemVectors: true,
                        out keepMetadata) ||
                    !TryCompileItemSpecification(
                        item.RemoveMetadata,
                        allowItemVectors: true,
                        out removeMetadata))
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(item.KeepDuplicates))
                {
                    keepDuplicates = CompiledConditionProgram.TryCreate(
                        item.KeepDuplicates,
                        item.KeepDuplicatesLocation);
                    if (keepDuplicates == null)
                    {
                        return null;
                    }
                }
            }
            else if (kind == CompiledItemOperationKind.Remove)
            {
                if (!string.IsNullOrEmpty(item.KeepMetadata) ||
                    !string.IsNullOrEmpty(item.RemoveMetadata) ||
                    !string.IsNullOrEmpty(item.KeepDuplicates) ||
                    (string.IsNullOrEmpty(item.MatchOnMetadata) &&
                     !string.IsNullOrEmpty(item.MatchOnMetadataOptions)))
                {
                    return null;
                }

                if (!TryCompileItemSpecification(
                        item.Remove,
                        allowItemVectors: true,
                        out remove) ||
                    !TryCompileItemSpecification(
                        item.MatchOnMetadata,
                        allowItemVectors: true,
                        out matchOnMetadata))
                {
                    return null;
                }

                if (matchOnMetadata != null)
                {
                    Enum.TryParse(
                        item.MatchOnMetadataOptions,
                        out matchOnMetadataOptions);
                }
            }
            else if (!string.IsNullOrEmpty(item.KeepDuplicates) ||
                     !string.IsNullOrEmpty(item.MatchOnMetadata) ||
                     !string.IsNullOrEmpty(item.MatchOnMetadataOptions) ||
                     !TryCompileItemSpecification(
                         item.KeepMetadata,
                         allowItemVectors: true,
                         out keepMetadata) ||
                     !TryCompileItemSpecification(
                         item.RemoveMetadata,
                         allowItemVectors: true,
                         out removeMetadata))
            {
                return null;
            }

            var metadata =
                new CompiledItemMetadataAssignment[item.Metadata.Count];
            int metadataIndex = 0;
            foreach (ProjectItemGroupTaskMetadataInstance metadataInstance
                in item.Metadata)
            {
                CompiledConditionProgram metadataCondition = null;
                if (!string.IsNullOrEmpty(metadataInstance.Condition))
                {
                    metadataCondition =
                        CompiledConditionProgram.TryCreate(
                            metadataInstance.Condition,
                            metadataInstance.ConditionLocation);
                    if (metadataCondition == null)
                    {
                        return null;
                    }
                }

                if (ExpressionShredder
                        .ContainsMetadataExpressionOutsideTransform(
                            metadataInstance.Value))
                {
                    return null;
                }

                CompiledScalarProgram value =
                    CompiledScalarProgram.TryCreateItemSpecification(
                        metadataInstance.Value);
                if (value == null)
                {
                    return null;
                }

                metadata[metadataIndex++] =
                    new CompiledItemMetadataAssignment(
                        metadataInstance,
                        metadataCondition,
                        value);
            }

            return new CompiledItemOperation(
                item,
                kind,
                condition,
                include,
                exclude,
                remove,
                keepDuplicates,
                keepMetadata,
                removeMetadata,
                matchOnMetadata,
                matchOnMetadataOptions,
                metadata);
        }

        private static bool TryCompileItemSpecification(
            string specification,
            bool allowItemVectors,
            out CompiledScalarProgram program)
        {
            program = null;
            if (string.IsNullOrEmpty(specification))
            {
                return true;
            }

            if (ExpressionShredder
                    .ContainsMetadataExpressionOutsideTransform(
                        specification) ||
                (!allowItemVectors &&
                 ExpressionShredder.ContainsItemVectorMarker(
                     specification)))
            {
                return false;
            }

            program =
                allowItemVectors
                    ? CompiledScalarProgram.TryCreateItemSpecification(
                        specification)
                    : CompiledScalarProgram.TryCreate(specification);
            return program != null;
        }
    }

    internal readonly struct CompiledItemMetadataAssignment
    {
        internal CompiledItemMetadataAssignment(
            ProjectItemGroupTaskMetadataInstance metadata,
            CompiledConditionProgram condition,
            CompiledScalarProgram value)
        {
            Metadata = metadata;
            Condition = condition;
            Value = value;
        }

        internal ProjectItemGroupTaskMetadataInstance Metadata { get; }

        internal CompiledConditionProgram Condition { get; }

        internal CompiledScalarProgram Value { get; }
    }

    internal sealed class CompiledLookupExpressionEnvironment :
        ICompiledExpressionEnvironment
    {
        private readonly Expander<
            ProjectPropertyInstance,
            ProjectItemInstance> _expander;

        internal CompiledLookupExpressionEnvironment(
            Expander<ProjectPropertyInstance, ProjectItemInstance> expander)
        {
            _expander = expander;
        }

        internal Expander<ProjectPropertyInstance, ProjectItemInstance>
            Expander => _expander;

        string ICompiledExpressionEnvironment.GetEscapedPropertyValue(
            string propertyName,
            IElementLocation location) =>
            _expander.GetEscapedPropertyValue(propertyName, location);

        string ICompiledExpressionEnvironment.ExpandItems(
            string escapedValue,
            IElementLocation location) =>
            _expander.ExpandIntoStringLeaveEscaped(
                escapedValue,
                ExpanderOptions.ExpandItems,
                location);

        void ICompiledExpressionEnvironment.EnterConditionEvaluation(
            bool oneSideIsEmpty)
        {
            _expander.PropertiesUseTracker.PropertyReadContext =
                oneSideIsEmpty
                    ? PropertyReadContext
                        .ConditionEvaluationWithOneSideEmpty
                    : PropertyReadContext.ConditionEvaluation;
        }

        void ICompiledExpressionEnvironment.LeaveConditionEvaluation() =>
            _expander.PropertiesUseTracker.ResetPropertyReadContext();
    }

    /// <summary>
    /// Project-bound action for an ordinary, in-process assembly task site.
    /// </summary>
    internal sealed class CompiledTaskAction
    {
        private readonly CompiledTaskSourceProgram _program;
        private readonly PartiallyEvaluatedTaskRegistration _registration;
        private BoundTaskAction _boundAction;

        internal CompiledTaskAction(
            CompiledTaskSourceProgram program,
            PartiallyEvaluatedTaskRegistration registration)
        {
            _program = program;
            _registration = registration;
        }

        internal CompiledTaskSourceProgram Program => _program;

        internal CompiledTaskSourceProgram Template => _program;

        internal BoundTaskAction GetBoundAction()
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            ResolvedTaskRegistration registration = _registration.Get();
            return registration == null
                ? null
                : Bind(registration);
        }

        internal FastTaskInvocation GetFastInvocation()
        {
            ResolvedTaskRegistration registration = _registration.Get();
            if (registration == null)
            {
                return default;
            }

            FastTaskAction action =
                FastTaskAction.TryGetOrCreate(_program, registration);
            return action == null
                ? default
                : new FastTaskInvocation(
                    action,
                    registration.TaskFactoryWrapper);
        }

        internal FastTaskAction GetFastAction()
        {
            ResolvedTaskRegistration registration = _registration.Get();
            return registration == null
                ? null
                : FastTaskAction.TryGetOrCreate(
                    _program,
                    registration);
        }

        internal BoundTaskAction TryBind(TaskRequirements requirements, TaskFactoryWrapper taskFactoryWrapper)
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            var registration = new ResolvedTaskRegistration(requirements, taskFactoryWrapper);
            BoundTaskAction candidate = BoundTaskAction.TryCreate(_program, registration);
            if (candidate == null)
            {
                return null;
            }

            return Bind(_registration.Publish(registration));
        }

        private BoundTaskAction Bind(ResolvedTaskRegistration registration)
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            BoundTaskAction candidate = BoundTaskAction.TryCreate(_program, registration);
            return candidate == null
                ? null
                : Interlocked.CompareExchange(ref _boundAction, candidate, null) ?? candidate;
        }

        internal bool Invoke(CompiledTaskActionFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return frame.TaskExecutionHost.SetCompiledTaskParameters(
                frame.BoundAction,
                frame.BoundAction.TypeMetadata);
        }
    }

    /// <summary>
    /// Dynamic per-batch state made available to a compiled task action.
    /// </summary>
    internal sealed class CompiledTaskActionFrame
    {
        internal CompiledTaskActionFrame(TaskExecutionHost taskExecutionHost, BoundTaskAction boundAction)
        {
            TaskExecutionHost = taskExecutionHost;
            BoundAction = boundAction;
        }

        internal TaskExecutionHost TaskExecutionHost { get; }

        internal BoundTaskAction BoundAction { get; }
    }

    /// <summary>
    /// Publication cell for the project-local registration selected for one static task identity.
    /// </summary>
    internal sealed class PartiallyEvaluatedTaskRegistration
    {
        private ResolvedTaskRegistration _registration;

        internal ResolvedTaskRegistration Get() => Volatile.Read(ref _registration);

        internal ResolvedTaskRegistration Publish(ResolvedTaskRegistration registration) =>
            Interlocked.CompareExchange(ref _registration, registration, null) ?? registration;
    }

    /// <summary>
    /// Project-local task registration selected by normal MSBuild task resolution.
    /// </summary>
    internal sealed record ResolvedTaskRegistration(
        TaskRequirements Requirements,
        TaskFactoryWrapper TaskFactoryWrapper);

    /// <summary>
    /// Project-local specialization of a source task site. It retains registration-local property wrappers,
    /// while task-type metadata and memoized setter actions remain ALC-scoped.
    /// </summary>
    internal sealed class BoundTaskAction
    {
        private BoundTaskAction(
            TaskFactoryWrapper taskFactoryWrapper,
            TaskActionTypeMetadata typeMetadata,
            BoundTaskParameter[] parameters,
            string[] requiredParameterNames,
            ulong allRequiredParameters,
            FastTaskAction fastAction)
        {
            TaskFactoryWrapper = taskFactoryWrapper;
            TypeMetadata = typeMetadata;
            Parameters = parameters;
            RequiredParameterNames = requiredParameterNames;
            AllRequiredParameters = allRequiredParameters;
            FastAction = fastAction;
        }

        internal TaskFactoryWrapper TaskFactoryWrapper { get; }

        internal TaskActionTypeMetadata TypeMetadata { get; }

        internal Type TaskType => TaskFactoryWrapper.TaskFactoryLoadedType.Type;

        internal BoundTaskParameter[] Parameters { get; }

        internal string[] RequiredParameterNames { get; }

        internal ulong AllRequiredParameters { get; }

        internal FastTaskAction FastAction { get; }

        internal static BoundTaskAction TryCreate(
            CompiledTaskSourceProgram program,
            ResolvedTaskRegistration registration)
        {
            TaskRequirements requirements = registration.Requirements;
            TaskFactoryWrapper taskFactoryWrapper = registration.TaskFactoryWrapper;
            if (!program.HasStaticCurrentProcessIdentity ||
                requirements != TaskRequirements.None ||
                taskFactoryWrapper?.TaskFactory is not AssemblyTaskFactory ||
                !taskFactoryWrapper.FactoryIdentityParameters.IsEmpty)
            {
                return null;
            }

            LoadedType loadedType = taskFactoryWrapper.TaskFactoryLoadedType;
            if (loadedType?.Type == null ||
                loadedType.LoadedViaMetadataLoadContext ||
                typeof(IGeneratedTask).IsAssignableFrom(loadedType.Type) ||
                typeof(MSBuild).IsAssignableFrom(loadedType.Type) ||
                typeof(CallTarget).IsAssignableFrom(loadedType.Type) ||
                typeof(TaskHostTask).IsAssignableFrom(loadedType.Type))
            {
                return null;
            }

#if FEATURE_APPDOMAIN
            if (loadedType.IsMarshalByRef || loadedType.HasLoadInSeparateAppDomainAttribute)
            {
                return null;
            }
#endif

            IReadOnlyDictionary<string, string> requiredParameters = taskFactoryWrapper.GetNamesOfPropertiesWithRequiredAttribute;
            if (requiredParameters.Count > 64)
            {
                return null;
            }

            TaskActionTypeMetadata metadata = TaskActionTypeMetadata.GetOrCreate(loadedType);
            var boundParameters = new BoundTaskParameter[program.Parameters.Length];
            string[] requiredParameterNames = requiredParameters.Keys.ToArray();

            for (int parameterIndex = 0; parameterIndex < program.Parameters.Length; parameterIndex++)
            {
                CompiledTaskParameterProgram parameter = program.Parameters[parameterIndex];
                int propertyIndex = FindPropertyIndex(loadedType, parameter.Name);
                if (propertyIndex < 0)
                {
                    return null;
                }

                TaskPropertyInfo property = taskFactoryWrapper.GetProperty(propertyIndex);
                TaskActionPropertyMetadata propertyMetadata = metadata.GetProperty(propertyIndex);
                if (!TaskParameterTypeVerifier.IsValidScalarInputParameter(propertyMetadata.ParameterType) &&
                    !TaskParameterTypeVerifier.IsValidVectorInputParameter(propertyMetadata.ParameterType))
                {
                    return null;
                }

                ulong requiredBit = 0;
                for (int requiredIndex = 0; requiredIndex < requiredParameterNames.Length; requiredIndex++)
                {
                    if (requiredParameterNames[requiredIndex].Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        requiredBit = 1UL << requiredIndex;
                        break;
                    }
                }

                boundParameters[parameterIndex] = new BoundTaskParameter(parameter, property, propertyIndex, requiredBit);
            }

            ulong allRequiredParameters = requiredParameterNames.Length == 64
                ? ulong.MaxValue
                : (1UL << requiredParameterNames.Length) - 1;

            FastTaskAction fastAction = metadata.GetOrCreateFastAction(
                program,
                taskFactoryWrapper,
                loadedType);

            return new BoundTaskAction(
                taskFactoryWrapper,
                metadata,
                boundParameters,
                requiredParameterNames,
                allRequiredParameters,
                fastAction);
        }

        internal static int FindPropertyIndex(LoadedType loadedType, string parameterName)
        {
            int caseInsensitiveIndex = -1;
            int caseInsensitiveMatches = 0;

            for (int i = 0; i < loadedType.Properties.Length; i++)
            {
                if (loadedType.Properties[i].Name.Equals(parameterName))
                {
                    return i;
                }

                if (loadedType.Properties[i].Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveIndex = i;
                    caseInsensitiveMatches++;
                }
            }

            return caseInsensitiveMatches == 1 ? caseInsensitiveIndex : -1;
        }
    }

    /// <summary>
    /// Prebound source parameter and its registration-local property wrapper.
    /// </summary>
    internal sealed class BoundTaskParameter
    {
        internal BoundTaskParameter(CompiledTaskParameterProgram source, TaskPropertyInfo property, int propertyIndex, ulong requiredBit)
        {
            Source = source;
            Property = property;
            PropertyIndex = propertyIndex;
            RequiredBit = requiredBit;
        }

        internal CompiledTaskParameterProgram Source { get; }

        internal TaskPropertyInfo Property { get; }

        internal int PropertyIndex { get; }

        internal ulong RequiredBit { get; }
    }

    /// <summary>
    /// Task-type metadata and memoized setter actions. Values are dependent on the task ALC so they
    /// cannot extend the lifetime of a collectible task assembly.
    /// </summary>
    internal sealed class TaskActionTypeMetadata
    {
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
        private static readonly ConditionalWeakTable<AssemblyLoadContext, TaskActionTypeMetadataCache> s_metadataByLoadContext = new();
#else
        private static readonly ConcurrentDictionary<Type, TaskActionTypeMetadata> s_metadataByType = new();
#endif

        private readonly TaskActionPropertyMetadata[] _properties;
        private readonly ConditionalWeakTable<
            CompiledTaskSourceProgram,
            FastTaskActionBinding> _fastActions = new();

        private TaskActionTypeMetadata(LoadedType loadedType)
        {
            _properties = new TaskActionPropertyMetadata[loadedType.Properties.Length];
            for (int i = 0; i < _properties.Length; i++)
            {
                ReflectableTaskPropertyInfo property = loadedType.Properties[i];
                _properties[i] = new TaskActionPropertyMetadata(
                    property.PropertyType,
                    CompileSetter(loadedType.Type, property),
                    CompileGetter(loadedType.Type, property));
            }
        }

        internal static TaskActionTypeMetadata GetOrCreate(LoadedType loadedType)
        {
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
            Type taskType = loadedType.Type;
            AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(taskType.Assembly);
            TaskActionTypeMetadataCache metadataByType =
                s_metadataByLoadContext.GetValue(
                    loadContext,
                    static _ => new TaskActionTypeMetadataCache());
            return metadataByType.GetOrCreate(taskType, loadedType);
#else
            return s_metadataByType.GetOrAdd(loadedType.Type, _ => new TaskActionTypeMetadata(loadedType));
#endif
        }

        internal TaskActionPropertyMetadata GetProperty(int propertyIndex) => _properties[propertyIndex];

        internal FastTaskAction GetOrCreateFastAction(
            CompiledTaskSourceProgram program,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType) =>
            _fastActions.GetValue(
                program,
                source => new FastTaskActionBinding(
                    FastTaskAction.TryCreate(
                        source,
                        taskFactoryWrapper,
                        loadedType,
                        this))).Action;

        private static Action<ITask, object> CompileSetter(Type taskType, ReflectableTaskPropertyInfo property)
        {
            MethodInfo setter = property.Reflection?.SetMethod;
            if (setter?.IsPublic != true ||
                setter.GetParameters().Length != 1 ||
                !taskType.IsVisible)
            {
                return null;
            }

#if NET
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                ParameterExpression task =
                    Expression.Parameter(typeof(ITask), "task");
                ParameterExpression value =
                    Expression.Parameter(typeof(object), "value");
                MethodCallExpression call = Expression.Call(
                    Expression.Convert(task, taskType),
                    setter,
                    Expression.Convert(value, property.PropertyType));
                return Expression.Lambda<Action<ITask, object>>(
                    call,
                    task,
                    value).Compile();
            }

            MethodInvoker invoker = MethodInvoker.Create(setter);
            return (task, value) => invoker.Invoke(task, value);
#else
            return null;
#endif
        }

        private static Func<ITask, object> CompileGetter(Type taskType, ReflectableTaskPropertyInfo property)
        {
            MethodInfo getter = property.Reflection?.GetMethod;
            if (getter?.IsPublic != true ||
                getter.GetParameters().Length != 0 ||
                !taskType.IsVisible)
            {
                return null;
            }

#if NET
            MethodInvoker invoker = MethodInvoker.Create(getter);
            return task => invoker.Invoke(task);
#else
            return null;
#endif
        }

        private sealed class FastTaskActionBinding
        {
            internal FastTaskActionBinding(FastTaskAction action)
            {
                Action = action;
            }

            internal FastTaskAction Action { get; }
        }

#if NET && FEATURE_ASSEMBLYLOADCONTEXT
        private sealed class TaskActionTypeMetadataCache
        {
            private readonly ConditionalWeakTable<Type, TaskActionTypeMetadata> _metadataByType = new();

            internal TaskActionTypeMetadata GetOrCreate(Type taskType, LoadedType loadedType)
            {
                if (_metadataByType.TryGetValue(taskType, out TaskActionTypeMetadata cached))
                {
                    return cached;
                }

                return _metadataByType.GetValue(taskType, _ => new TaskActionTypeMetadata(loadedType));
            }
        }
#endif
    }

    /// <summary>
    /// Immutable task-type property metadata. Registration-local logging policy is intentionally absent.
    /// </summary>
    internal sealed class TaskActionPropertyMetadata
    {
        internal TaskActionPropertyMetadata(
            Type parameterType,
            Action<ITask, object> setter,
            Func<ITask, object> getter)
        {
            ParameterType = parameterType;
            Setter = setter;
            Getter = getter;
        }

        internal Type ParameterType { get; }

        internal Action<ITask, object> Setter { get; }

        internal Func<ITask, object> Getter { get; }
    }
}
