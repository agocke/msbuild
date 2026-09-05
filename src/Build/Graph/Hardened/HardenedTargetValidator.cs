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

    private readonly Dictionary<string, HardenedTaskClassification> _taskClassifications;
    private readonly List<InvalidProjectFileException> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _propertiesWithoutConcreteValues = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly HashSet<string> _targetAssignedItemTypes = new(MSBuildNameIgnoreCaseComparer.Default);
    private HardenedValidationContext? _context;
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
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targetNames);

        _diagnostics.Clear();
        _diagnosticKeys.Clear();
        _propertiesWithoutConcreteValues.Clear();
        _targetAssignedItemTypes.Clear();
        _context = new HardenedValidationContext();
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

    private void ValidateTarget(
        ProjectInstance project,
        string targetName,
        IElementLocation referenceLocation,
        HashSet<string> visitedTargets)
    {
        if (!visitedTargets.Add(targetName))
        {
            return;
        }

        if (!project.Targets.TryGetValue(targetName, out ProjectTargetInstance? target))
        {
            ReportMissingTarget(referenceLocation, targetName);
            return;
        }

        ValidateUnsupportedTargetConstructs(target);
        bool targetConditionMetadataValidated = RejectTargetMetadata(
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'");
        ExpressionValidationResult targetConditionResult = ValidateExpression(
            target.Condition,
            target.ConditionLocation,
            $"the condition of target '{target.Name}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: targetConditionMetadataValidated);
        ValidateExpression(target.BeforeTargets, target.BeforeTargetsLocation, $"the BeforeTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);
        ValidateExpression(target.AfterTargets, target.AfterTargetsLocation, $"the AfterTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);

        bool targetExecutes = !TryEvaluateCondition(
            target.Condition,
            target.ConditionLocation,
            targetConditionResult,
            out bool conditionValue) || conditionValue;

        if (targetExecutes)
        {
            ExpressionValidationResult dependenciesResult = ValidateExpression(
                target.DependsOnTargets,
                target.DependsOnTargetsLocation,
                $"the DependsOnTargets attribute of target '{target.Name}'",
                requireStatic: true,
                isCondition: false);

            if (dependenciesResult.CanEvaluate &&
                TryExpandConcreteExpression(
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
                [target.Inputs, target.Outputs, target.Returns],
                implicitItemType: null,
                target.Location,
                $"target '{target.Name}'");

            foreach (ProjectTargetInstanceChild child in target.Children)
            {
                switch (child)
                {
                    case ProjectPropertyGroupTaskInstance propertyGroup:
                        ValidatePropertyGroup(propertyGroup, target.Name);
                        break;

                    case ProjectItemGroupTaskInstance itemGroup:
                        ValidateItemGroup(itemGroup, target.Name);
                        break;

                    case ProjectTaskInstance task:
                        ValidateTask(task, target.Name);
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
            ValidateExpression(
                returnExpression,
                returnLocation,
                $"the {returnAttribute} attribute of target '{target.Name}'",
                requireStatic: false,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);
        }

        foreach (TargetSpecification afterTarget in project.GetTargetsWhichRunAfter(target.Name))
        {
            ValidateTarget(project, afterTarget.TargetName, afterTarget.ReferenceLocation, visitedTargets);
        }
    }

    private void ValidateUnsupportedTargetConstructs(ProjectTargetInstance target)
    {
        if (target.OnErrorChildren.Count > 0)
        {
            ReportUnsupported(target.OnErrorChildren[0].Location, "OnError", $"target '{target.Name}'");
        }
    }

    private void ValidatePropertyGroup(ProjectPropertyGroupTaskInstance propertyGroup, string targetName)
    {
        ExpressionValidationResult groupConditionResult = ValidateExpression(
            propertyGroup.Condition,
            propertyGroup.ConditionLocation,
            $"the condition of a PropertyGroup in target '{targetName}'",
            requireStatic: true,
            isCondition: true);
        bool groupConditionKnown = TryEvaluateCondition(
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
                [property.Condition, property.Value],
                implicitItemType: null,
                property.Location,
                $"property '{property.Name}'");

            ExpressionValidationResult conditionResult = ValidateExpression(
                property.Condition,
                property.ConditionLocation,
                $"the condition of property '{property.Name}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true);
            bool propertyConditionKnown = TryEvaluateCondition(
                property.Condition,
                property.ConditionLocation,
                conditionResult,
                out bool propertyConditionValue);
            if (propertyConditionKnown && !propertyConditionValue)
            {
                continue;
            }

            ExpressionValidationResult valueResult = ValidateExpression(
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
            Context.SetProperty(property.Name, propertyState, overwrite: overwrites);

            if (overwrites &&
                propertyState.IsStatic &&
                TryExpandConcreteExpression(
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

    private void ValidateItemGroup(ProjectItemGroupTaskInstance itemGroup, string targetName)
    {
        ValidateExpression(
            itemGroup.Condition,
            itemGroup.ConditionLocation,
            $"the condition of an ItemGroup in target '{targetName}'",
            requireStatic: true,
            isCondition: true);

        foreach (ProjectItemGroupTaskItemInstance item in itemGroup.Items)
        {
            _targetAssignedItemTypes.Add(item.ItemType);
            ValidateItemOperation(item, targetName);
        }
    }

    private void ValidateTask(ProjectTaskInstance task, string targetName)
    {
        if (!_taskClassifications.TryGetValue(task.Name, out HardenedTaskClassification classification))
        {
            classification = HardenedTaskClassification.Unaudited;
        }

        if (MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "CallTarget") ||
            MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "MSBuild"))
        {
            ReportUnsupported(task.Location, $"the {task.Name} task", $"target '{targetName}'");
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
            batchableExpressions,
            implicitItemType: null,
            task.Location,
            $"task '{task.Name}'");

        ExpressionValidationResult taskConditionResult = ValidateExpression(
            task.Condition,
            task.ConditionLocation,
            $"the condition of task '{task.Name}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: true);

        ExpressionValidationResult continueOnErrorResult = ValidateExpression(
            task.ContinueOnError,
            task.ContinueOnErrorLocation,
            $"ContinueOnError of task '{task.Name}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);

        ValueState taskControlState = ValueState.Combine(
            taskBatchingState,
            ValueState.Combine(taskConditionResult.State, continueOnErrorResult.State));
        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            ExpressionValidationResult parameterResult = ValidateExpression(
                parameter.Value.Item1,
                parameter.Value.Item2,
                $"parameter '{parameter.Key}' of task '{task.Name}'",
                requireStatic: classification == HardenedTaskClassification.Pure,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);

            if (classification == HardenedTaskClassification.Pure ||
                parameterResult.State.Availability == ValueAvailability.Blocked)
            {
                taskControlState = ValueState.Combine(taskControlState, parameterResult.State);
            }
        }

        bool outputsAreDeferred = classification != HardenedTaskClassification.Pure;
        foreach (ProjectTaskInstanceChild output in task.Outputs)
        {
            string outputTaskParameter = GetTaskParameter(output);
            string? outputDestinationExpression = GetOutputDestination(output);
            ValueState outputConditionBatchingState = ValidateBatching(
                [output.Condition],
                implicitItemType: null,
                output.ConditionLocation,
                $"condition of output '{outputTaskParameter}' from task '{task.Name}'",
                reportDiagnostics: false);

            ExpressionValidationResult outputTaskParameterResult = ValidateExpression(
                outputTaskParameter,
                output.TaskParameterLocation,
                $"TaskParameter '{outputTaskParameter}' of output from task '{task.Name}'",
                requireStatic: true,
                isCondition: false,
                metadataBatchingValidated: true,
                includeItemMetadata: true);

            ExpressionValidationResult outputConditionResult = ValidateExpression(
                output.Condition,
                output.ConditionLocation,
                $"the condition of output '{GetTaskParameter(output)}' from task '{task.Name}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true);

            ValueState outputDestinationBatchingState = ValidateBatching(
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
                        propertyOutput.PropertyName,
                        propertyOutput.PropertyNameLocation,
                        $"PropertyName of output from task '{task.Name}'",
                        outputDestinationBatchingState);
                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    destination = ValidateOutputDestination(
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
            ValueState outputState = outputMappingState.IsStatic
                ? outputsAreDeferred ? ValueState.Deferred(origin) : ValueState.Static
                : ToBlocked(outputMappingState, $"output '{GetTaskParameter(output)}' of task '{task.Name}' is unavailable");
            switch (output)
            {
                case ProjectTaskOutputPropertyInstance propertyOutput:
                    Context.SetProperty(destination, outputState);
                    _propertiesWithoutConcreteValues.Add(destination);
                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    Context.AddTaskOutputItems(destination, outputState);
                    _targetAssignedItemTypes.Add(destination);
                    break;
            }
        }
    }

    private string? ValidateOutputDestination(
        string destination,
        IElementLocation location,
        string context,
        ValueState batchingState)
    {
        ExpressionValidationResult result = ValidateExpression(
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

    private bool ReferencesPropertyWithoutConcreteValue(string expression)
        => ReferencesPropertyWithoutConcreteValue(expression, 0, expression.Length);

    private bool ReferencesPropertyWithoutConcreteValue(string expression, int startIndex, int endIndex)
    {
        int marker = ExpressionShredder.IndexOfPropertyMarker(
            expression,
            startIndex,
            endIndex - startIndex);
        while (marker >= 0 && marker < endIndex)
        {
            int bodyStart = marker + 2;
            int close = FindClosingParenthesis(expression, bodyStart);
            if (close < 0 || close >= endIndex)
            {
                return false;
            }

            ReadOnlySpan<char> body = expression.AsSpan(bodyStart, close - bodyStart).Trim();
            if (!body.IsEmpty && body[0] != '[' && !body.StartsWith("registry:", StringComparison.OrdinalIgnoreCase))
            {
                int nameEnd = body.IndexOfAny('.', '[');
                ReadOnlySpan<char> propertyName = (nameEnd < 0 ? body : body[..nameEnd]).Trim();
                if (!propertyName.IsEmpty && _propertiesWithoutConcreteValues.Contains(propertyName.ToString()))
                {
                    return true;
                }
            }

            if (ReferencesPropertyWithoutConcreteValue(expression, bodyStart, close))
            {
                return true;
            }

            marker = ExpressionShredder.IndexOfPropertyMarker(
                expression,
                close + 1,
                endIndex - close - 1);
        }

        return false;
    }

    private bool TryEvaluateCondition(
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
            !CanExpandConcreteExpression(condition))
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
        string expression,
        IElementLocation? location,
        string context,
        bool reportUnmodeled,
        out string expanded)
    {
        expanded = string.Empty;
        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        if (!CanExpandConcreteExpression(expression))
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

    private bool CanExpandConcreteExpression(string expression)
    {
        if (ReferencesPropertyWithoutConcreteValue(expression))
        {
            return false;
        }

        ItemsAndMetadataPair references = ExpressionShredder.GetReferencedItemNamesAndMetadata([expression]);
        if (references.Metadata is not null)
        {
            return false;
        }

        if (references.Items is not null)
        {
            foreach (string itemType in references.Items)
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
        string? expression,
        IElementLocation? location,
        string context,
        bool requireStatic,
        bool isCondition,
        bool metadataBatchingValidated = false,
        bool includeItemMetadata = false)
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

        ItemsAndMetadataPair references = ExpressionShredder.GetReferencedItemNamesAndMetadata([expression]);
        state = ValueState.Combine(state, FindPropertyState(expression));

        if (references.Items is not null)
        {
            foreach (string itemType in references.Items)
            {
                state = ValueState.Combine(state, Context.GetItemMembership(itemType));
            }
        }

        state = ValueState.Combine(state, FindTransformMetadataState(expression, includeItemMetadata));

        if (!metadataBatchingValidated && references.Metadata is not null)
        {
            ValueState metadataState = GetMetadataReferenceState(
                references,
                implicitItemType: null,
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

    private ValueState FindPropertyState(string expression)
        => FindPropertyState(expression, 0, expression.Length);

    private ValueState FindPropertyState(string expression, int startIndex, int endIndex)
    {
        ValueState state = ValueState.Static;
        int marker = ExpressionShredder.IndexOfPropertyMarker(
            expression,
            startIndex,
            endIndex - startIndex);
        while (marker >= 0 && marker < endIndex)
        {
            int bodyStart = marker + 2;
            int close = FindClosingParenthesis(expression, bodyStart);
            if (close < 0 || close >= endIndex)
            {
                return state;
            }

            ReadOnlySpan<char> body = expression.AsSpan(bodyStart, close - bodyStart).Trim();
            if (!body.IsEmpty && body[0] != '[' && !body.StartsWith("registry:", StringComparison.OrdinalIgnoreCase))
            {
                int nameEnd = body.IndexOfAny('.', '[');
                ReadOnlySpan<char> propertyName = (nameEnd < 0 ? body : body[..nameEnd]).Trim();
                if (!propertyName.IsEmpty)
                {
                    state = ValueState.Combine(state, Context.GetProperty(propertyName.ToString()));
                }
            }

            state = ValueState.Combine(state, FindPropertyState(expression, bodyStart, close));
            marker = ExpressionShredder.IndexOfPropertyMarker(
                expression,
                close + 1,
                endIndex - close - 1);
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
            batchableExpressions,
            item.ItemType,
            item.Location,
            $"item '{item.ItemType}' in target '{targetName}'");

        ExpressionValidationResult conditionResult = ValidateExpression(
            item.Condition,
            item.ConditionLocation,
            $"the condition of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: true,
            metadataBatchingValidated: true);
        ExpressionValidationResult includeResult = ValidateExpression(
            item.Include,
            item.IncludeLocation,
            $"the Include of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult excludeResult = ValidateExpression(
            item.Exclude,
            item.ExcludeLocation,
            $"the Exclude of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult removeResult = ValidateExpression(
            item.Remove,
            item.RemoveLocation,
            $"the Remove of item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult matchOnMetadataResult = ValidateExpression(
            item.MatchOnMetadata,
            item.MatchOnMetadataLocation,
            $"MatchOnMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult matchOnMetadataOptionsResult = ValidateExpression(
            item.MatchOnMetadataOptions,
            item.MatchOnMetadataOptionsLocation,
            $"MatchOnMetadataOptions on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult keepMetadataResult = ValidateExpression(
            item.KeepMetadata,
            item.KeepMetadataLocation,
            $"KeepMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult removeMetadataResult = ValidateExpression(
            item.RemoveMetadata,
            item.RemoveMetadataLocation,
            $"RemoveMetadata on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);
        ExpressionValidationResult keepDuplicatesResult = ValidateExpression(
            item.KeepDuplicates,
            item.KeepDuplicatesLocation,
            $"KeepDuplicates on item '{item.ItemType}'",
            requireStatic: true,
            isCondition: false,
            metadataBatchingValidated: true);

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
                metadata.Condition,
                metadata.ConditionLocation,
                $"the condition of metadata '{metadata.Name}' on item '{item.ItemType}'",
                requireStatic: true,
                isCondition: true,
                metadataBatchingValidated: true);
            ExpressionValidationResult valueResult = ValidateExpression(
                metadata.Value,
                metadata.Location,
                $"the value of metadata '{metadata.Name}' on item '{item.ItemType}'",
                requireStatic: false,
                isCondition: false,
                metadataBatchingValidated: true);

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

        ItemsAndMetadataPair removeReferences = ExpressionShredder.GetReferencedItemNamesAndMetadata([item.Remove]);
        if (removeReferences.Items is null)
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

            foreach (string sourceItemType in removeReferences.Items)
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
        string include,
        out Dictionary<string, ValueState>? inheritedMetadata,
        out ValueState inheritedDefaultMetadata)
    {
        inheritedMetadata = null;
        inheritedDefaultMetadata = ValueState.Static;
        int startIndex = 0;
        while (ExpressionShredder.TryGetNextItemVectorExpression(
            include,
            startIndex,
            out ExpressionShredder.ItemExpressionCapture itemVector))
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

            startIndex = itemVector.Index + itemVector.Length;
        }
    }

    private ValueState ValidateBatching(
        IReadOnlyList<string?> expressions,
        string? implicitItemType,
        IElementLocation? location,
        string context,
        bool reportDiagnostics = true)
    {
        List<string> nonEmptyExpressions = [];
        for (int i = 0; i < expressions.Count; i++)
        {
            AddIfNotEmpty(nonEmptyExpressions, expressions[i]);
        }

        if (nonEmptyExpressions.Count == 0)
        {
            return ValueState.Static;
        }

        ItemsAndMetadataPair references = ExpressionShredder.GetReferencedItemNamesAndMetadata(nonEmptyExpressions);
        if (references.Metadata is null)
        {
            return ValueState.Static;
        }

        var consumedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        if (references.Items is not null)
        {
            consumedItemTypes.UnionWith(references.Items);
        }

        if (implicitItemType is not null)
        {
            consumedItemTypes.Add(implicitItemType);
        }

        var batchedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        BatchingEngine.AddItemTypesToBeBatched(
            references.Metadata,
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
            foreach (MetadataReference metadataReference in references.Metadata.Values)
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
        ItemsAndMetadataPair references,
        string? implicitItemType,
        IElementLocation location,
        string context)
    {
        var consumedItemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        if (references.Items is not null)
        {
            consumedItemTypes.UnionWith(references.Items);
        }

        if (implicitItemType is not null)
        {
            consumedItemTypes.Add(implicitItemType);
        }

        var itemTypes = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        BatchingEngine.AddItemTypesToBeBatched(
            references.Metadata!,
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
            foreach (MetadataReference metadataReference in references.Metadata!.Values)
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

    private ValueState FindTransformMetadataState(string expression, bool includeItemMetadata)
    {
        ValueState state = ValueState.Static;
        int startIndex = 0;
        while (ExpressionShredder.TryGetNextItemVectorExpression(
            expression,
            startIndex,
            out ExpressionShredder.ItemExpressionCapture itemVector))
        {
            if (includeItemMetadata && itemVector.Captures is null)
            {
                state = ValueState.Combine(state, Context.GetItemValue(itemVector.ItemType, includeMetadata: true));
            }

            if (itemVector.Captures is not null)
            {
                foreach (ExpressionShredder.ItemExpressionCapture transform in itemVector.Captures)
                {
                    ItemsAndMetadataPair transformReferences =
                        ExpressionShredder.GetReferencedItemNamesAndMetadata([transform.Value]);
                    if (transformReferences.Metadata is not null)
                    {
                        foreach (MetadataReference metadataReference in transformReferences.Metadata.Values)
                        {
                            string itemType = metadataReference.ItemName ?? itemVector.ItemType;
                            ValueState metadataState = Context.GetMetadata(itemType, metadataReference.MetadataName);
                            state = ValueState.Combine(
                                state,
                                metadataState.WithOrigin($"transform of item '{itemVector.ItemType}'"));
                        }
                    }
                }
            }

            startIndex = itemVector.Index + itemVector.Length;
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

    private static int FindClosingParenthesis(string expression, int start)
    {
        int depth = 1;
        for (int index = start; index < expression.Length; index++)
        {
            switch (expression[index])
            {
                case '\'' or '"' or '`':
                    int closingQuote = expression.IndexOf(expression[index], index + 1);
                    if (closingQuote < 0)
                    {
                        return -1;
                    }

                    index = closingQuote;
                    break;

                case '(':
                    depth++;
                    break;

                case ')':
                    if (--depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return -1;
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

    private HardenedValidationContext Context
        => _context ?? throw new InvalidOperationException("Validation context has not been initialized.");

    private HardenedConcreteState ConcreteState
        => _concreteState ?? throw new InvalidOperationException("Concrete state has not been initialized.");

    private Expander<ProjectPropertyInstance, ProjectItemInstance> ConcreteExpander
        => _concreteExpander ?? throw new InvalidOperationException("Concrete expander has not been initialized.");

    private readonly record struct ExpressionValidationResult(ValueState State, bool CanEvaluate);
}
