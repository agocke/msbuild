// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using ReservedPropertyNames = Microsoft.Build.Internal.ReservedPropertyNames;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal enum HardenedTaskClassification
{
    Pure,
    DeclaredIO,
    Unaudited,
}

internal sealed class HardenedTargetValidator
{
    private static readonly HashSet<string> s_purePathMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "AltDirectorySeparatorChar",
        "ChangeExtension",
        "Combine",
        "DirectorySeparatorChar",
        "EndsInDirectorySeparator",
        "GetDirectoryName",
        "GetExtension",
        "GetFileName",
        "GetFileNameWithoutExtension",
        "GetInvalidFileNameChars",
        "GetInvalidPathChars",
        "GetPathRoot",
        "GetRelativePath",
        "HasExtension",
        "IsPathFullyQualified",
        "IsPathRooted",
        "Join",
        "PathSeparator",
        "TrimEndingDirectorySeparator",
        "VolumeSeparatorChar",
    };

    private static readonly HashSet<string> s_staticMSBuildParameters = new(
        [
            "Projects",
            "Targets",
            "Properties",
            "RemoveProperties",
            "SkipNonexistentProjects",
            "SkipNonexistentTargets",
            "ToolsVersion",
            "TargetAndPropertyListSeparators",
        ],
        MSBuildNameIgnoreCaseComparer.Default);

    private static readonly string[] s_staticMSBuildProjectMetadata =
    [
        ItemMetadataNames.PropertiesMetadataName,
        ItemMetadataNames.UndefinePropertiesMetadataName,
        ItemMetadataNames.AdditionalPropertiesMetadataName,
        "ToolsVersion",
        "SkipNonexistentProjects",
    ];

    private readonly Dictionary<string, HardenedTaskClassification> _taskClassifications;
    private readonly List<InvalidProjectFileException> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueState> _targetResults =
        new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _activeTargets = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _propertiesWithoutConcreteValues = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _targetAssignedItemTypes = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HardenedExpressionDescriptorCache _expressionDescriptors = new();
    private HardenedLookupState? _context;
    private HardenedConcreteState? _concreteState;
    private Expander<ProjectPropertyInstance, ProjectItemInstance>? _concreteExpander;

    internal HardenedTargetValidator(IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        _taskClassifications = new Dictionary<string, HardenedTaskClassification>(
            taskClassifications.Count,
            MSBuildNameIgnoreCaseComparer.Default);

        foreach (KeyValuePair<string, HardenedTaskClassification> classification in taskClassifications)
        {
            _taskClassifications.Add(classification.Key, classification.Value);
        }
    }

    internal HardenedTargetValidator()
        : this(new Dictionary<string, HardenedTaskClassification>())
    {
    }

    internal IReadOnlyList<InvalidProjectFileException> Validate(ProjectInstance project, string targetName)
        => Validate(project, [targetName]);

    internal IReadOnlyList<InvalidProjectFileException> Validate(ProjectInstance project, IEnumerable<string> targetNames)
        => Validate(
            project,
            new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith),
            targetNames);

    internal IReadOnlyList<InvalidProjectFileException> Validate(
        ProjectInstance project,
        Lookup lookup,
        IEnumerable<string> targetNames)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(targetNames);

        _diagnostics.Clear();
        _diagnosticKeys.Clear();
        _targetResults.Clear();
        _activeTargets.Clear();
        _propertiesWithoutConcreteValues.Clear();
        _targetAssignedItemTypes.Clear();
        _expressionDescriptors.Clear();
        _context = lookup.Clone().EnableHardenedState();
        _concreteState = new HardenedConcreteState(project);
        _concreteExpander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
            _concreteState,
            _concreteState,
            FileSystems.Default,
            loggingContext: null);

        HashSet<string> visitedTargets = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (string targetName in targetNames)
        {
            ArgumentException.ThrowIfNullOrEmpty(targetName);
            ValidateTarget(project, targetName, project.ProjectFileLocation, visitedTargets);
        }

        return _diagnostics;
    }

    private ValueState ValidateTarget(
        ProjectInstance project,
        string targetName,
        IElementLocation referenceLocation,
        HashSet<string> visitedTargets,
        bool isAfterTarget = false)
    {
        if (_activeTargets.Contains(targetName))
        {
            if (!isAfterTarget)
            {
                ReportCircularTarget(referenceLocation, targetName);
            }

            return _targetResults.TryGetValue(targetName, out ValueState activeResult)
                ? activeResult
                : ValueState.Blocked(new ValueOrigin($"result of circular target '{targetName}'"));
        }

        if (!visitedTargets.Add(targetName))
        {
            return _targetResults.TryGetValue(targetName, out ValueState result)
                ? result
                : ValueState.Blocked(new ValueOrigin($"result of target '{targetName}' while it is still being validated"));
        }

        if (!project.Targets.TryGetValue(targetName, out ProjectTargetInstance? target))
        {
            ReportMissingTarget(referenceLocation, targetName);
            ValueState missingResult =
                ValueState.Blocked(new ValueOrigin($"result of missing target '{targetName}'"));
            _targetResults[targetName] = missingResult;
            return missingResult;
        }

        _activeTargets.Add(targetName);
        ValueState targetResult = ValueState.Static;
        bool targetConditionMetadataValidated = RejectTargetMetadata(
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'");
        ExpressionValidationResult targetConditionResult = ValidateExpression(
            target,
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: targetConditionMetadataValidated);
        ValidateExpression(target, target.BeforeTargets, target.BeforeTargetsLocation, $"the BeforeTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);
        ValidateExpression(target, target.AfterTargets, target.AfterTargetsLocation, $"the AfterTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);

        bool targetExecutes = !TryEvaluateCondition(
            target,
            target.Condition,
            target.ConditionLocation,
            targetConditionResult,
            out bool conditionValue) || conditionValue;

        if (targetExecutes)
        {
            ExpressionValidationResult dependenciesResult = ValidateExpression(
                target,
                target.DependsOnTargets,
                target.DependsOnTargetsLocation,
                $"the DependsOnTargets attribute of target '{target.Name}'",
                requireStatic: true,
                isCondition: false);

            if (dependenciesResult.CanEvaluate &&
                TryExpandConcreteExpression(
                    target,
                    target.DependsOnTargets,
                    target.DependsOnTargetsLocation,
                    $"the DependsOnTargets attribute of target '{target.Name}'",
                    reportUnmodeled: true,
                    out string expandedDependencies))
            {
                foreach (string dependency in ExpressionShredder.SplitSemiColonSeparatedList(expandedDependencies))
                {
                    ValidateTarget(project, dependency, target.DependsOnTargetsLocation, visitedTargets);
                }
            }
        }

        foreach (TargetSpecification beforeTarget in project.GetTargetsWhichRunBefore(target.Name))
        {
            ValidateTarget(project, beforeTarget.TargetName, beforeTarget.ReferenceLocation, visitedTargets);
        }

        if (targetExecutes)
        {
            ValidateBatching(
                target,
                [target.Inputs, target.Outputs, target.Returns],
                implicitItemType: null,
                target.Location,
                $"target '{target.Name}'");

            LegacyCallTargetScope? callTargetScope = ContainsCallTarget(target)
                ? new LegacyCallTargetScope(CaptureState())
                : null;
            foreach (ProjectTargetInstanceChild child in target.Children)
            {
                switch (child)
                {
                    case ProjectPropertyGroupTaskInstance propertyGroup:
                        ValidatePropertyGroup(propertyGroup, target.Name, callTargetScope);
                        break;

                    case ProjectItemGroupTaskInstance itemGroup:
                        ValidateItemGroup(itemGroup, target.Name, callTargetScope);
                        break;

                    case ProjectTaskInstance task:
                        ValidateTask(project, task, target.Name, visitedTargets, callTargetScope);
                        break;

                    default:
                        ReportUnsupported(child.Location, child.GetType().Name, $"target '{target.Name}'");
                        break;
                }
            }

            string returnExpression = string.IsNullOrEmpty(target.Returns) ? target.Outputs : target.Returns;
            IElementLocation returnLocation = string.IsNullOrEmpty(target.Returns)
                ? target.OutputsLocation
                : target.ReturnsLocation;
            string returnAttribute = string.IsNullOrEmpty(target.Returns) ? "Outputs" : "Returns";
            ExpressionValidationResult returnResult = ValidateExpression(
                target,
                returnExpression,
                returnLocation,
                $"the {returnAttribute} attribute of target '{target.Name}'",
                requireStatic: false,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);
            targetResult = returnResult.CanEvaluate
                ? returnResult.State
                : ToBlocked(returnResult.State, $"return value of target '{target.Name}' is unavailable");

            ValidateOnErrorTargets(project, target, visitedTargets);
            if (callTargetScope is not null)
            {
                CompleteLegacyCallTargetScope(callTargetScope);
            }
        }

        _targetResults[targetName] = targetResult;
        foreach (TargetSpecification afterTarget in project.GetTargetsWhichRunAfter(target.Name))
        {
            ValidateTarget(
                project,
                afterTarget.TargetName,
                afterTarget.ReferenceLocation,
                visitedTargets,
                isAfterTarget: true);
        }

        _activeTargets.Remove(targetName);
        return targetResult;
    }

    private void ValidatePropertyGroup(
        ProjectPropertyGroupTaskInstance propertyGroup,
        string targetName,
        LegacyCallTargetScope? callTargetScope)
    {
        ExpressionValidationResult groupConditionResult = ValidateExpression(
            propertyGroup,
            propertyGroup.Condition,
            propertyGroup.ConditionLocation,
            $"the condition of a PropertyGroup in target '{targetName}'",
            requireStatic: true,
            isCondition: true);
        bool groupConditionKnown = TryEvaluateCondition(
            propertyGroup,
            propertyGroup.Condition,
            propertyGroup.ConditionLocation,
            groupConditionResult,
            out bool groupConditionValue);
        if (groupConditionKnown && !groupConditionValue)
        {
            return;
        }

        foreach (ProjectPropertyGroupTaskPropertyInstance property in propertyGroup.Properties)
        {
            ValueState batchingState = ValidateBatching(
                property,
                [property.Condition, property.Value],
                implicitItemType: null,
                property.Location,
                $"property '{property.Name}'");

            ExpressionValidationResult conditionResult = ValidateExpression(
                property,
                property.Condition,
                property.ConditionLocation,
                $"the condition of property '{property.Name}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true);
            bool propertyConditionKnown = TryEvaluateCondition(
                property,
                property.Condition,
                property.ConditionLocation,
                conditionResult,
                out bool propertyConditionValue);
            if (propertyConditionKnown && !propertyConditionValue)
            {
                continue;
            }

            ExpressionValidationResult valueResult = ValidateExpression(
                property,
                property.Value,
                property.Location,
                $"the value of property '{property.Name}'",
                requireStatic: false,
                isCondition: false,
                metadataBatchingValidated: true);

            ValueState propertyState = ValueState.Combine(
                batchingState,
                ValueState.Combine(conditionResult.State, valueResult.State));
            if (!conditionResult.State.IsStatic)
            {
                propertyState = ToBlocked(propertyState, $"property '{property.Name}' has an unavailable condition");
            }

            bool overwrites = groupConditionKnown &&
                groupConditionValue &&
                propertyConditionKnown &&
                propertyConditionValue;
            callTargetScope?.CallerAssignedProperties.Add(property.Name);
            Context.SetProperty(property.Name, propertyState, overwrite: overwrites);

            if (overwrites &&
                propertyState.IsStatic &&
                TryExpandConcreteExpression(
                    property,
                    property.Value,
                    property.Location,
                    $"the value of property '{property.Name}'",
                    reportUnmodeled: false,
                    out string expandedValue))
            {
                ConcreteState.SetProperty(property.Name, expandedValue);
                _propertiesWithoutConcreteValues.Remove(property.Name);
            }
            else
            {
                _propertiesWithoutConcreteValues.Add(property.Name);
            }
        }
    }

    private void ValidateItemGroup(
        ProjectItemGroupTaskInstance itemGroup,
        string targetName,
        LegacyCallTargetScope? callTargetScope)
    {
        ValidateExpression(
            itemGroup,
            itemGroup.Condition,
            itemGroup.ConditionLocation,
            $"the condition of an ItemGroup in target '{targetName}'",
            requireStatic: true,
            isCondition: true);

        foreach (ProjectItemGroupTaskItemInstance item in itemGroup.Items)
        {
            callTargetScope?.CallerAssignedItemTypes.Add(item.ItemType);
            _targetAssignedItemTypes.Add(item.ItemType);
            ValidateItemOperation(item, targetName);
        }
    }

    private void ValidateTask(
        ProjectInstance project,
        ProjectTaskInstance task,
        string targetName,
        HashSet<string> visitedTargets,
        LegacyCallTargetScope? callTargetScope)
    {
        if (!_taskClassifications.TryGetValue(task.Name, out HardenedTaskClassification classification))
        {
            classification = HardenedTaskClassification.Unaudited;
        }

        List<string> batchableExpressions = [];
        AddIfNotEmpty(batchableExpressions, task.Condition);
        AddIfNotEmpty(batchableExpressions, task.ContinueOnError);
        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            AddIfNotEmpty(batchableExpressions, parameter.Value.Item1);
        }

        foreach (ProjectTaskInstanceChild output in task.Outputs)
        {
            AddIfNotEmpty(batchableExpressions, GetOutputDestination(output));
            AddIfNotEmpty(batchableExpressions, GetTaskParameter(output));
            AddIfNotEmpty(batchableExpressions, output.Condition);
        }

        ValueState taskBatchingState = ValidateBatching(
            task,
            batchableExpressions,
            implicitItemType: null,
            task.Location,
            $"task '{task.Name}'");

        ExpressionValidationResult taskConditionResult = ValidateExpression(
            task,
            task.Condition,
            task.ConditionLocation,
            $"the condition of task '{task.Name}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: true);

        if (TryEvaluateCondition(
            task,
            task.Condition,
            task.ConditionLocation,
            taskConditionResult,
            out bool taskConditionValue) &&
            !taskConditionValue)
        {
            return;
        }

        ExpressionValidationResult continueOnErrorResult = ValidateExpression(
            task,
            task.ContinueOnError,
            task.ContinueOnErrorLocation,
            $"ContinueOnError of task '{task.Name}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);

        ValueState taskControlState = ValueState.Combine(
            taskBatchingState,
            ValueState.Combine(taskConditionResult.State, continueOnErrorResult.State));

        bool isCallTarget = MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "CallTarget");
        bool isMSBuild = MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "MSBuild");

        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            bool requiresStatic = classification == HardenedTaskClassification.Pure ||
                isCallTarget ||
                (isMSBuild && s_staticMSBuildParameters.Contains(parameter.Key));
            ExpressionValidationResult parameterResult = ValidateExpression(
                task,
                parameter.Value.Item1,
                parameter.Value.Item2,
                $"parameter '{parameter.Key}' of task '{task.Name}'",
                requireStatic: requiresStatic,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: classification == HardenedTaskClassification.Pure ||
                    (!isCallTarget && !isMSBuild));

            if (requiresStatic ||
                parameterResult.State.Availability == ValueAvailability.Blocked)
            {
                taskControlState = ValueState.Combine(taskControlState, parameterResult.State);
            }
        }

        if (isMSBuild)
        {
            taskControlState = ValidateMSBuildProjectMetadata(task, taskControlState);
        }

        ValueState? intrinsicTaskOutputState = null;
        if (isCallTarget)
        {
            intrinsicTaskOutputState = ValidateCallTarget(
                project,
                task,
                taskControlState,
                visitedTargets,
                callTargetScope ?? throw new InvalidOperationException("CallTarget scope was not captured."));
        }

        bool outputsAreDeferred = classification != HardenedTaskClassification.Pure;
        foreach (ProjectTaskInstanceChild output in task.Outputs)
        {
            string outputTaskParameter = GetTaskParameter(output);
            string? outputDestinationExpression = GetOutputDestination(output);
            ValueState outputConditionBatchingState = ValidateBatching(
                output,
                [output.Condition],
                implicitItemType: null,
                output.ConditionLocation,
                $"condition of output '{outputTaskParameter}' from task '{task.Name}'",
                reportDiagnostics: false);

            ExpressionValidationResult outputTaskParameterResult = ValidateExpression(
                output,
                outputTaskParameter,
                output.TaskParameterLocation,
                $"TaskParameter '{outputTaskParameter}' of output from task '{task.Name}'",
                requireStatic: true,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);

            ExpressionValidationResult outputConditionResult = ValidateExpression(
                output,
                output.Condition,
                output.ConditionLocation,
                $"the condition of output '{GetTaskParameter(output)}' from task '{task.Name}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true);

            ValueState outputDestinationBatchingState = ValidateBatching(
                output,
                [outputDestinationExpression],
                implicitItemType: null,
                output.Location,
                $"output destination of task '{task.Name}'",
                reportDiagnostics: false);

            string? destination;
            switch (output)
            {
                case ProjectTaskOutputPropertyInstance propertyOutput:
                    destination = ValidateOutputDestination(
                        propertyOutput,
                        propertyOutput.PropertyName,
                        propertyOutput.PropertyNameLocation,
                        $"PropertyName of output from task '{task.Name}'",
                        outputDestinationBatchingState);
                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    destination = ValidateOutputDestination(
                        itemOutput,
                        itemOutput.ItemType,
                        itemOutput.ItemTypeLocation,
                        $"ItemName of output from task '{task.Name}'",
                        outputDestinationBatchingState);
                    break;

                default:
                    ReportUnsupported(output.Location, output.GetType().Name, $"target '{targetName}'");
                    continue;
            }

            if (destination is null)
            {
                continue;
            }

            ValueOrigin origin = new($"output '{GetTaskParameter(output)}' of task '{task.Name}'");
            ValueState outputMappingState = ValueState.Combine(
                taskControlState,
                ValueState.Combine(
                    outputTaskParameterResult.State,
                    ValueState.Combine(outputConditionResult.State, outputConditionBatchingState)));
            ValueState outputState;
            if (!outputMappingState.IsStatic)
            {
                outputState = ToBlocked(
                    outputMappingState,
                    $"output '{GetTaskParameter(output)}' of task '{task.Name}' is unavailable");
            }
            else if (intrinsicTaskOutputState is ValueState intrinsicState &&
                     MSBuildNameIgnoreCaseComparer.Default.Equals(outputTaskParameter, "TargetOutputs"))
            {
                outputState = intrinsicState.WithOrigin($"output '{outputTaskParameter}' of task '{task.Name}'");
            }
            else
            {
                outputState = outputsAreDeferred ? ValueState.Deferred(origin) : ValueState.Static;
            }
            switch (output)
            {
                case ProjectTaskOutputPropertyInstance propertyOutput:
                    callTargetScope?.CallerAssignedProperties.Add(destination);
                    Context.SetProperty(destination, outputState);
                    _propertiesWithoutConcreteValues.Add(destination);
                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    callTargetScope?.CallerAssignedItemTypes.Add(destination);
                    Context.AddTaskOutputItems(destination, outputState);
                    _targetAssignedItemTypes.Add(destination);
                    break;
            }
        }

        ValueState taskStatus = ValueState.Deferred(new ValueOrigin($"result of task '{task.Name}'"));
        callTargetScope?.CallerAssignedProperties.Add(ReservedPropertyNames.lastTaskResult);
        Context.SetProperty(ReservedPropertyNames.lastTaskResult, taskStatus, overwrite: true);
        _propertiesWithoutConcreteValues.Add(ReservedPropertyNames.lastTaskResult);
    }

    private ValueState ValidateMSBuildProjectMetadata(
        ProjectTaskInstance task,
        ValueState taskControlState)
    {
        string? projectsExpression = null;
        IElementLocation projectsLocation = task.Location;
        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            if (MSBuildNameIgnoreCaseComparer.Default.Equals(parameter.Key, "Projects"))
            {
                projectsExpression = parameter.Value.Item1;
                projectsLocation = parameter.Value.Item2;
                break;
            }
        }

        if (projectsExpression is not { Length: > 0 } concreteProjectsExpression)
        {
            return taskControlState;
        }

        HardenedExpressionDescriptor descriptor =
            _expressionDescriptors.GetOrCreate(task, concreteProjectsExpression);
        if (descriptor.ItemTypes.IsEmpty)
        {
            return taskControlState;
        }

        ValueState metadataState = ValueState.Static;
        foreach (string itemType in descriptor.ItemTypes)
        {
            if (!Context.GetItemMembership(itemType).IsStatic)
            {
                continue;
            }

            foreach (string metadataName in s_staticMSBuildProjectMetadata)
            {
                ValueState state = Context.GetMetadata(itemType, metadataName);
                metadataState = ValueState.Combine(metadataState, state);
                RequireStatic(
                    state,
                    projectsLocation,
                    $"'{metadataName}' metadata on Projects item '{itemType}' of task '{task.Name}'",
                    concreteProjectsExpression);
            }
        }

        return ValueState.Combine(taskControlState, metadataState);
    }

    private ValueState ValidateCallTarget(
        ProjectInstance project,
        ProjectTaskInstance task,
        ValueState taskControlState,
        HashSet<string> visitedTargets,
        LegacyCallTargetScope callTargetScope)
    {
        string? targets = null;
        IElementLocation targetsLocation = task.Location;
        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            if (MSBuildNameIgnoreCaseComparer.Default.Equals(parameter.Key, "Targets"))
            {
                targets = parameter.Value.Item1;
                targetsLocation = parameter.Value.Item2;
            }
        }

        if (targets is not { Length: > 0 } targetsExpression ||
            !taskControlState.IsStatic ||
            !TryExpandConcreteExpression(
                task,
                targetsExpression,
                targetsLocation,
                $"Targets parameter of task '{task.Name}'",
                reportUnmodeled: true,
                out string expandedTargets))
        {
            return taskControlState.IsStatic
                ? ValueState.Static
                : ToBlocked(taskControlState, $"target outputs of task '{task.Name}' are unavailable");
        }

        ValidatorState callerState = CaptureState();
        ValueState targetOutputs = ValueState.Static;

        try
        {
            callTargetScope.WasInvoked = true;
            RestoreState(callTargetScope.CalledState);
            foreach (string calledTarget in ExpressionShredder.SplitSemiColonSeparatedList(expandedTargets))
            {
                targetOutputs = ValueState.Combine(
                    targetOutputs,
                    ValidateTarget(project, calledTarget, targetsLocation, visitedTargets));
            }
        }
        finally
        {
            callTargetScope.CalledState = CaptureState();
            RestoreState(callerState);
        }

        return targetOutputs;
    }

    private void CompleteLegacyCallTargetScope(LegacyCallTargetScope callTargetScope)
    {
        if (!callTargetScope.WasInvoked)
        {
            return;
        }

        ValidatorState callerState = CaptureState();
        ValidatorState mergedState = callTargetScope.CalledState;

        foreach (string propertyName in callTargetScope.CallerAssignedProperties)
        {
            mergedState.Context.CopyPropertyFrom(callerState.Context, propertyName);
            if (callerState.PropertiesWithoutConcreteValues.Contains(propertyName))
            {
                mergedState.PropertiesWithoutConcreteValues.Add(propertyName);
            }
            else
            {
                mergedState.PropertiesWithoutConcreteValues.Remove(propertyName);
                mergedState.ConcreteState.SetProperty(
                    propertyName,
                    callerState.ConcreteState.GetProperty(propertyName).EvaluatedValue);
            }
        }

        foreach (string itemType in callTargetScope.CallerAssignedItemTypes)
        {
            mergedState.Context.CopyItemFrom(callerState.Context, itemType);
        }

        mergedState.TargetAssignedItemTypes.UnionWith(callerState.TargetAssignedItemTypes);
        RestoreState(mergedState);
    }

    private ValidatorState CaptureState()
        => new(
            Context.Snapshot(),
            ConcreteState.Clone(),
            new HashSet<string>(
                _propertiesWithoutConcreteValues,
                MSBuildNameIgnoreCaseComparer.Default),
            new HashSet<string>(
                _targetAssignedItemTypes,
                MSBuildNameIgnoreCaseComparer.Default));

    private void RestoreState(ValidatorState state)
    {
        _context = state.Context;
        _concreteState = state.ConcreteState;
        _concreteExpander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
            _concreteState,
            _concreteState,
            FileSystems.Default,
            loggingContext: null);
        RestoreSet(_propertiesWithoutConcreteValues, state.PropertiesWithoutConcreteValues);
        RestoreSet(_targetAssignedItemTypes, state.TargetAssignedItemTypes);
    }

    private void ValidateOnErrorTargets(
        ProjectInstance project,
        ProjectTargetInstance target,
        HashSet<string> visitedTargets)
    {
        foreach (ProjectOnErrorInstance onError in target.OnErrorChildren)
        {
            ValidateExpression(
                onError,
                onError.Condition,
                onError.ConditionLocation,
                $"the condition of OnError in target '{target.Name}'",
                requireStatic: true,
                isCondition: true,
                allowTaskStatus: true);

            ExpressionValidationResult targetsResult = ValidateExpression(
                onError,
                onError.ExecuteTargets,
                onError.ExecuteTargetsLocation,
                $"the ExecuteTargets attribute of OnError in target '{target.Name}'",
                requireStatic: true,
                isCondition: false);

            if (targetsResult.CanEvaluate &&
                TryExpandConcreteExpression(
                    onError,
                    onError.ExecuteTargets,
                    onError.ExecuteTargetsLocation,
                    $"the ExecuteTargets attribute of OnError in target '{target.Name}'",
                    reportUnmodeled: true,
                    out string expandedTargets))
            {
                foreach (string errorTarget in ExpressionShredder.SplitSemiColonSeparatedList(expandedTargets))
                {
                    ValidateTarget(project, errorTarget, onError.ExecuteTargetsLocation, visitedTargets);
                }
            }
        }
    }

    private string? ValidateOutputDestination(
        ProjectTaskInstanceChild output,
        string destination,
        IElementLocation location,
        string context,
        ValueState batchingState)
    {
        ExpressionValidationResult result = ValidateExpression(
            output,
            destination,
            location,
            context,
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);

        if (!ValueState.Combine(result.State, batchingState).IsStatic)
        {
            return null;
        }

        if (!TryExpandConcreteExpression(
                output,
                destination,
                location,
                context,
                reportUnmodeled: true,
                out string expandedDestination))
        {
            return null;
        }

        if (string.IsNullOrEmpty(expandedDestination) ||
            ExpressionShredder.ContainsPropertyMarker(expandedDestination) ||
            ExpressionShredder.ContainsItemVectorMarker(expandedDestination) ||
            ExpressionShredder.ContainsMetadataMarker(expandedDestination))
        {
            ReportUnsupported(location, "a statically resolvable output destination", context);
            return null;
        }

        return expandedDestination;
    }

    private bool ReferencesPropertyWithoutConcreteValue(HardenedExpressionDescriptor descriptor)
    {
        foreach (string propertyName in descriptor.Properties)
        {
            if (_propertiesWithoutConcreteValues.Contains(propertyName))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEvaluateCondition(
        object owner,
        string condition,
        IElementLocation? location,
        ExpressionValidationResult validationResult,
        out bool result)
    {
        result = true;
        if (condition.Length == 0)
        {
            return true;
        }

        if (!validationResult.CanEvaluate ||
            !validationResult.State.IsStatic ||
            !CanExpandConcreteExpression(owner, condition))
        {
            return false;
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        ElementLocation conditionLocation = effectiveLocation as ElementLocation ?? ElementLocation.EmptyLocation;
        try
        {
            result = ConditionEvaluator.EvaluateCondition(
                condition,
                ParserOptions.AllowPropertiesAndItemLists,
                ConcreteExpander,
                ExpanderOptions.ExpandPropertiesAndItems,
                ConcreteState.ProjectDirectory,
                conditionLocation,
                FileSystems.Default,
                loggingContext: null);
            return true;
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return false;
        }
    }

    private bool TryExpandConcreteExpression(
        object owner,
        string expression,
        IElementLocation? location,
        string context,
        bool reportUnmodeled,
        out string expanded)
    {
        expanded = string.Empty;
        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        if (!CanExpandConcreteExpression(owner, expression))
        {
            if (reportUnmodeled)
            {
                ReportUnsupported(
                    effectiveLocation,
                    "an expression whose target-time value cannot yet be represented",
                    context);
            }

            return false;
        }

        try
        {
            expanded = ConcreteExpander.ExpandIntoStringAndUnescape(
                expression,
                ExpanderOptions.ExpandPropertiesAndItems,
                effectiveLocation);
            return true;
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return false;
        }
    }

    private bool CanExpandConcreteExpression(object owner, string expression)
    {
        HardenedExpressionDescriptor descriptor =
            _expressionDescriptors.GetOrCreate(owner, expression);
        if (ReferencesPropertyWithoutConcreteValue(descriptor))
        {
            return false;
        }

        if (descriptor.BatchMetadata is not null)
        {
            return false;
        }

        if (!descriptor.ItemTypes.IsEmpty)
        {
            foreach (string itemType in descriptor.ItemTypes)
            {
                if (_targetAssignedItemTypes.Contains(itemType))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? GetOutputDestination(ProjectTaskInstanceChild output)
        => output switch
        {
            ProjectTaskOutputPropertyInstance property => property.PropertyName,
            ProjectTaskOutputItemInstance item => item.ItemType,
            _ => null,
        };

    private ExpressionValidationResult ValidateExpression(
        object owner,
        string? expression,
        IElementLocation? location,
        string context,
        bool requireStatic,
        bool isCondition,
        bool metadataBatchingValidated = false,
        bool includeItemMetadata = false,
        bool allowTaskStatus = false,
        string? implicitItemType = null)
    {
        if (expression is null || expression.Length == 0)
        {
            return new ExpressionValidationResult(ValueState.Static, CanEvaluate: true);
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        bool canEvaluate = ValidateProhibitedFunctions(expression, effectiveLocation, context, isCondition);
        ValueState state = canEvaluate
            ? ValueState.Static
            : ValueState.Blocked(new ValueOrigin($"unsupported expression in {context}"));

        HardenedExpressionDescriptor descriptor =
            _expressionDescriptors.GetOrCreate(owner, expression, implicitItemType);
        state = ValueState.Combine(state, FindPropertyState(descriptor, allowTaskStatus));

        if (!descriptor.ItemTypes.IsEmpty)
        {
            foreach (string itemType in descriptor.ItemTypes)
            {
                state = ValueState.Combine(state, Context.GetItemMembership(itemType));
            }
        }

        state = ValueState.Combine(state, FindTransformMetadataState(descriptor, includeItemMetadata));

        if (!metadataBatchingValidated && descriptor.BatchMetadata is not null)
        {
            ValueState metadataState = GetMetadataReferenceState(
                descriptor,
                effectiveLocation,
                context);
            if (metadataState.Availability == ValueAvailability.Blocked)
            {
                canEvaluate = false;
            }

            state = ValueState.Combine(state, metadataState);
        }

        if (requireStatic && !RequireStatic(state, effectiveLocation, context, expression))
        {
            canEvaluate = false;
        }

        return new ExpressionValidationResult(state, canEvaluate);
    }

    private ValueState FindPropertyState(
        HardenedExpressionDescriptor descriptor,
        bool allowTaskStatus)
    {
        ValueState state = ValueState.Static;
        foreach (string propertyName in descriptor.Properties)
        {
            if (!allowTaskStatus ||
                !MSBuildNameIgnoreCaseComparer.Default.Equals(
                    propertyName,
                    ReservedPropertyNames.lastTaskResult))
            {
                state = ValueState.Combine(state, Context.GetProperty(propertyName));
            }
        }

        return state;
    }

    private void ValidateItemOperation(ProjectItemGroupTaskItemInstance item, string targetName)
    {
        List<string> batchableExpressions = [];
        AddIfNotEmpty(batchableExpressions, item.Include);
        AddIfNotEmpty(batchableExpressions, item.Exclude);
        AddIfNotEmpty(batchableExpressions, item.Remove);
        AddIfNotEmpty(batchableExpressions, item.Condition);
        foreach (ProjectItemGroupTaskMetadataInstance metadata in item.Metadata)
        {
            AddIfNotEmpty(batchableExpressions, metadata.Value);
            AddIfNotEmpty(batchableExpressions, metadata.Condition);
        }

        ValueState batchingState = ValidateBatching(
            item,
            batchableExpressions,
            item.ItemType,
            item.Location,
            $"item '{item.ItemType}' in target '{targetName}'");

        ExpressionValidationResult conditionResult = ValidateExpression(
            item,
            item.Condition,
            item.ConditionLocation,
            $"the condition of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult includeResult = ValidateExpression(
            item,
            item.Include,
            item.IncludeLocation,
            $"the Include of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult excludeResult = ValidateExpression(
            item,
            item.Exclude,
            item.ExcludeLocation,
            $"the Exclude of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult removeResult = ValidateExpression(
            item,
            item.Remove,
            item.RemoveLocation,
            $"the Remove of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult matchOnMetadataResult = ValidateExpression(
            item,
            item.MatchOnMetadata,
            item.MatchOnMetadataLocation,
            $"MatchOnMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult matchOnMetadataOptionsResult = ValidateExpression(
            item,
            item.MatchOnMetadataOptions,
            item.MatchOnMetadataOptionsLocation,
            $"MatchOnMetadataOptions on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult keepMetadataResult = ValidateExpression(
            item,
            item.KeepMetadata,
            item.KeepMetadataLocation,
            $"KeepMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult removeMetadataResult = ValidateExpression(
            item,
            item.RemoveMetadata,
            item.RemoveMetadataLocation,
            $"RemoveMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        ExpressionValidationResult keepDuplicatesResult = ValidateExpression(
            item,
            item.KeepDuplicates,
            item.KeepDuplicatesLocation,
            $"KeepDuplicates on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);

        ValueState destinationMembership = Context.GetItemMembership(item.ItemType);
        RequireStatic(
            destinationMembership,
            item.Location,
            $"the '{item.ItemType}' item operation",
            item.ItemType);

        Dictionary<string, ValueState> assignedMetadata = ValidateMetadataAssignments(item);
        ValueState matchOnMetadataValuesState = GetMatchOnMetadataValuesState(item);
        ValueState operationState = ValueState.Combine(
            batchingState,
            ValueState.Combine(
                destinationMembership,
                ValueState.Combine(
                    conditionResult.State,
                    ValueState.Combine(
                        includeResult.State,
                        ValueState.Combine(
                            excludeResult.State,
                            ValueState.Combine(
                                removeResult.State,
                                ValueState.Combine(
                                    matchOnMetadataResult.State,
                                    ValueState.Combine(
                                        matchOnMetadataOptionsResult.State,
                                        ValueState.Combine(
                                            keepMetadataResult.State,
                                            ValueState.Combine(
                                                removeMetadataResult.State,
                                                ValueState.Combine(
                                                    keepDuplicatesResult.State,
                                                    matchOnMetadataValuesState)))))))))));

        bool isInclude = item.Include.Length != 0 || item.Exclude.Length != 0;
        bool isRemove = !isInclude && item.Remove.Length != 0;

        if (!operationState.IsStatic)
        {
            if (isInclude || isRemove)
            {
                Context.BlockItemMembership(
                    item.ItemType,
                    operationState,
                    $"item '{item.ItemType}' membership");
            }
            else
            {
                foreach (string metadataName in assignedMetadata.Keys)
                {
                    Context.BlockMetadata(
                        item.ItemType,
                        metadataName,
                        operationState,
                        $"metadata '{metadataName}' update on item '{item.ItemType}'");
                }
            }

            return;
        }

        if (isInclude)
        {
            GetInheritedMetadata(
                item,
                item.Include,
                out Dictionary<string, ValueState>? inheritedMetadata,
                out ValueState inheritedDefaultMetadata);

            Context.AddItems(
                item.ItemType,
                ValueState.Static,
                inheritedMetadata,
                inheritedDefaultMetadata,
                assignedMetadata,
                item.KeepMetadata,
                item.RemoveMetadata,
                $"Include into item '{item.ItemType}'");
        }
        else if (!isRemove)
        {
            Context.ApplyMetadataFilters(item.ItemType, item.KeepMetadata, item.RemoveMetadata);
            Context.UpdateMetadata(item.ItemType, assignedMetadata);
        }
    }

    private Dictionary<string, ValueState> ValidateMetadataAssignments(ProjectItemGroupTaskItemInstance item)
    {
        var assignedMetadata = new Dictionary<string, ValueState>(MSBuildNameIgnoreCaseComparer.Default);
        foreach (ProjectItemGroupTaskMetadataInstance metadata in item.Metadata)
        {
            ExpressionValidationResult conditionResult = ValidateExpression(
                item,
                metadata.Condition,
                metadata.ConditionLocation,
                $"the condition of metadata '{metadata.Name}' on item '{item.ItemType}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true,
                implicitItemType: item.ItemType);
            ExpressionValidationResult valueResult = ValidateExpression(
                item,
                metadata.Value,
                metadata.Location,
                $"the value of metadata '{metadata.Name}' on item '{item.ItemType}'",
                requireStatic: false,
                isCondition: false,
                metadataBatchingValidated: true,
                implicitItemType: item.ItemType);

            ValueState metadataState = valueResult.State;
            if (!conditionResult.State.IsStatic)
            {
                metadataState = ToBlocked(
                    ValueState.Combine(conditionResult.State, valueResult.State),
                    $"metadata '{metadata.Name}' on item '{item.ItemType}' has an unavailable condition");
            }

            assignedMetadata[metadata.Name] = metadataState;
        }

        return assignedMetadata;
    }

    private ValueState GetMatchOnMetadataValuesState(ProjectItemGroupTaskItemInstance item)
    {
        if (!TryParseLiteralMetadataNames(item.MatchOnMetadata, out HashSet<string>? metadataNames))
        {
            return ValueState.Static;
        }

        HardenedExpressionDescriptor removeDescriptor =
            _expressionDescriptors.GetOrCreate(item, item.Remove, item.ItemType);
        if (removeDescriptor.ItemTypes.IsEmpty)
        {
            return ValueState.Static;
        }

        ValueState state = ValueState.Static;
        foreach (string metadataName in metadataNames!)
        {
            ValueState destinationMetadata = Context.GetMetadata(item.ItemType, metadataName);
            state = ValueState.Combine(state, destinationMetadata);
            RequireStatic(
                destinationMetadata,
                item.MatchOnMetadataLocation,
                $"MatchOnMetadata '{metadataName}' on item '{item.ItemType}'",
                item.Remove);

            foreach (string sourceItemType in removeDescriptor.ItemTypes)
            {
                ValueState sourceMetadata = Context.GetMetadata(sourceItemType, metadataName);
                state = ValueState.Combine(state, sourceMetadata);
                RequireStatic(
                    sourceMetadata,
                    item.MatchOnMetadataLocation,
                    $"MatchOnMetadata '{metadataName}' from item '{sourceItemType}'",
                    item.Remove);
            }
        }

        return state;
    }

    private void GetInheritedMetadata(
        ProjectItemGroupTaskItemInstance item,
        string include,
        out Dictionary<string, ValueState>? inheritedMetadata,
        out ValueState inheritedDefaultMetadata)
    {
        inheritedMetadata = null;
        inheritedDefaultMetadata = ValueState.Static;
        HardenedExpressionDescriptor descriptor =
            _expressionDescriptors.GetOrCreate(item, include, item.ItemType);
        foreach (HardenedItemVectorDescriptor itemVector in descriptor.ItemVectors)
        {
            inheritedMetadata ??= new Dictionary<string, ValueState>(MSBuildNameIgnoreCaseComparer.Default);
            inheritedDefaultMetadata = ValueState.Combine(
                inheritedDefaultMetadata,
                Context.GetDefaultMetadata(itemVector.ItemType));

            foreach (KeyValuePair<string, ValueState> metadata in Context.GetMetadata(itemVector.ItemType))
            {
                ValueState existing = inheritedMetadata.TryGetValue(metadata.Key, out ValueState state)
                    ? state
                    : ValueState.Static;
                inheritedMetadata[metadata.Key] = ValueState.Combine(existing, metadata.Value);
            }
        }
    }

    private ValueState ValidateBatching(
        object owner,
        IReadOnlyList<string?> expressions,
        string? implicitItemType,
        IElementLocation? location,
        string context,
        bool reportDiagnostics = true)
    {
        List<string> nonEmptyExpressions = [];
        var consumedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        var metadataReferences = new Dictionary<string, MetadataReference>(MSBuildNameIgnoreCaseComparer.Default);
        for (int i = 0; i < expressions.Count; i++)
        {
            string? expression = expressions[i];
            if (string.IsNullOrEmpty(expression))
            {
                continue;
            }

            nonEmptyExpressions.Add(expression!);
            HardenedExpressionDescriptor descriptor =
                _expressionDescriptors.GetOrCreate(owner, expression, implicitItemType);
            if (!descriptor.ItemTypes.IsEmpty)
            {
                consumedItemTypes.UnionWith(descriptor.ItemTypes);
            }

            if (descriptor.BatchMetadata is not null)
            {
                foreach (KeyValuePair<string, MetadataReference> metadata in descriptor.BatchMetadata)
                {
                    metadataReferences[metadata.Key] = metadata.Value;
                }
            }
        }

        if (nonEmptyExpressions.Count == 0)
        {
            return ValueState.Static;
        }

        if (metadataReferences.Count == 0)
        {
            return ValueState.Static;
        }

        if (implicitItemType is not null)
        {
            consumedItemTypes.Add(implicitItemType);
        }

        var batchedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        BatchingEngine.AddItemTypesToBeBatched(
            metadataReferences,
            consumedItemTypes,
            batchedItemTypes);

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        if (batchedItemTypes.Count == 0)
        {
            if (reportDiagnostics)
            {
                ReportUnsupported(
                    effectiveLocation,
                    $"unqualified metadata in {context} without an associated item list",
                    context);
            }

            return ValueState.Blocked(new ValueOrigin($"unresolved batching metadata in {context}"));
        }

        ValueState state = ValueState.Static;
        foreach (string itemType in batchedItemTypes)
        {
            state = ValueState.Combine(state, Context.GetItemMembership(itemType));
            foreach (MetadataReference metadataReference in metadataReferences.Values)
            {
                if (metadataReference.ItemName is null ||
                    MSBuildNameIgnoreCaseComparer.Default.Equals(metadataReference.ItemName, itemType))
                {
                    state = ValueState.Combine(
                        state,
                        Context.GetMetadata(itemType, metadataReference.MetadataName));
                }
            }
        }

        if (reportDiagnostics)
        {
            RequireStatic(
                state,
                effectiveLocation,
                $"batching of {context}",
                string.Join(";", nonEmptyExpressions));
        }

        return state;
    }

    private ValueState GetMetadataReferenceState(
        HardenedExpressionDescriptor descriptor,
        IElementLocation location,
        string context)
    {
        var consumedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        if (!descriptor.ItemTypes.IsEmpty)
        {
            consumedItemTypes.UnionWith(descriptor.ItemTypes);
        }

        if (descriptor.ImplicitItemType is not null)
        {
            consumedItemTypes.Add(descriptor.ImplicitItemType);
        }

        var itemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        BatchingEngine.AddItemTypesToBeBatched(
            descriptor.BatchMetadata!,
            consumedItemTypes,
            itemTypes);
        if (itemTypes.Count == 0)
        {
            ReportUnsupported(
                location,
                $"unqualified metadata in {context} without an associated item list",
                context);
            return ValueState.Blocked(new ValueOrigin($"unresolved metadata in {context}"));
        }

        ValueState state = ValueState.Static;
        foreach (string itemType in itemTypes)
        {
            foreach (MetadataReference metadataReference in descriptor.BatchMetadata!.Values)
            {
                if (metadataReference.ItemName is null ||
                    MSBuildNameIgnoreCaseComparer.Default.Equals(metadataReference.ItemName, itemType))
                {
                    state = ValueState.Combine(
                        state,
                        Context.GetMetadata(itemType, metadataReference.MetadataName));
                }
            }
        }

        return state;
    }

    private ValueState FindTransformMetadataState(
        HardenedExpressionDescriptor descriptor,
        bool includeItemMetadata)
    {
        ValueState state = ValueState.Static;
        foreach (HardenedItemVectorDescriptor itemVector in descriptor.ItemVectors)
        {
            if (includeItemMetadata && itemVector.Transforms.IsEmpty)
            {
                state = ValueState.Combine(state, Context.GetItemValue(itemVector.ItemType, includeMetadata: true));
            }

            foreach (HardenedItemTransformDescriptor transform in itemVector.Transforms)
            {
                foreach (MetadataReference metadataReference in transform.Metadata)
                {
                    string itemType = metadataReference.ItemName ?? itemVector.ItemType;
                    ValueState metadataState = Context.GetMetadata(itemType, metadataReference.MetadataName);
                    state = ValueState.Combine(
                        state,
                        metadataState.WithOrigin($"transform of item '{itemVector.ItemType}'"));
                }
            }
        }

        return state;
    }

    private bool RequireStatic(
        ValueState state,
        IElementLocation location,
        string context,
        string expression)
    {
        if (state.Availability == ValueAvailability.Deferred)
        {
            ReportDeferred(location, context, expression, state.Origin!);
            return false;
        }

        return state.Availability == ValueAvailability.Static;
    }

    private static ValueState ToBlocked(ValueState state, string description)
        => state.Availability == ValueAvailability.Blocked
            ? state.WithOrigin(description)
            : ValueState.Blocked(new ValueOrigin(description, state.Origin));

    private bool RejectTargetMetadata(string expression, IElementLocation? location, string context)
    {
        if (ExpressionShredder.ContainsMetadataExpressionOutsideTransform(expression))
        {
            ReportUnsupported(
                location ?? ElementLocation.EmptyLocation,
                "metadata expressions",
                context);
            return true;
        }

        return false;
    }

    private static bool TryParseLiteralMetadataNames(string expression, out HashSet<string>? metadataNames)
    {
        metadataNames = null;
        if (string.IsNullOrEmpty(expression) ||
            expression.AsSpan().IndexOfAny('$', '@', '%') >= 0)
        {
            return false;
        }

        metadataNames = new HashSet<string>(
            ExpressionShredder.SplitSemiColonSeparatedList(expression),
            MSBuildNameIgnoreCaseComparer.Default);
        return true;
    }

    private static void AddIfNotEmpty(List<string> expressions, string? expression)
    {
        if (!string.IsNullOrEmpty(expression))
        {
            expressions.Add(expression!);
        }
    }

    private static bool ContainsCallTarget(ProjectTargetInstance target)
    {
        foreach (ProjectTargetInstanceChild child in target.Children)
        {
            if (child is ProjectTaskInstance task &&
                MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "CallTarget"))
            {
                return true;
            }
        }

        return false;
    }

    private static void RestoreSet(HashSet<string> destination, HashSet<string> source)
    {
        destination.Clear();
        destination.UnionWith(source);
    }

    private bool ValidateProhibitedFunctions(
        string expression,
        IElementLocation location,
        string context,
        bool isCondition)
    {
        bool canEvaluate = true;

        if (expression.Contains("$(registry:", StringComparison.OrdinalIgnoreCase))
        {
            ReportProhibitedFunction(location, "$(registry:...)", context);
            canEvaluate = false;
        }

        if (isCondition && ContainsFunctionCall(expression, "Exists"))
        {
            ReportProhibitedFunction(location, "Exists", context);
            canEvaluate = false;
        }

        int marker = expression.IndexOf("$([", StringComparison.Ordinal);
        while (marker >= 0)
        {
            int typeStart = marker + 3;
            int typeEnd = expression.IndexOf(']', typeStart);
            if (typeEnd < 0)
            {
                break;
            }

            ReadOnlySpan<char> typeName = expression.AsSpan(typeStart, typeEnd - typeStart).Trim();
            int separator = typeEnd + 1;
            while (separator < expression.Length && char.IsWhiteSpace(expression[separator]))
            {
                separator++;
            }

            if (separator + 1 >= expression.Length ||
                expression[separator] != ':' ||
                expression[separator + 1] != ':')
            {
                marker = expression.IndexOf("$([", marker + 3, StringComparison.Ordinal);
                continue;
            }

            int memberStart = separator + 2;
            while (memberStart < expression.Length && char.IsWhiteSpace(expression[memberStart]))
            {
                memberStart++;
            }

            int memberEnd = memberStart;
            while (memberEnd < expression.Length)
            {
                char character = expression[memberEnd];
                if (character is '(' or '.' or ')' || char.IsWhiteSpace(character))
                {
                    break;
                }

                memberEnd++;
            }

            ReadOnlySpan<char> memberName = expression.AsSpan(memberStart, memberEnd - memberStart);
            if (IsProhibitedFunction(typeName, memberName))
            {
                ReportProhibitedFunction(location, $"$([{typeName.ToString()}]::{memberName.ToString()})", context);
                canEvaluate = false;
            }

            marker = expression.IndexOf("$([", marker + 3, StringComparison.Ordinal);
        }

        return canEvaluate;
    }

    private static bool ContainsFunctionCall(string expression, string functionName)
    {
        bool inQuote = false;
        char quote = '\0';

        for (int index = 0; index <= expression.Length - functionName.Length; index++)
        {
            char character = expression[index];
            if (character is '\'' or '"' or '`')
            {
                if (inQuote && character == quote)
                {
                    inQuote = false;
                }
                else if (!inQuote)
                {
                    inQuote = true;
                    quote = character;
                }

                continue;
            }

            if (inQuote ||
                !expression.AsSpan(index).StartsWith(functionName, StringComparison.OrdinalIgnoreCase) ||
                (index > 0 && IsIdentifierCharacter(expression[index - 1])))
            {
                continue;
            }

            int openParenthesis = index + functionName.Length;
            while (openParenthesis < expression.Length && char.IsWhiteSpace(expression[openParenthesis]))
            {
                openParenthesis++;
            }

            if (openParenthesis < expression.Length && expression[openParenthesis] == '(')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    private static bool IsProhibitedFunction(ReadOnlySpan<char> typeName, ReadOnlySpan<char> memberName)
    {
        if (typeName.Equals("System.IO.Path", StringComparison.OrdinalIgnoreCase))
        {
            return !s_purePathMembers.Contains(memberName.ToString());
        }

        if (typeName.StartsWith("System.IO", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Environment", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase) ||
            typeName.Equals("System.Random", StringComparison.OrdinalIgnoreCase) ||
            typeName.StartsWith("Microsoft.Win32.Registry", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (typeName.Equals("System.Guid", StringComparison.OrdinalIgnoreCase) &&
            memberName.Equals("NewGuid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((typeName.Equals("System.DateTime", StringComparison.OrdinalIgnoreCase) ||
             typeName.Equals("System.DateTimeOffset", StringComparison.OrdinalIgnoreCase)) &&
            (memberName.Equals("Now", StringComparison.OrdinalIgnoreCase) ||
             memberName.Equals("UtcNow", StringComparison.OrdinalIgnoreCase) ||
             memberName.Equals("Today", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return typeName.Equals("MSBuild", StringComparison.OrdinalIgnoreCase) &&
               (memberName.Equals("Exists", StringComparison.OrdinalIgnoreCase) ||
                memberName.Equals("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase) ||
                memberName.Equals("GetDirectoryNameOfFileAbove", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetTaskParameter(ProjectTaskInstanceChild output)
        => output switch
        {
            ProjectTaskOutputPropertyInstance property => property.TaskParameter,
            ProjectTaskOutputItemInstance item => item.TaskParameter,
            _ => output.GetType().Name,
        };

    private void ReportUnsupported(IElementLocation location, string construct, string context)
        => AddDiagnostic(
            () => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
                new BuildEventFileInfo(location),
                "HardenedGraphUnsupportedConstruct",
                construct,
                context));

    private void ReportProhibitedFunction(IElementLocation location, string function, string context)
        => AddDiagnostic(
            () => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
                new BuildEventFileInfo(location),
                "HardenedGraphProhibitedFunction",
                function,
                context));

    private void ReportDeferred(IElementLocation location, string context, string expression, ValueOrigin origin)
        => AddDiagnostic(
            () => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
                new BuildEventFileInfo(location),
                "HardenedGraphDeferredValueInStaticContext",
                context,
                expression,
                origin.ToString()));

    private void ReportMissingTarget(IElementLocation location, string targetName)
        => AddDiagnostic(
            () => ProjectErrorUtilities.ThrowInvalidProject(
                location,
                "TargetDoesNotExist",
                targetName));

    private void ReportCircularTarget(IElementLocation location, string targetName)
        => AddDiagnostic(
            () => ProjectErrorUtilities.ThrowInvalidProject(
                location,
                "CircularDependency",
                targetName));

    private void AddDiagnostic(Action throwDiagnostic)
    {
        try
        {
            throwDiagnostic();
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
        }
    }

    private void AddDiagnostic(InvalidProjectFileException exception)
    {
        string key = $"{exception.ErrorCode}\0{exception.ProjectFile}\0{exception.LineNumber}\0{exception.ColumnNumber}\0{exception.Message}";
        if (_diagnosticKeys.Add(key))
        {
            _diagnostics.Add(exception);
        }
    }

    private HardenedLookupState Context
        => _context ?? throw new InvalidOperationException("Validation context has not been initialized.");

    private HardenedConcreteState ConcreteState
        => _concreteState ?? throw new InvalidOperationException("Concrete state has not been initialized.");

    private Expander<ProjectPropertyInstance, ProjectItemInstance> ConcreteExpander
        => _concreteExpander ?? throw new InvalidOperationException("Concrete expander has not been initialized.");

    private sealed class LegacyCallTargetScope(ValidatorState calledState)
    {
        internal ValidatorState CalledState { get; set; } = calledState;

        internal HashSet<string> CallerAssignedProperties { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal HashSet<string> CallerAssignedItemTypes { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal bool WasInvoked { get; set; }
    }

    private sealed record ValidatorState(
        HardenedLookupState Context,
        HardenedConcreteState ConcreteState,
        HashSet<string> PropertiesWithoutConcreteValues,
        HashSet<string> TargetAssignedItemTypes);

    private readonly record struct ExpressionValidationResult(ValueState State, bool CanEvaluate);
}
