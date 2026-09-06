// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
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
    private readonly HardenedTaskClassifier? _taskClassifier;
    private readonly HardenedPureTaskExecutor? _pureTaskExecutor;
    private readonly List<InvalidProjectFileException> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueState> _targetResults =
        new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _activeTargets = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HardenedExpressionDescriptorCache _expressionDescriptors = new();
    private Lookup? _validationLookup;
    private Expander<ProjectPropertyInstance, ProjectItemInstance>? _concreteExpander;
    private IMetadataTable? _activeMetadata;
    private ProjectInstance? _project;
    private string? _projectDirectory;
    private HardenedItemOperationPlan? _itemOperationPlan;

    internal HardenedTargetValidator(
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications,
        HardenedPureTaskExecutor? pureTaskExecutor = null)
    {
        _pureTaskExecutor = pureTaskExecutor;
        _taskClassifications = new Dictionary<string, HardenedTaskClassification>(
            taskClassifications.Count,
            MSBuildNameIgnoreCaseComparer.Default);

        foreach (KeyValuePair<string, HardenedTaskClassification> classification in taskClassifications)
        {
            _taskClassifications.Add(classification.Key, classification.Value);
        }
    }

    internal HardenedTargetValidator(
        HardenedTaskClassifier taskClassifier,
        HardenedPureTaskExecutor? pureTaskExecutor = null)
        : this(new Dictionary<string, HardenedTaskClassification>(), pureTaskExecutor)
    {
        _taskClassifier = taskClassifier;
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
            targetNames,
            new HardenedItemOperationPlan(project.Directory));

    internal IReadOnlyList<InvalidProjectFileException> Validate(
        ProjectInstance project,
        Lookup lookup,
        IEnumerable<string> targetNames)
        => Validate(
            project,
            lookup,
            targetNames,
            new HardenedItemOperationPlan(project.Directory));

    internal IReadOnlyList<InvalidProjectFileException> Validate(
        ProjectInstance project,
        Lookup lookup,
        IEnumerable<string> targetNames,
        HardenedItemOperationPlan itemOperationPlan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(targetNames);
        ArgumentNullException.ThrowIfNull(itemOperationPlan);

        _diagnostics.Clear();
        _diagnosticKeys.Clear();
        _targetResults.Clear();
        _activeTargets.Clear();
        _expressionDescriptors.Clear();
        _validationLookup = lookup.Clone();
        _validationLookup.EnableHardenedState();
        _validationLookup.ConfigureHardenedItemOperationPlan(itemOperationPlan);
        _validationLookup.EnterScope("HardenedTargetValidator");
        _activeMetadata = null;
        _project = project;
        _projectDirectory = project.Directory;
        _itemOperationPlan = itemOperationPlan;
        _concreteExpander = CreateConcreteExpander(_validationLookup);

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
        bool isAfterTarget = false,
        bool isFailureContext = false)
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
        List<FailureState>? preBodyFailureStates =
            target.OnErrorChildren.Count == 0 ? null : [];
        ValueState targetResult = ValueState.Static;

        bool targetConditionMetadataRejected = RejectTargetMetadata(
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'");
        ConditionValidationResult targetConditionResult = ValidateCondition(
            target,
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'",
            metadataBatchingValidated: targetConditionMetadataRejected,
            conditionSyntaxValid: !targetConditionMetadataRejected);
        if (preBodyFailureStates is not null &&
            targetConditionResult.MayThrow)
        {
            preBodyFailureStates.Add(new FailureState(CaptureBranchState(), CallTargetScope: null));
        }

        ValidateExpression(target, target.BeforeTargets, target.BeforeTargetsLocation, $"the BeforeTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);
        ValidateExpression(target, target.AfterTargets, target.AfterTargetsLocation, $"the AfterTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);

        bool targetExecutes =
            !targetConditionResult.IsKnown ||
            targetConditionResult.Value;

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
                    preBodyFailureStates?.Add(
                        new FailureState(CaptureBranchState(), CallTargetScope: null));
                    ValidateTarget(
                        project,
                        dependency,
                        target.DependsOnTargetsLocation,
                        visitedTargets,
                        isFailureContext: isFailureContext);
                }
            }
        }

        foreach (TargetSpecification beforeTarget in project.GetTargetsWhichRunBefore(target.Name))
        {
            preBodyFailureStates?.Add(
                new FailureState(CaptureBranchState(), CallTargetScope: null));
            ValidateTarget(
                project,
                beforeTarget.TargetName,
                beforeTarget.ReferenceLocation,
                visitedTargets,
                isFailureContext: isFailureContext);
        }

        if (targetExecutes)
        {
            BatchingValidationResult targetBatchingResult = ValidateBatching(
                target,
                [target.Inputs, target.Outputs, target.Returns],
                implicitItemType: null,
                target.Location,
                $"target '{target.Name}'");

            LegacyCallTargetScope? callTargetScope = ContainsCallTarget(target)
                ? new LegacyCallTargetScope(CaptureState())
                : null;
            List<FailureState> failureStates;
            if (target.OnErrorChildren.Count == 0)
            {
                ValidateInBuckets(
                    targetBatchingResult,
                    _ => ValidateTargetBody(
                        project,
                        target,
                        visitedTargets,
                        callTargetScope,
                        collectFailureStates: false));
                failureStates = [];
            }
            else
            {
                ValidatorState targetEntryState = CaptureBranchState();
                List<ValidatedBucket<TargetBodyValidationResult>> targetBuckets =
                    ValidateInBucketsWithResults(
                        targetBatchingResult,
                        _ => ValidateTargetBody(
                            project,
                            target,
                            visitedTargets,
                            callTargetScope,
                            collectFailureStates: true));
                failureStates =
                    MaterializeTargetFailureStates(targetEntryState, targetBuckets);
            }

            if (preBodyFailureStates is not null)
            {
                failureStates.InsertRange(0, preBodyFailureStates);
            }

            string? returnExpression = target.Returns;
            IElementLocation returnLocation = target.ReturnsLocation;
            string returnAttribute = "Returns";
            if (returnExpression is null && !target.ParentProjectSupportsReturnsAttribute)
            {
                returnExpression = target.Outputs;
                returnLocation = target.OutputsLocation;
                returnAttribute = "Outputs";
            }

            if (!string.IsNullOrEmpty(returnExpression))
            {
                BatchingValidationResult returnBatchingResult = ValidateBatching(
                    target,
                    [target.Inputs, target.Outputs, target.Returns],
                    implicitItemType: null,
                    target.Location,
                    $"return value of target '{target.Name}'");
                ValidateInBuckets(
                    returnBatchingResult,
                    batchingState =>
                    {
                        ExpressionValidationResult returnResult = ValidateExpression(
                            target,
                            returnExpression,
                            returnLocation,
                            $"the {returnAttribute} attribute of target '{target.Name}'",
                            requireStatic: false,
                            isCondition: false,
                            metadataBatchingValidated: true,
                            includeItemMetadata: true);
                        ValueState returnState = ValueState.Combine(batchingState, returnResult.State);
                        targetResult = ValueState.Combine(
                            targetResult,
                            returnResult.CanEvaluate
                                ? returnState
                                : ToBlocked(
                                    returnState,
                                    $"return value of target '{target.Name}' is unavailable"));
                    });
            }

            if (failureStates.Count > 0)
            {
                ValidatorState successfulState = CaptureState();
                var successfulTargetResults = new Dictionary<string, ValueState>(
                    _targetResults,
                    MSBuildNameIgnoreCaseComparer.Default);
                try
                {
                    List<ValidatorState> completedFailureStates = new(failureStates.Count);
                    foreach (FailureState failureState in failureStates)
                    {
                        completedFailureStates.Add(CompleteLegacyCallTargetScope(failureState));
                    }

                    RestoreState(
                        JoinStates(
                            completedFailureStates,
                            $"failure paths of target '{target.Name}'"));
                    ValidateOnErrorTargets(
                        project,
                        target,
                        isFailureContext
                            ? visitedTargets
                            : new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default));
                }
                finally
                {
                    RestoreState(successfulState);
                    _targetResults.Clear();
                    foreach (KeyValuePair<string, ValueState> targetResultEntry in successfulTargetResults)
                    {
                        _targetResults.Add(targetResultEntry.Key, targetResultEntry.Value);
                    }
                }
            }

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
                isAfterTarget: true,
                isFailureContext: isFailureContext);
        }

        _activeTargets.Remove(targetName);
        return targetResult;
    }

    private TargetBodyValidationResult ValidateTargetBody(
        ProjectInstance project,
        ProjectTargetInstance target,
        HashSet<string> visitedTargets,
        LegacyCallTargetScope? callTargetScope,
        bool collectFailureStates)
    {
        List<FailureState> failureStates = [];
        foreach (ProjectTargetInstanceChild child in target.Children)
        {
            switch (child)
            {
                case ProjectPropertyGroupTaskInstance propertyGroup:
                    ValidatePropertyGroup(
                        propertyGroup,
                        target.Name,
                        callTargetScope,
                        collectFailureStates ? failureStates : null);
                    break;

                case ProjectItemGroupTaskInstance itemGroup:
                    ValidateItemGroup(
                        itemGroup,
                        target.Name,
                        callTargetScope,
                        collectFailureStates ? failureStates : null);
                    break;

                case ProjectTaskInstance task:
                    failureStates.AddRange(
                        ValidateTask(
                            project,
                            task,
                            target,
                            visitedTargets,
                            callTargetScope,
                            collectFailureStates));
                    break;

                default:
                    ReportUnsupported(child.Location, child.GetType().Name, $"target '{target.Name}'");
                    break;
            }
        }

        return new TargetBodyValidationResult(failureStates);
    }

    private void ValidatePropertyGroup(
        ProjectPropertyGroupTaskInstance propertyGroup,
        string targetName,
        LegacyCallTargetScope? callTargetScope,
        List<FailureState>? failureStates)
    {
        ConditionValidationResult groupConditionResult = ValidateCondition(
            propertyGroup,
            propertyGroup.Condition,
            propertyGroup.ConditionLocation,
            $"the condition of a PropertyGroup in target '{targetName}'");
        if (groupConditionResult.MayThrow)
        {
            failureStates?.Add(CaptureFailureState(callTargetScope));
        }

        if (groupConditionResult.IsKnown && !groupConditionResult.Value)
        {
            return;
        }

        foreach (ProjectPropertyGroupTaskPropertyInstance property in propertyGroup.Properties)
        {
            if (MayThrowDuringExpansion(property.Condition) ||
                MayThrowDuringExpansion(property.Value))
            {
                failureStates?.Add(CaptureFailureState(callTargetScope));
            }

            BatchingValidationResult batchingResult = ValidateBatching(
                property,
                [property.Value, property.Condition],
                implicitItemType: null,
                property.Location,
                $"property '{property.Name}'");

            ValidateInBuckets(
                batchingResult,
                batchingState => ValidateProperty(
                    property,
                    groupConditionResult.IsKnown,
                    groupConditionResult.Value,
                    callTargetScope,
                    batchingState));
        }
    }

    private void ValidateProperty(
        ProjectPropertyGroupTaskPropertyInstance property,
        bool groupConditionKnown,
        bool groupConditionValue,
        LegacyCallTargetScope? callTargetScope,
        ValueState batchingState)
    {
        ConditionValidationResult conditionResult = ValidateCondition(
            property,
            property.Condition,
            property.ConditionLocation,
            $"the condition of property '{property.Name}'",
            metadataBatchingValidated: true);
        if (conditionResult.IsKnown && !conditionResult.Value)
        {
            return;
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
            conditionResult.IsKnown &&
            conditionResult.Value;
        callTargetScope?.CallerAssignedProperties.Add(property.Name);
        HardenedValue<string> propertyValue;
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
            propertyValue = HardenedValue<string>.Static(expandedValue);
        }
        else
        {
            ValueState nonStaticState = propertyState.IsStatic
                ? ValueState.Blocked(
                    new ValueOrigin($"concrete value of property '{property.Name}' is unavailable"))
                : propertyState;
            propertyValue = HardenedValue<string>.NonStatic(nonStaticState);
        }

        Context.SetProperty(property.Name, propertyValue, overwrite: overwrites);
    }

    private void ValidateItemGroup(
        ProjectItemGroupTaskInstance itemGroup,
        string targetName,
        LegacyCallTargetScope? callTargetScope,
        List<FailureState>? failureStates)
    {
        ConditionValidationResult groupConditionResult = ValidateCondition(
            itemGroup,
            itemGroup.Condition,
            itemGroup.ConditionLocation,
            $"the condition of an ItemGroup in target '{targetName}'");
        if (groupConditionResult.MayThrow)
        {
            failureStates?.Add(CaptureFailureState(callTargetScope));
        }

        if (groupConditionResult.IsKnown && !groupConditionResult.Value)
        {
            return;
        }

        foreach (ProjectItemGroupTaskItemInstance item in itemGroup.Items)
        {
            if (MayThrowDuringExpansion(item.Condition) ||
                MayThrowDuringExpansion(item.Include) ||
                MayThrowDuringExpansion(item.Exclude) ||
                MayThrowDuringExpansion(item.Remove))
            {
                failureStates?.Add(CaptureFailureState(callTargetScope));
            }

            callTargetScope?.CallerAssignedItemTypes.Add(item.ItemType);
            ValidateItemOperation(item, targetName);
        }
    }

    private List<FailureState> ValidateTask(
        ProjectInstance project,
        ProjectTaskInstance task,
        ProjectTargetInstance target,
        HashSet<string> visitedTargets,
        LegacyCallTargetScope? callTargetScope,
        bool collectFailureStates)
    {
        List<string> batchableExpressions = [];
        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            AddIfNotEmpty(batchableExpressions, parameter.Value.Item1);
        }

        foreach (ProjectTaskInstanceChild output in task.Outputs)
        {
            AddIfNotEmpty(batchableExpressions, GetTaskParameter(output));
            AddIfNotEmpty(batchableExpressions, GetOutputDestination(output));
            AddIfNotEmpty(batchableExpressions, output.Condition);
        }

        AddIfNotEmpty(batchableExpressions, task.Condition);
        AddIfNotEmpty(batchableExpressions, task.ContinueOnError);
        AddIfNotEmpty(batchableExpressions, task.MSBuildRuntime);
        AddIfNotEmpty(batchableExpressions, task.MSBuildArchitecture);

        BatchingValidationResult batchingResult = ValidateBatching(
            task,
            batchableExpressions,
            implicitItemType: null,
            task.Location,
            $"task '{task.Name}'");

        if (!collectFailureStates)
        {
            ValidateInBuckets(
                batchingResult,
                batchingState =>
                    ValidateTaskCore(
                        project,
                        task,
                        target,
                        visitedTargets,
                        callTargetScope,
                        batchingState));
            return [];
        }

        ValidatorState taskEntryState = CaptureBranchState();
        LegacyCallTargetScope? taskEntryCallTargetScope = callTargetScope?.Clone();
        List<ValidatedBucket<TaskValidationResult>> taskBuckets =
            ValidateInBucketsWithResults(
            batchingResult,
            batchingState =>
                ValidateTaskCore(
                    project,
                    task,
                    target,
                    visitedTargets,
                    callTargetScope,
                    batchingState));

        return MaterializeTaskFailureStates(
            task,
            taskEntryState,
            taskEntryCallTargetScope,
            taskBuckets);
    }

    private TaskValidationResult ValidateTaskCore(
        ProjectInstance project,
        ProjectTaskInstance task,
        ProjectTargetInstance target,
        HashSet<string> visitedTargets,
        LegacyCallTargetScope? callTargetScope,
        ValueState taskBatchingState)
    {
        ConditionValidationResult taskConditionResult = ValidateCondition(
            task,
            task.Condition,
            task.ConditionLocation,
            $"the condition of task '{task.Name}'",
            metadataBatchingValidated: true);

        if (taskConditionResult.IsKnown && !taskConditionResult.Value)
        {
            return new TaskValidationResult(
                CanStopOnFailure: false,
                Executed: false,
                callTargetScope?.Clone());
        }

        ExpressionValidationResult continueOnErrorResult = ValidateExpression(
            task,
            task.ContinueOnError,
            task.ContinueOnErrorLocation,
            $"ContinueOnError of task '{task.Name}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        bool canStopOnFailure = CanTaskStopOnFailure(task, continueOnErrorResult);

        ExpressionValidationResult runtimeResult = ValidateExpression(
            task,
            task.MSBuildRuntime,
            task.MSBuildRuntimeLocation ?? task.Location,
            $"MSBuildRuntime of task '{task.Name}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult architectureResult = ValidateExpression(
            task,
            task.MSBuildArchitecture,
            task.MSBuildArchitectureLocation ?? task.Location,
            $"MSBuildArchitecture of task '{task.Name}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);

        ValueState taskControlState = ValueState.Combine(
            taskBatchingState,
            ValueState.Combine(
                taskConditionResult.State,
                ValueState.Combine(
                    continueOnErrorResult.State,
                    ValueState.Combine(runtimeResult.State, architectureResult.State))));

        HardenedTaskClassification classification = HardenedTaskClassification.Unaudited;
        if (runtimeResult.State.IsStatic && architectureResult.State.IsStatic)
        {
            TaskHostParameters taskIdentityParameters =
                TaskBuilder.GatherTaskIdentityParameters(
                    task,
                    ConcreteExpander,
                    _activeMetadata is null
                        ? ExpanderOptions.ExpandPropertiesAndItems
                        : ExpanderOptions.ExpandAll);
            if (_taskClassifier is not null)
            {
                classification = _taskClassifier(task, taskIdentityParameters);
            }
            else
            {
                if (!_taskClassifications.TryGetValue(task.Name, out classification))
                {
                    classification = HardenedTaskClassification.Unaudited;
                }
            }
        }

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

        HardenedPureTaskExecutor? pureTaskExecutor = _pureTaskExecutor;
        bool pureTaskExecuted = false;
        if (pureTaskExecutor is not null &&
            classification == HardenedTaskClassification.Pure &&
            taskControlState.IsStatic)
        {
            pureTaskExecutor(target, task, ValidationLookup);
            pureTaskExecuted = true;
        }

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
                reportDiagnostics: false).State;

            ConditionValidationResult outputConditionResult = ValidateCondition(
                output,
                output.Condition,
                output.ConditionLocation,
                $"the condition of output '{GetTaskParameter(output)}' from task '{task.Name}'",
                metadataBatchingValidated: true);

            if (outputConditionResult.IsKnown && !outputConditionResult.Value)
            {
                continue;
            }

            ExpressionValidationResult outputTaskParameterResult = ValidateExpression(
                output,
                outputTaskParameter,
                output.TaskParameterLocation,
                $"TaskParameter '{outputTaskParameter}' of output from task '{task.Name}'",
                requireStatic: true,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);

            ValueState outputDestinationBatchingState = ValidateBatching(
                output,
                [outputDestinationExpression],
                implicitItemType: null,
                output.Location,
                $"output destination of task '{task.Name}'",
                reportDiagnostics: false).State;

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
                    ReportUnsupported(output.Location, output.GetType().Name, $"target '{target.Name}'");
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
            else if (pureTaskExecuted)
            {
                outputState = ValueState.Static;
            }
            else
            {
                outputState = ValueState.Deferred(origin);
            }
            switch (output)
            {
                case ProjectTaskOutputPropertyInstance propertyOutput:
                    callTargetScope?.CallerAssignedProperties.Add(destination);
                    if (pureTaskExecuted && outputState.IsStatic)
                    {
                        Context.SetConcreteProperty(
                            destination,
                            ValidationLookup.GetProperty(destination)?.EvaluatedValue ?? string.Empty);
                    }
                    else
                    {
                        Context.SetProperty(
                            destination,
                            HardenedValue<string>.NonStatic(outputState));
                    }

                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    callTargetScope?.CallerAssignedItemTypes.Add(destination);
                    Context.AddTaskOutputItems(destination, outputState);
                    break;
            }
        }

        callTargetScope?.CallerAssignedProperties.Add(ReservedPropertyNames.lastTaskResult);
        if (pureTaskExecuted)
        {
            Context.SetConcreteProperty(
                ReservedPropertyNames.lastTaskResult,
                ValidationLookup.GetProperty(ReservedPropertyNames.lastTaskResult)?.EvaluatedValue ?? "true");
        }
        else
        {
            ValueState taskStatus = ValueState.Deferred(new ValueOrigin($"result of task '{task.Name}'"));
            Context.SetProperty(
                ReservedPropertyNames.lastTaskResult,
                HardenedValue<string>.NonStatic(taskStatus),
                overwrite: true);
        }

        return new TaskValidationResult(
            canStopOnFailure,
            Executed: !pureTaskExecuted,
            callTargetScope?.Clone());
    }

    private bool CanTaskStopOnFailure(
        ProjectTaskInstance task,
        ExpressionValidationResult continueOnErrorResult)
    {
        if (string.IsNullOrEmpty(task.ContinueOnError))
        {
            return true;
        }

        if (!continueOnErrorResult.CanEvaluate ||
            !TryExpandConcreteExpression(
                task,
                task.ContinueOnError,
                task.ContinueOnErrorLocation,
                $"ContinueOnError of task '{task.Name}'",
                reportUnmodeled: false,
                out string expandedValue))
        {
            return true;
        }

        if (string.Equals(
                XMakeAttributes.ContinueOnErrorValues.errorAndContinue,
                expandedValue,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                XMakeAttributes.ContinueOnErrorValues.warnAndContinue,
                expandedValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(
                XMakeAttributes.ContinueOnErrorValues.errorAndStop,
                expandedValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return !ConversionUtilities.ConvertStringToBool(expandedValue);
        }
        catch (ArgumentException)
        {
            return true;
        }
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
        IMetadataTable? callerMetadata = _activeMetadata;
        ValueState targetOutputs = ValueState.Static;

        try
        {
            callTargetScope.WasInvoked = true;
            _activeMetadata = null;
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
            _activeMetadata = callerMetadata;
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

        RestoreState(
            CompleteLegacyCallTargetScope(
                new FailureState(CaptureBranchState(), callTargetScope.Clone())));
    }

    private ValidatorState CaptureState()
        => new(ValidationLookup.SnapshotHardenedLookup());

    private ValidatorState CaptureBranchState()
        => new(ValidationLookup.SnapshotHardenedLookupForBranching());

    private static ValidatorState CloneState(ValidatorState state)
        => new(state.Lookup.SnapshotHardenedLookupForBranching());

    private FailureState CaptureFailureState(LegacyCallTargetScope? callTargetScope)
        => new(CaptureBranchState(), callTargetScope?.Clone());

    private void RestoreState(ValidatorState state)
    {
        _validationLookup = state.Lookup;
        _concreteExpander = CreateConcreteExpander(_validationLookup);
    }

    private List<FailureState> MaterializeTaskFailureStates(
        ProjectTaskInstance task,
        ValidatorState taskEntryState,
        LegacyCallTargetScope? taskEntryCallTargetScope,
        IReadOnlyList<ValidatedBucket<TaskValidationResult>> taskBuckets)
    {
        List<FailureState> failureStates = [];
        ValidatorState reachableState = CloneState(taskEntryState);
        LegacyCallTargetScope? reachableCallTargetScope = taskEntryCallTargetScope;

        foreach (ValidatedBucket<TaskValidationResult> bucket in taskBuckets)
        {
            ValidatorState preTaskState = CloneState(reachableState);
            ValidatorState postTaskState = bucket.HasScope
                ? CloneState(reachableState)
                : CloneState(bucket.State);
            if (bucket.HasScope)
            {
                postTaskState.Lookup.ApplyHardenedScopeSnapshot(bucket.State.Lookup);
            }

            if (!bucket.Result.Executed)
            {
                reachableState = postTaskState;
                reachableCallTargetScope = bucket.Result.CallTargetScope;
            }
            else if (bucket.Result.CanStopOnFailure)
            {
                failureStates.Add(
                    new FailureState(
                        preTaskState,
                        reachableCallTargetScope?.Clone()));

                ValidatorState falseReturnState = CloneState(postTaskState);
                falseReturnState.Context.SetConcreteProperty(
                    ReservedPropertyNames.lastTaskResult,
                    "false",
                    updateLookup: true);
                failureStates.Add(
                    new FailureState(
                        falseReturnState,
                        bucket.Result.CallTargetScope?.Clone()));

                reachableState = postTaskState;
                reachableCallTargetScope = bucket.Result.CallTargetScope;
            }
            else
            {
                reachableState = JoinStates(
                    [preTaskState, postTaskState],
                    $"continuing outcomes of task '{task.Name}'");
                reachableCallTargetScope = bucket.Result.CallTargetScope;
            }
        }

        bool requiresContinuationJoin = false;
        foreach (ValidatedBucket<TaskValidationResult> bucket in taskBuckets)
        {
            requiresContinuationJoin |=
                bucket.Result.Executed &&
                !bucket.Result.CanStopOnFailure;
        }

        if (requiresContinuationJoin)
        {
            ValueState taskStatus = ValueState.Deferred(new ValueOrigin($"result of task '{task.Name}'"));
            reachableState.Context.SetProperty(
                ReservedPropertyNames.lastTaskResult,
                HardenedValue<string>.NonStatic(taskStatus),
                overwrite: true);
            ApplyJoinedAvailability(reachableState);
        }

        return failureStates;
    }

    private static bool MayThrowDuringExpansion(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return false;
        }

        int propertyStart = expression!.IndexOf("$(", StringComparison.Ordinal);
        while (propertyStart >= 0)
        {
            int propertyEnd = expression.IndexOf(')', propertyStart + 2);
            if (propertyEnd < 0)
            {
                return true;
            }

            ReadOnlySpan<char> propertyExpression =
                expression.AsSpan(propertyStart + 2, propertyEnd - propertyStart - 2);
            if (propertyExpression.StartsWith("[", StringComparison.Ordinal) ||
                propertyExpression.Contains(".", StringComparison.Ordinal))
            {
                return true;
            }

            propertyStart = expression.IndexOf("$(", propertyEnd + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private void ApplyJoinedAvailability(ValidatorState source)
    {
        var propertyNames = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        var itemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        source.Context.CollectTrackedNames(propertyNames, itemTypes);

        foreach (string propertyName in propertyNames)
        {
            HardenedValue<string> value = source.Context.GetProperty(propertyName);
            if (!value.IsStatic)
            {
                Context.SetProperty(propertyName, value, overwrite: true);
            }
        }

        foreach (string itemType in itemTypes)
        {
            ValueState state = source.Context.GetItemValue(itemType, includeMetadata: true);
            if (!state.IsStatic)
            {
                Context.SetItemMembershipNonStatic(
                    itemType,
                    state,
                    $"continuing outcomes for item '{itemType}'");
            }
        }
    }

    private static List<FailureState> MaterializeTargetFailureStates(
        ValidatorState targetEntryState,
        IReadOnlyList<ValidatedBucket<TargetBodyValidationResult>> targetBuckets)
    {
        List<FailureState> failureStates = [];
        ValidatorState reachableState = CloneState(targetEntryState);

        foreach (ValidatedBucket<TargetBodyValidationResult> bucket in targetBuckets)
        {
            foreach (FailureState bucketFailureState in bucket.Result.FailureStates)
            {
                if (!bucket.HasScope)
                {
                    failureStates.Add(
                        new FailureState(
                            CloneState(bucketFailureState.State),
                            bucketFailureState.CallTargetScope?.Clone()));
                    continue;
                }

                ValidatorState failureState = CloneState(reachableState);
                failureState.Lookup.ApplyHardenedScopeSnapshot(bucketFailureState.State.Lookup);
                failureStates.Add(
                    new FailureState(
                        failureState,
                        bucketFailureState.CallTargetScope?.Clone()));
            }

            if (bucket.HasScope)
            {
                reachableState.Lookup.ApplyHardenedScopeSnapshot(bucket.State.Lookup);
            }
            else
            {
                reachableState = CloneState(bucket.State);
            }
        }

        return failureStates;
    }

    private static ValidatorState CompleteLegacyCallTargetScope(FailureState failureState)
    {
        LegacyCallTargetScope? callTargetScope = failureState.CallTargetScope;
        if (callTargetScope is null || !callTargetScope.WasInvoked)
        {
            return CloneState(failureState.State);
        }

        ValidatorState mergedState = CloneState(callTargetScope.CalledState);
        foreach (string propertyName in callTargetScope.CallerAssignedProperties)
        {
            mergedState.Context.CopyPropertyFrom(failureState.State.Context, propertyName);
        }

        foreach (string itemType in callTargetScope.CallerAssignedItemTypes)
        {
            mergedState.Context.CopyItemFrom(failureState.State.Context, itemType);
        }

        return mergedState;
    }

    private static ValidatorState JoinStates(
        IReadOnlyList<ValidatorState> states,
        string description)
    {
        if (states.Count == 0)
        {
            throw new ArgumentException("At least one state is required.", nameof(states));
        }

        ValidatorState joined = CloneState(states[0]);
        if (states.Count == 1)
        {
            return joined;
        }

        var propertyNames = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        var itemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        foreach (ValidatorState state in states)
        {
            state.Context.CollectTrackedNames(propertyNames, itemTypes);
        }

        foreach (string propertyName in propertyNames)
        {
            HardenedValue<string> first = states[0].Context.GetProperty(propertyName);
            ValueState combinedState = first.State;
            bool staticValuesMatch = first.TryGetStaticValue(out string? staticValue);

            for (int i = 1; i < states.Count; i++)
            {
                HardenedValue<string> value = states[i].Context.GetProperty(propertyName);
                combinedState = ValueState.Combine(combinedState, value.State);
                staticValuesMatch =
                    staticValuesMatch &&
                    value.TryGetStaticValue(out string? candidate) &&
                    string.Equals(staticValue, candidate, StringComparison.Ordinal);
            }

            if (staticValuesMatch)
            {
                continue;
            }

            ValueState joinedState = combinedState.IsStatic
                ? ValueState.Deferred(
                    new ValueOrigin(
                        $"property '{propertyName}' may have different values across {description}"))
                : combinedState.WithOrigin(description);
            joined.Context.SetProperty(
                propertyName,
                HardenedValue<string>.NonStatic(joinedState),
                overwrite: true);
        }

        foreach (string itemType in itemTypes)
        {
            ValueState combinedState = states[0].Context.GetItemValue(itemType, includeMetadata: true);
            bool concreteItemsMatch = true;
            for (int i = 1; i < states.Count; i++)
            {
                combinedState = ValueState.Combine(
                    combinedState,
                    states[i].Context.GetItemValue(itemType, includeMetadata: true));
                concreteItemsMatch &=
                    HaveEquivalentItems(states[0].Lookup, states[i].Lookup, itemType);
            }

            if (!concreteItemsMatch)
            {
                joined.Context.SetItemMembershipNonStatic(
                    itemType,
                    ValueState.Deferred(
                        new ValueOrigin(
                            $"item '{itemType}' may have different values across {description}")),
                    description);
            }
            else if (!combinedState.IsStatic)
            {
                joined.Context.SetItemMembershipNonStatic(
                    itemType,
                    combinedState,
                    description);
            }
        }

        return joined;
    }

    private static bool HaveEquivalentItems(Lookup left, Lookup right, string itemType)
    {
        ICollection<ProjectItemInstance> leftItems = left.GetItems(itemType);
        ICollection<ProjectItemInstance> rightItems = right.GetItems(itemType);
        if (leftItems.Count != rightItems.Count)
        {
            return false;
        }

        using IEnumerator<ProjectItemInstance> leftEnumerator = leftItems.GetEnumerator();
        using IEnumerator<ProjectItemInstance> rightEnumerator = rightItems.GetEnumerator();
        while (leftEnumerator.MoveNext())
        {
            if (!rightEnumerator.MoveNext() ||
                !HaveEquivalentItem(leftEnumerator.Current, rightEnumerator.Current))
            {
                return false;
            }
        }

        return !rightEnumerator.MoveNext();
    }

    private static bool HaveEquivalentItem(
        ProjectItemInstance left,
        ProjectItemInstance right)
    {
        if (!string.Equals(left.EvaluatedInclude, right.EvaluatedInclude, StringComparison.Ordinal))
        {
            return false;
        }

        IDictionary leftMetadata = ((ITaskItem)left).CloneCustomMetadata();
        IDictionary rightMetadata = ((ITaskItem)right).CloneCustomMetadata();
        if (leftMetadata.Count != rightMetadata.Count)
        {
            return false;
        }

        foreach (DictionaryEntry metadata in leftMetadata)
        {
            if (!rightMetadata.Contains(metadata.Key) ||
                !string.Equals(
                    metadata.Value as string,
                    rightMetadata[metadata.Key] as string,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ValidateOnErrorTargets(
        ProjectInstance project,
        ProjectTargetInstance target,
        HashSet<string> visitedTargets)
    {
        foreach (ProjectOnErrorInstance onError in target.OnErrorChildren)
        {
            ConditionValidationResult conditionResult = ValidateCondition(
                onError,
                onError.Condition,
                onError.ConditionLocation,
                $"the condition of OnError in target '{target.Name}'",
                allowTaskStatus: true);
            if (conditionResult.IsKnown && !conditionResult.Value)
            {
                continue;
            }

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
                    ValidateTarget(
                        project,
                        errorTarget,
                        onError.ExecuteTargetsLocation,
                        visitedTargets,
                        isFailureContext: true);
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
            if (!Context.GetProperty(propertyName).IsStatic)
            {
                return true;
            }
        }

        return false;
    }

    private ConditionValidationResult ValidateCondition(
        object owner,
        string condition,
        IElementLocation? location,
        string context,
        bool metadataBatchingValidated = false,
        bool allowTaskStatus = false,
        string? implicitItemType = null,
        bool conditionSyntaxValid = true)
    {
        if (condition.Length == 0)
        {
            return new ConditionValidationResult(
                ValueState.Static,
                IsKnown: true,
                Value: true,
                MayThrow: false);
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        ElementLocation conditionLocation =
            effectiveLocation as ElementLocation ?? ElementLocation.EmptyLocation;
        if (!conditionSyntaxValid)
        {
            ExpressionValidationResult result = ValidateExpression(
                owner,
                condition,
                effectiveLocation,
                context,
                requireStatic: false,
                isCondition: true,
                metadataBatchingValidated,
                allowTaskStatus: allowTaskStatus,
                implicitItemType: implicitItemType);
            return new ConditionValidationResult(
                ToBlocked(result.State, $"condition in {context} could not be evaluated"),
                IsKnown: false,
                Value: false,
                MayThrowDuringExpansion(condition));
        }

        ValueState state = ValueState.Static;
        bool canEvaluate = true;
        bool mayThrow = false;

        bool CanExpandExpression(string expression)
        {
            mayThrow |= MayThrowDuringExpansion(expression);
            ExpressionValidationResult result = ValidateExpression(
                owner,
                expression,
                effectiveLocation,
                context,
                requireStatic: false,
                isCondition: true,
                metadataBatchingValidated,
                allowTaskStatus: allowTaskStatus,
                implicitItemType: implicitItemType);
            state = ValueState.Combine(state, result.State);
            canEvaluate &= result.CanEvaluate;
            return result.CanEvaluate &&
                result.State.IsStatic &&
                CanExpandConcreteExpression(owner, expression);
        }

        bool CanEvaluateFunction(string functionName)
        {
            if (!MSBuildNameIgnoreCaseComparer.Default.Equals(functionName, "Exists"))
            {
                return true;
            }

            ReportProhibitedFunction(effectiveLocation, "Exists", context);
            state = ValueState.Blocked(new ValueOrigin($"unsupported expression in {context}"));
            canEvaluate = false;
            return false;
        }

        ConditionEvaluationResult evaluationResult;
        try
        {
            ExpanderOptions expanderOptions = _activeMetadata is null
                ? ExpanderOptions.ExpandPropertiesAndItems
                : ExpanderOptions.ExpandAll;
            evaluationResult = ConditionEvaluator.EvaluateConditionPartially(
                condition,
                ParserOptions.AllowAll,
                ConcreteExpander,
                expanderOptions,
                ProjectDirectory,
                conditionLocation,
                FileSystems.Default,
                CanExpandExpression,
                CanEvaluateFunction);
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            state = ToBlocked(state, $"condition in {context} could not be evaluated");
            canEvaluate = false;
            evaluationResult = ConditionEvaluationResult.Deferred;
        }

        bool isKnown =
            canEvaluate &&
            evaluationResult != ConditionEvaluationResult.Deferred;
        if (!isKnown)
        {
            RequireStatic(state, effectiveLocation, context, condition);
        }

        return new ConditionValidationResult(
            isKnown ? ValueState.Static : state,
            isKnown,
            evaluationResult == ConditionEvaluationResult.KnownTrue,
            mayThrow);
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
            ParserOptions parserOptions = _activeMetadata is null
                ? ParserOptions.AllowPropertiesAndItemLists
                : ParserOptions.AllowAll;
            ExpanderOptions expanderOptions = _activeMetadata is null
                ? ExpanderOptions.ExpandPropertiesAndItems
                : ExpanderOptions.ExpandAll;
            result = ConditionEvaluator.EvaluateCondition(
                condition,
                parserOptions,
                ConcreteExpander,
                expanderOptions,
                ProjectDirectory,
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
        => TryExpandConcreteExpression(
            owner,
            expression,
            location,
            context,
            reportUnmodeled,
            unescape: true,
            out expanded);

    private bool TryExpandConcreteExpressionLeaveEscaped(
        object owner,
        string expression,
        IElementLocation? location,
        string context,
        bool reportUnmodeled,
        out string expanded)
        => TryExpandConcreteExpression(
            owner,
            expression,
            location,
            context,
            reportUnmodeled,
            unescape: false,
            out expanded);

    private bool TryExpandConcreteExpression(
        object owner,
        string expression,
        IElementLocation? location,
        string context,
        bool reportUnmodeled,
        bool unescape,
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
            ExpanderOptions options = _activeMetadata is null
                ? ExpanderOptions.ExpandPropertiesAndItems
                : ExpanderOptions.ExpandAll;
            expanded = unescape
                ? ConcreteExpander.ExpandIntoStringAndUnescape(
                    expression,
                    options,
                    effectiveLocation)
                : ConcreteExpander.ExpandIntoStringLeaveEscaped(
                    expression,
                    options,
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

        if (descriptor.BatchMetadata is not null && _activeMetadata is null)
        {
            return false;
        }

        if (!descriptor.ItemTypes.IsEmpty)
        {
            foreach (string itemType in descriptor.ItemTypes)
            {
                if (!Context.GetItemValue(itemType, includeMetadata: false).IsStatic)
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
                state = ValueState.Combine(
                    state,
                    Context.GetItemValue(itemType, includeMetadata: false));
            }
        }

        state = ValueState.Combine(
            state,
            FindTransformMetadataState(
                owner,
                descriptor,
                effectiveLocation,
                context,
                includeItemMetadata));

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
                state = ValueState.Combine(state, Context.GetProperty(propertyName).State);
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

        BatchingValidationResult batchingResult = ValidateBatching(
            item,
            batchableExpressions,
            item.ItemType,
            item.Location,
            $"item '{item.ItemType}' in target '{targetName}'");

        ValidateInBuckets(
            batchingResult,
            batchingState => ValidateItemOperation(item, batchingState));
    }

    private void ValidateItemOperation(
        ProjectItemGroupTaskItemInstance item,
        ValueState batchingState)
    {
        ConditionValidationResult conditionResult = ValidateCondition(
            item,
            item.Condition,
            item.ConditionLocation,
            $"the condition of item '{item.ItemType}'",
            metadataBatchingValidated: true,
            implicitItemType: item.ItemType);
        if (conditionResult.IsKnown && !conditionResult.Value)
        {
            return;
        }

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

        Dictionary<string, HardenedValue<string>> assignedMetadata = ValidateMetadataAssignments(item);
        string? evaluatedMatchOnMetadata = TryExpandConcreteExpression(
            item,
            item.MatchOnMetadata,
            item.MatchOnMetadataLocation,
            $"MatchOnMetadata on item '{item.ItemType}'",
            reportUnmodeled: false,
            out string expandedMatchOnMetadata)
                ? expandedMatchOnMetadata
                : item.MatchOnMetadata;
        ValueState matchOnMetadataValuesState =
            GetMatchOnMetadataValuesState(item, evaluatedMatchOnMetadata);
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

        HashSet<string>? keepMetadata = ExpandMetadataNames(
            item.KeepMetadata,
            item.KeepMetadataLocation);
        HashSet<string>? removeMetadata = ExpandMetadataNames(
            item.RemoveMetadata,
            item.RemoveMetadataLocation);

        if (isInclude)
        {
            var itemSources = new Dictionary<ProjectItemInstance, ProjectItemInstance>();
            HardenedItemOperationPlan.ExpansionCapture expansionCapture =
                ItemOperationPlan.CreateExpansionCapture();
            List<ProjectItemInstance> itemsToAdd;
            try
            {
                itemsToAdd = ItemGroupIntrinsicTask.ExpandItemIntoItems(
                    Project,
                    item,
                    ConcreteExpander,
                    keepMetadata,
                    removeMetadata,
                    loggingContext: null,
                    itemSources: itemSources,
                    hardenedGlobPolicy: ItemOperationPlan.GlobPolicy,
                    hardenedExpansionCapture: expansionCapture);
            }
            catch (InvalidProjectFileException exception)
            {
                AddDiagnostic(exception);
                Context.BlockItemMembership(
                    item.ItemType,
                    ValueState.Blocked(new ValueOrigin($"invalid Include into item '{item.ItemType}'")),
                    $"item '{item.ItemType}' membership");
                return;
            }

            try
            {
                ItemOperationPlan.Record(
                    ValidationLookup.HardenedBucketPath,
                    item,
                    itemsToAdd,
                    itemSources,
                    expansionCapture);
            }
            catch (InvalidProjectFileException exception)
            {
                AddDiagnostic(exception);
                Context.BlockItemMembership(
                    item.ItemType,
                    ValueState.Blocked(new ValueOrigin($"conflicting Include into item '{item.ItemType}'")),
                    $"item '{item.ItemType}' membership");
                return;
            }

            if (itemsToAdd.Count == 0)
            {
                return;
            }

            var concreteMetadata = new Dictionary<string, string>(
                MSBuildNameIgnoreCaseComparer.Default);
            foreach (KeyValuePair<string, HardenedValue<string>> metadata in assignedMetadata)
            {
                if (metadata.Value.TryGetStaticValue(out string? staticValue))
                {
                    concreteMetadata[metadata.Key] = staticValue;
                }
            }

            ProjectItemInstance.SetMetadata(concreteMetadata, itemsToAdd);

            bool keepDuplicates = false;
            if (item.KeepDuplicates.Length > 0 &&
                !TryEvaluateCondition(
                    item,
                    item.KeepDuplicates,
                    item.KeepDuplicatesLocation,
                    keepDuplicatesResult,
                    out keepDuplicates))
            {
                Context.BlockItemMembership(
                    item.ItemType,
                    keepDuplicatesResult.State,
                    $"duplicate filtering for item '{item.ItemType}'");
                return;
            }

            if (!keepDuplicates)
            {
                ValueState deduplicationState = GetDeduplicationState(
                    item,
                    itemsToAdd,
                    itemSources,
                    assignedMetadata,
                    keepMetadata,
                    removeMetadata);
                if (!deduplicationState.IsStatic)
                {
                    Context.SetItemMembershipNonStatic(
                        item.ItemType,
                        deduplicationState,
                        $"duplicate filtering for item '{item.ItemType}'");
                    return;
                }
            }

            ICollection<ProjectItemInstance> addedItems = ValidationLookup.AddNewItemsOfItemType(
                item.ItemType,
                itemsToAdd,
                doNotAddDuplicates: !keepDuplicates);
            Context.ApplyIncludedItems(
                item.ItemType,
                addedItems,
                itemSources,
                assignedMetadata,
                keepMetadata,
                removeMetadata,
                $"Include into item '{item.ItemType}'");
        }
        else if (isRemove)
        {
            ICollection<ProjectItemInstance> items =
                ValidationLookup.GetItems(item.ItemType) ?? [];
            if (items.Count == 0)
            {
                return;
            }

            List<ProjectItemInstance> itemsToRemove;
            if (TryParseLiteralMetadataNames(
                    evaluatedMatchOnMetadata,
                    out HashSet<string>? matchOnMetadata))
            {
                MatchOnMetadataOptions matchingOptions =
                    MatchOnMetadataConstants.MatchOnMetadataOptionsDefaultValue;
                if (TryExpandConcreteExpression(
                        item,
                        item.MatchOnMetadataOptions,
                        item.MatchOnMetadataOptionsLocation,
                        $"MatchOnMetadataOptions on item '{item.ItemType}'",
                        reportUnmodeled: false,
                        out string expandedMatchingOptions))
                {
                    Enum.TryParse(expandedMatchingOptions, out matchingOptions);
                }

                itemsToRemove = ItemGroupIntrinsicTask.FindItemsMatchingMetadataSpecification(
                    items,
                    item,
                    ConcreteExpander,
                    matchOnMetadata!,
                    matchingOptions,
                    ProjectDirectory);
            }
            else
            {
                itemsToRemove = ItemGroupIntrinsicTask.FindItemsMatchingSpecification(
                    items,
                    item.Remove,
                    item.RemoveLocation,
                    ConcreteExpander,
                    ProjectDirectory,
                    loggingContext: null);
            }

            if (itemsToRemove is { Count: > 0 })
            {
                ValidationLookup.RemoveItems(item.ItemType, itemsToRemove);
            }
        }
        else
        {
            ICollection<ProjectItemInstance> items =
                ValidationLookup.GetItems(item.ItemType) ?? [];
            Lookup.MetadataModifications metadataChanges =
                CreateConcreteMetadataModifications(
                    keepMetadata,
                    removeMetadata,
                    assignedMetadata);
            ValidationLookup.ModifyItems(item.ItemType, items, metadataChanges);
            foreach (KeyValuePair<string, HardenedValue<string>> metadata in assignedMetadata)
            {
                if (!metadata.Value.IsStatic)
                {
                    foreach (ProjectItemInstance selectedItem in items)
                    {
                        Context.SetMetadata(
                            selectedItem,
                            metadata.Key,
                            metadata.Value.State.WithOrigin(
                                $"metadata '{metadata.Key}' update on item '{item.ItemType}'"));
                    }
                }
            }
        }
    }

    private Dictionary<string, HardenedValue<string>> ValidateMetadataAssignments(
        ProjectItemGroupTaskItemInstance item)
    {
        var assignedMetadata =
            new Dictionary<string, HardenedValue<string>>(MSBuildNameIgnoreCaseComparer.Default);
        foreach (ProjectItemGroupTaskMetadataInstance metadata in item.Metadata)
        {
            ConditionValidationResult conditionResult = ValidateCondition(
                item,
                metadata.Condition,
                metadata.ConditionLocation,
                $"the condition of metadata '{metadata.Name}' on item '{item.ItemType}'",
                metadataBatchingValidated: true,
                implicitItemType: item.ItemType);
            if (conditionResult.IsKnown && !conditionResult.Value)
            {
                continue;
            }

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

            if (metadataState.IsStatic &&
                TryExpandConcreteExpressionLeaveEscaped(
                    item,
                    metadata.Value,
                    metadata.Location,
                    $"the value of metadata '{metadata.Name}' on item '{item.ItemType}'",
                    reportUnmodeled: false,
                    out string expandedValue))
            {
                assignedMetadata[metadata.Name] = HardenedValue<string>.Static(expandedValue);
            }
            else
            {
                if (metadataState.IsStatic)
                {
                    metadataState = ValueState.Blocked(
                        new ValueOrigin(
                            $"concrete value of metadata '{metadata.Name}' on item '{item.ItemType}' is unavailable"));
                }

                assignedMetadata[metadata.Name] = HardenedValue<string>.NonStatic(metadataState);
            }
        }

        return assignedMetadata;
    }

    private ValueState GetDeduplicationState(
        ProjectItemGroupTaskItemInstance item,
        ICollection<ProjectItemInstance> itemsToAdd,
        IReadOnlyDictionary<ProjectItemInstance, ProjectItemInstance> itemSources,
        IReadOnlyDictionary<string, HardenedValue<string>> assignedMetadata,
        ISet<string>? keepMetadata,
        ISet<string>? removeMetadata)
    {
        var itemsByIdentity =
            new Dictionary<string, ValueState>(MSBuildNameIgnoreCaseComparer.Default);
        foreach (ProjectItemInstance existingItem in ValidationLookup.GetItems(item.ItemType))
        {
            string identity = ((IItem)existingItem).EvaluatedIncludeEscaped;
            ValueState existingState = Context.GetItemValue(existingItem, includeMetadata: true);
            if (itemsByIdentity.TryGetValue(
                    identity,
                    out ValueState sameIdentityState))
            {
                existingState = ValueState.Combine(existingState, sameIdentityState);
            }

            itemsByIdentity[identity] = existingState;
        }

        ValueState deduplicationState = ValueState.Static;
        foreach (ProjectItemInstance itemToAdd in itemsToAdd)
        {
            string identity = ((IItem)itemToAdd).EvaluatedIncludeEscaped;
            itemSources.TryGetValue(itemToAdd, out ProjectItemInstance? sourceItem);
            ValueState itemState = Context.GetIncludedItemValue(
                item.ItemType,
                sourceItem,
                assignedMetadata,
                keepMetadata,
                removeMetadata,
                $"Include into item '{item.ItemType}'");
            if (itemsByIdentity.TryGetValue(
                    identity,
                    out ValueState sameIdentityState))
            {
                deduplicationState = ValueState.Combine(
                    deduplicationState,
                    ValueState.Combine(itemState, sameIdentityState));
            }

            itemsByIdentity[identity] = itemState;
        }

        return deduplicationState;
    }

    private HashSet<string>? ExpandMetadataNames(
        string expression,
        IElementLocation? location)
    {
        if (expression.Length == 0)
        {
            return null;
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        try
        {
            ExpanderOptions options = _activeMetadata is null
                ? ExpanderOptions.ExpandPropertiesAndItems
                : ExpanderOptions.ExpandAll;
            var metadataNames = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
            foreach (string metadataName in ConcreteExpander.ExpandIntoStringListLeaveEscaped(
                         expression,
                         options,
                         effectiveLocation))
            {
                metadataNames.Add(metadataName);
            }

            return metadataNames.Count == 0 ? null : metadataNames;
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return null;
        }
    }

    private static Lookup.MetadataModifications CreateConcreteMetadataModifications(
        ISet<string>? keepMetadata,
        ISet<string>? removeMetadata,
        IReadOnlyDictionary<string, HardenedValue<string>> assignedMetadata)
    {
        var metadataChanges = new Lookup.MetadataModifications(keepMetadata is not null);
        if (keepMetadata is not null)
        {
            foreach (string metadataName in keepMetadata)
            {
                metadataChanges[metadataName] =
                    Lookup.MetadataModification.CreateFromNoChange();
            }
        }
        else if (removeMetadata is not null)
        {
            foreach (string metadataName in removeMetadata)
            {
                metadataChanges[metadataName] =
                    Lookup.MetadataModification.CreateFromRemove();
            }
        }

        foreach (KeyValuePair<string, HardenedValue<string>> metadata in assignedMetadata)
        {
            if (metadata.Value.TryGetStaticValue(out string? staticValue))
            {
                metadataChanges[metadata.Key] =
                    Lookup.MetadataModification.CreateFromNewValue(staticValue);
            }
        }

        return metadataChanges;
    }

    private ValueState GetMatchOnMetadataValuesState(
        ProjectItemGroupTaskItemInstance item,
        string? matchOnMetadata)
    {
        if (!TryParseLiteralMetadataNames(matchOnMetadata, out HashSet<string>? metadataNames))
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

    private void ValidateInBuckets(
        BatchingValidationResult batchingResult,
        Action<ValueState> validate)
    {
        if (batchingResult.Buckets is null)
        {
            HardenedItemOperationPlan.HardenedBucketPath previousPath =
                ValidationLookup.HardenedBucketPath;
            ValidationLookup.EnterHardenedBucket(batchingResult.Owner, sequenceNumber: 0);
            try
            {
                validate(batchingResult.State);
            }
            finally
            {
                ValidationLookup.RestoreHardenedBucketPath(previousPath);
            }

            return;
        }

        Lookup parentLookup = ValidationLookup;
        Expander<ProjectPropertyInstance, ProjectItemInstance> parentExpander = ConcreteExpander;
        IMetadataTable? parentMetadata = _activeMetadata;
        int initializedBucketCount = 0;
        try
        {
            for (int i = 0; i < batchingResult.Buckets.Count; i++)
            {
                ItemBucket bucket = batchingResult.Buckets[i];
                bucket.Initialize(loggingContext: null);
                initializedBucketCount++;
                _validationLookup = bucket.Lookup;
                _activeMetadata = bucket.Expander.Metadata;
                _concreteExpander = bucket.Expander;
                try
                {
                    validate(batchingResult.State);
                }
                finally
                {
                    _validationLookup = parentLookup;
                    _activeMetadata = parentMetadata;
                    _concreteExpander = parentExpander;
                }
            }
        }
        finally
        {
            for (int i = 0; i < initializedBucketCount; i++)
            {
                batchingResult.Buckets[i].LeaveScope();
            }

            _validationLookup = parentLookup;
            _activeMetadata = parentMetadata;
            _concreteExpander = parentExpander;
        }
    }

    private List<ValidatedBucket<T>> ValidateInBucketsWithResults<T>(
        BatchingValidationResult batchingResult,
        Func<ValueState, T> validate)
    {
        if (batchingResult.Buckets is null)
        {
            HardenedItemOperationPlan.HardenedBucketPath previousPath =
                ValidationLookup.HardenedBucketPath;
            ValidationLookup.EnterHardenedBucket(batchingResult.Owner, sequenceNumber: 0);
            try
            {
                T result = validate(batchingResult.State);
                return [new ValidatedBucket<T>(CaptureBranchState(), result, HasScope: false)];
            }
            finally
            {
                ValidationLookup.RestoreHardenedBucketPath(previousPath);
            }
        }

        List<ValidatedBucket<T>> results = new(batchingResult.Buckets.Count);
        Lookup parentLookup = ValidationLookup;
        Expander<ProjectPropertyInstance, ProjectItemInstance> parentExpander = ConcreteExpander;
        IMetadataTable? parentMetadata = _activeMetadata;
        int initializedBucketCount = 0;
        try
        {
            for (int i = 0; i < batchingResult.Buckets.Count; i++)
            {
                ItemBucket bucket = batchingResult.Buckets[i];
                bucket.Initialize(loggingContext: null);
                initializedBucketCount++;
                _validationLookup = bucket.Lookup;
                _activeMetadata = bucket.Expander.Metadata;
                _concreteExpander = bucket.Expander;
                try
                {
                    T result = validate(batchingResult.State);
                    results.Add(new ValidatedBucket<T>(CaptureBranchState(), result, HasScope: true));
                }
                finally
                {
                    _validationLookup = parentLookup;
                    _activeMetadata = parentMetadata;
                    _concreteExpander = parentExpander;
                }
            }
        }
        finally
        {
            for (int i = 0; i < initializedBucketCount; i++)
            {
                batchingResult.Buckets[i].LeaveScope();
            }

            _validationLookup = parentLookup;
            _activeMetadata = parentMetadata;
            _concreteExpander = parentExpander;
        }

        return results;
    }

    private BatchingValidationResult ValidateBatching(
        object owner,
        IReadOnlyList<string?> expressions,
        string? implicitItemType,
        IElementLocation? location,
        string context,
        bool reportDiagnostics = true)
    {
        bool hasExpression = false;
        List<string> consumedItemTypes = [];
        var seenConsumedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        var metadataReferences = new Dictionary<string, MetadataReference>(MSBuildNameIgnoreCaseComparer.Default);
        List<MetadataReference> orderedMetadataReferences = [];
        for (int i = 0; i < expressions.Count; i++)
        {
            string? expression = expressions[i];
            if (string.IsNullOrEmpty(expression))
            {
                continue;
            }

            hasExpression = true;
            HardenedExpressionDescriptor descriptor =
                _expressionDescriptors.GetOrCreate(owner, expression, implicitItemType);
            foreach (string itemType in descriptor.ItemTypes)
            {
                if (seenConsumedItemTypes.Add(itemType))
                {
                    consumedItemTypes.Add(itemType);
                }
            }

            if (descriptor.BatchMetadata is not null)
            {
                foreach (KeyValuePair<string, MetadataReference> metadata in descriptor.BatchMetadata)
                {
                    if (!metadataReferences.ContainsKey(metadata.Key))
                    {
                        metadataReferences.Add(metadata.Key, metadata.Value);
                        orderedMetadataReferences.Add(metadata.Value);
                    }
                }
            }
        }

        if (!hasExpression)
        {
            return new BatchingValidationResult(ValueState.Static, Buckets: null, owner);
        }

        if (metadataReferences.Count == 0)
        {
            return new BatchingValidationResult(ValueState.Static, Buckets: null, owner);
        }

        if (implicitItemType is not null)
        {
            if (seenConsumedItemTypes.Add(implicitItemType))
            {
                consumedItemTypes.Add(implicitItemType);
            }
        }

        List<string> batchedItemTypes = [];
        var seenBatchedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        BatchingEngine.AddItemTypesToBeBatched(
            orderedMetadataReferences,
            consumedItemTypes,
            batchedItemTypes,
            seenBatchedItemTypes);

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

            return new BatchingValidationResult(
                ValueState.Blocked(new ValueOrigin($"unresolved batching metadata in {context}")),
                Buckets: null,
                owner);
        }

        ValueState state = ValueState.Static;
        bool keysAreStatic = true;
        string batchingContext = $"batching of {context}";
        foreach (string itemType in batchedItemTypes)
        {
            ValueState membership = Context.GetItemMembership(itemType);
            state = ValueState.Combine(state, membership);
            keysAreStatic &= !reportDiagnostics
                ? membership.IsStatic
                : RequireStatic(
                    membership,
                    effectiveLocation,
                    batchingContext,
                    $"membership of item list '@({itemType})'");

            ICollection<ProjectItemInstance> items = ValidationLookup.GetItems(itemType);
            foreach (ProjectItemInstance item in items)
            {
                ValueState identity = Context.GetItemIdentity(item);
                state = ValueState.Combine(state, identity);
                keysAreStatic &= !reportDiagnostics
                    ? identity.IsStatic
                    : RequireStatic(
                        identity,
                        effectiveLocation,
                        batchingContext,
                        $"identity '{item.EvaluatedInclude}' of item '{itemType}'");

                foreach (MetadataReference metadataReference in orderedMetadataReferences)
                {
                    if (MSBuildNameIgnoreCaseComparer.Default.Equals(
                            metadataReference.MetadataName,
                            "Identity") ||
                        (metadataReference.ItemName is not null &&
                         !MSBuildNameIgnoreCaseComparer.Default.Equals(
                             metadataReference.ItemName,
                             itemType)))
                    {
                        continue;
                    }

                    ValueState metadata = Context.GetItemMetadata(item, metadataReference.MetadataName);
                    state = ValueState.Combine(state, metadata);
                    keysAreStatic &= !reportDiagnostics
                        ? metadata.IsStatic
                        : RequireStatic(
                            metadata,
                            effectiveLocation,
                            batchingContext,
                            $"metadata '{metadataReference.MetadataName}' on item '{item.EvaluatedInclude}' in '@({itemType})'");
                }
            }
        }

        if (!keysAreStatic || !reportDiagnostics)
        {
            return new BatchingValidationResult(state, Buckets: null, owner);
        }

        try
        {
            BatchingEngine.BatchingInfo batchingInfo = BatchingEngine.AnalyzeBatching(
                metadataReferences,
                consumedItemTypes,
                ValidationLookup,
                effectiveLocation);
            List<ItemBucket> buckets = BatchingEngine.PrepareBatchingBuckets(
                batchingInfo,
                ValidationLookup,
                effectiveLocation,
                loggingContext: null,
                hardenedBucketOwner: owner);
            return new BatchingValidationResult(state, buckets, owner);
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return new BatchingValidationResult(
                ValueState.Blocked(new ValueOrigin($"invalid batching in {context}")),
                Buckets: null,
                owner);
        }
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
        object owner,
        HardenedExpressionDescriptor descriptor,
        IElementLocation location,
        string context,
        bool includeItemMetadata)
    {
        ValueState state = ValueState.Static;
        foreach (HardenedItemVectorDescriptor itemVector in descriptor.ItemVectors)
        {
            if (includeItemMetadata && itemVector.Transforms.IsEmpty)
            {
                state = ValueState.Combine(state, Context.GetItemValue(itemVector.ItemType, includeMetadata: true));
            }

            bool canExpandPrefix =
                Context.GetItemValue(itemVector.ItemType, includeMetadata: false).IsStatic;
            for (int transformIndex = 0; transformIndex < itemVector.Transforms.Length; transformIndex++)
            {
                HardenedItemTransformDescriptor transform = itemVector.Transforms[transformIndex];
                if (transform.MetadataItemFunctionKind is HardenedMetadataItemFunctionKind functionKind)
                {
                    ValueState functionState = ValidateMetadataItemFunction(
                        owner,
                        itemVector,
                        transformIndex,
                        transform,
                        functionKind,
                        location,
                        context,
                        canExpandPrefix);
                    state = ValueState.Combine(state, functionState);
                    canExpandPrefix &= functionState.IsStatic;
                    continue;
                }

                foreach (MetadataReference metadataReference in transform.Metadata)
                {
                    string itemType = metadataReference.ItemName ?? itemVector.ItemType;
                    ValueState metadataState = Context.GetMetadata(itemType, metadataReference.MetadataName);
                    state = ValueState.Combine(
                        state,
                        metadataState.WithOrigin($"transform of item '{itemVector.ItemType}'"));
                    canExpandPrefix &= metadataState.IsStatic;
                }
            }
        }

        return state;
    }

    private ValueState ValidateMetadataItemFunction(
        object owner,
        HardenedItemVectorDescriptor itemVector,
        int transformIndex,
        HardenedItemTransformDescriptor transform,
        HardenedMetadataItemFunctionKind functionKind,
        IElementLocation location,
        string context,
        bool canExpandPrefix)
    {
        string argumentsExpression = transform.FunctionArguments ?? string.Empty;
        string[] arguments = Expander<ProjectPropertyInstance, ProjectItemInstance>.ExtractFunctionArguments(
            location,
            transform.Expression,
            argumentsExpression.AsMemory());
        int expectedArgumentCount = functionKind is
            HardenedMetadataItemFunctionKind.Metadata or
            HardenedMetadataItemFunctionKind.HasMetadata
                ? 1
                : 2;
        if (arguments.Length != expectedArgumentCount)
        {
            TryExpandItemVectorPrefix(itemVector, transformIndex + 1, location, out _);
            return ValueState.Blocked(
                new ValueOrigin($"invalid arguments to item function '{transform.FunctionName}' in {context}"));
        }

        ValueState keyState = ValidateStaticItemFunctionArgument(
            owner,
            arguments[0],
            itemVector.ItemType,
            location,
            $"the metadata key of item function '{transform.FunctionName}' in {context}");
        if (!keyState.IsStatic)
        {
            return keyState.Availability == ValueAvailability.Blocked
                ? keyState
                : ToBlocked(
                    keyState,
                    $"metadata key of item function '{transform.FunctionName}' is not static");
        }

        ValueState comparisonState = ValueState.Static;
        if (expectedArgumentCount == 2)
        {
            comparisonState = ValidateStaticItemFunctionArgument(
                owner,
                arguments[1],
                itemVector.ItemType,
                location,
                $"the comparison value of item function '{transform.FunctionName}' in {context}");
            if (!comparisonState.IsStatic)
            {
                return comparisonState.Availability == ValueAvailability.Blocked
                    ? comparisonState
                    : ToBlocked(
                        comparisonState,
                        $"comparison value of item function '{transform.FunctionName}' is not static");
            }
        }

        if (!canExpandPrefix ||
            !TryExpandItemFunctionArguments(transform, location, out string[] expandedArguments) ||
            !TryExpandItemVectorPrefix(itemVector, transformIndex, location, out List<Expander<ProjectPropertyInstance, ProjectItemInstance>.TransformEntry> entries))
        {
            return ValueState.Combine(keyState, comparisonState);
        }

        string metadataName = expandedArguments[0];
        string? comparisonValue = expandedArguments.Length == 2 ? expandedArguments[1] : null;
        ValueState state = ValueState.Static;
        foreach (Expander<ProjectPropertyInstance, ProjectItemInstance>.TransformEntry entry in entries)
        {
            ProjectItemInstance? item = entry.Item;
            if (item is null)
            {
                continue;
            }

            HardenedValue<string> metadataValue = Context.GetItemMetadataValue(item, metadataName);
            ValueState metadataState = metadataValue.State.WithOrigin(
                $"metadata '{metadataName}' on item '{item.EvaluatedInclude}' used by item function '{transform.FunctionName}'");
            state = ValueState.Combine(state, metadataState);
            if (!metadataState.IsStatic)
            {
                break;
            }

            string concreteMetadataValue = metadataValue.GetStaticValue();
            switch (functionKind)
            {
                case HardenedMetadataItemFunctionKind.Metadata:
                    break;

                case HardenedMetadataItemFunctionKind.HasMetadata:
                    if (!string.IsNullOrEmpty(concreteMetadataValue))
                    {
                        HardenedValue<string> identity = Context.GetItemIdentityValue(item);
                        state = ValueState.Combine(
                            state,
                            identity.State.WithOrigin(
                                $"identity of item '{item.EvaluatedInclude}' selected by item function '{transform.FunctionName}'"));
                    }

                    break;

                case HardenedMetadataItemFunctionKind.WithMetadataValue:
                    if (string.Equals(concreteMetadataValue, comparisonValue, StringComparison.OrdinalIgnoreCase))
                    {
                        HardenedValue<string> identity = Context.GetItemIdentityValue(item);
                        state = ValueState.Combine(
                            state,
                            identity.State.WithOrigin(
                                $"identity of item '{item.EvaluatedInclude}' selected by item function '{transform.FunctionName}'"));
                    }

                    break;

                case HardenedMetadataItemFunctionKind.AnyHaveMetadataValue:
                    if (string.Equals(concreteMetadataValue, comparisonValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return state;
                    }

                    break;
            }
        }

        return state;
    }

    private ValueState ValidateStaticItemFunctionArgument(
        object owner,
        string argument,
        string implicitItemType,
        IElementLocation location,
        string context)
    {
        ExpressionValidationResult result = ValidateExpression(
            owner,
            argument,
            location,
            context,
            requireStatic: false,
            isCondition: false,
            implicitItemType: implicitItemType);
        if (result.State.Availability == ValueAvailability.Deferred)
        {
            RequireStatic(result.State, location, context, argument);
        }

        return result.State;
    }

    private bool TryExpandItemFunctionArguments(
        HardenedItemTransformDescriptor transform,
        IElementLocation location,
        out string[] arguments)
    {
        arguments = [];
        try
        {
            string argumentsExpression = transform.FunctionArguments ?? string.Empty;
            string expandedArguments = ConcreteExpander.ExpandIntoStringLeaveEscaped(
                argumentsExpression,
                _activeMetadata is null
                    ? ExpanderOptions.ExpandProperties
                    : ExpanderOptions.ExpandPropertiesAndMetadata,
                location);
            arguments = Expander<ProjectPropertyInstance, ProjectItemInstance>.ExtractFunctionArguments(
                location,
                transform.Expression,
                expandedArguments.AsMemory());
            return true;
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return false;
        }
    }

    private bool TryExpandItemVectorPrefix(
        HardenedItemVectorDescriptor itemVector,
        int transformCount,
        IElementLocation location,
        out List<Expander<ProjectPropertyInstance, ProjectItemInstance>.TransformEntry> entries)
    {
        entries = [];
        var expression = new StringBuilder("@(").Append(itemVector.ItemType);
        for (int i = 0; i < transformCount; i++)
        {
            expression.Append("->").Append(itemVector.Transforms[i].Expression);
        }

        expression.Append(')');

        try
        {
            string expandedExpression = ConcreteExpander.ExpandIntoStringLeaveEscaped(
                expression.ToString(),
                _activeMetadata is null
                    ? ExpanderOptions.ExpandProperties
                    : ExpanderOptions.ExpandPropertiesAndMetadata,
                location);
            if (!ExpressionShredder.TryGetNextItemVectorExpression(
                    expandedExpression,
                    out ExpressionShredder.ItemExpressionCapture capture))
            {
                return false;
            }

            ConcreteExpander.ExpandExpressionCapture(
                capture,
                location,
                ExpanderOptions.ExpandItems,
                includeNullEntries: false,
                out _,
                out entries);
            return true;
        }
        catch (InvalidProjectFileException exception)
        {
            AddDiagnostic(exception);
            return false;
        }
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

    private static bool TryParseLiteralMetadataNames(string? expression, out HashSet<string>? metadataNames)
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
        => ValidationLookup.HardenedState;

    private HardenedItemOperationPlan ItemOperationPlan
        => _itemOperationPlan ?? throw new InvalidOperationException("The hardened item operation plan has not been initialized.");

    private Lookup ValidationLookup
        => _validationLookup ?? throw new InvalidOperationException("Validation lookup has not been initialized.");

    internal Lookup GetValidationLookupForTesting()
        => ValidationLookup;

    private Expander<ProjectPropertyInstance, ProjectItemInstance> ConcreteExpander
        => _concreteExpander ?? throw new InvalidOperationException("Concrete expander has not been initialized.");

    private ProjectInstance Project
        => _project ?? throw new InvalidOperationException("Project has not been initialized.");

    private string ProjectDirectory
        => _projectDirectory ?? throw new InvalidOperationException("Project directory has not been initialized.");

    private Expander<ProjectPropertyInstance, ProjectItemInstance> CreateConcreteExpander(Lookup lookup)
        => _activeMetadata is null
            ? new Expander<ProjectPropertyInstance, ProjectItemInstance>(
                lookup,
                lookup,
                FileSystems.Default,
                loggingContext: null)
            : new Expander<ProjectPropertyInstance, ProjectItemInstance>(
                lookup,
                lookup,
                _activeMetadata,
                FileSystems.Default,
                loggingContext: null);

    private sealed class LegacyCallTargetScope(ValidatorState calledState)
    {
        internal ValidatorState CalledState { get; set; } = calledState;

        internal HashSet<string> CallerAssignedProperties { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal HashSet<string> CallerAssignedItemTypes { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal bool WasInvoked { get; set; }

        internal LegacyCallTargetScope Clone()
        {
            var clone = new LegacyCallTargetScope(CloneState(CalledState))
            {
                WasInvoked = WasInvoked,
            };
            clone.CallerAssignedProperties.UnionWith(CallerAssignedProperties);
            clone.CallerAssignedItemTypes.UnionWith(CallerAssignedItemTypes);
            return clone;
        }
    }

    private sealed record ValidatorState(Lookup Lookup)
    {
        internal HardenedLookupState Context => Lookup.HardenedState;
    }

    private readonly record struct TargetBodyValidationResult(
        List<FailureState> FailureStates);

    private readonly record struct TaskValidationResult(
        bool CanStopOnFailure,
        bool Executed,
        LegacyCallTargetScope? CallTargetScope);

    private sealed record FailureState(
        ValidatorState State,
        LegacyCallTargetScope? CallTargetScope);

    private readonly record struct ValidatedBucket<T>(
        ValidatorState State,
        T Result,
        bool HasScope);

    private readonly record struct BatchingValidationResult(
        ValueState State,
        List<ItemBucket>? Buckets,
        object Owner);

    private readonly record struct ConditionValidationResult(
        ValueState State,
        bool IsKnown,
        bool Value,
        bool MayThrow);

    private readonly record struct ExpressionValidationResult(ValueState State, bool CanEvaluate);
}
