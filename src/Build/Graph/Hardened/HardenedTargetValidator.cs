// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;

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
    private readonly Dictionary<string, HardenedTaskClassification> _taskClassifications;
    private readonly Dictionary<string, ValueOrigin> _deferredProperties = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly Dictionary<string, ValueOrigin> _deferredItems = new(MSBuildNameIgnoreCaseComparer.Default);

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

    internal void Validate(ProjectInstance project, string targetName)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        _deferredProperties.Clear();
        _deferredItems.Clear();

        if (!project.Targets.TryGetValue(targetName, out ProjectTargetInstance? target))
        {
            throw new ArgumentException($"Target '{targetName}' does not exist.", nameof(targetName));
        }

        ValidateUnsupportedTargetConstructs(target);
        ValidateExpression(target.Condition, target.ConditionLocation, $"the condition of target '{target.Name}'", requireStatic: true, isCondition: true);

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
                    ThrowUnsupported(child.Location, child.GetType().Name, $"target '{target.Name}'");
                    break;
            }
        }
    }

    private static void ValidateUnsupportedTargetConstructs(ProjectTargetInstance target)
    {
        RejectNonEmpty(target.Inputs, target.InputsLocation, "the Inputs attribute", target.Name);
        RejectNonEmpty(target.Outputs, target.OutputsLocation, "the Outputs attribute", target.Name);
        RejectNonEmpty(target.Returns, target.ReturnsLocation, "the Returns attribute", target.Name);
        RejectNonEmpty(target.DependsOnTargets, target.DependsOnTargetsLocation, "the DependsOnTargets attribute", target.Name);
        RejectNonEmpty(target.BeforeTargets, target.BeforeTargetsLocation, "the BeforeTargets attribute", target.Name);
        RejectNonEmpty(target.AfterTargets, target.AfterTargetsLocation, "the AfterTargets attribute", target.Name);

        if (target.OnErrorChildren.Count > 0)
        {
            ThrowUnsupported(target.OnErrorChildren[0].Location, "OnError", $"target '{target.Name}'");
        }
    }

    private void ValidatePropertyGroup(ProjectPropertyGroupTaskInstance propertyGroup, string targetName)
    {
        ValidateExpression(
            propertyGroup.Condition,
            propertyGroup.ConditionLocation,
            $"the condition of a PropertyGroup in target '{targetName}'",
            requireStatic: true,
            isCondition: true);

        foreach (ProjectPropertyGroupTaskPropertyInstance property in propertyGroup.Properties)
        {
            ValidateExpression(
                property.Condition,
                property.ConditionLocation,
                $"the condition of property '{property.Name}'",
                requireStatic: true,
                isCondition: true);

            ValueOrigin? origin = ValidateExpression(
                property.Value,
                property.Location,
                $"the value of property '{property.Name}'",
                requireStatic: false,
                isCondition: false);

            if (origin is not null)
            {
                _deferredProperties[property.Name] = new ValueOrigin($"property '{property.Name}'", origin);
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
            if (item.Metadata.Count > 0)
            {
                ThrowUnsupported(item.Location, "item metadata in an in-target ItemGroup", $"target '{targetName}'");
            }

            if (_deferredItems.TryGetValue(item.ItemType, out ValueOrigin? existingOrigin))
            {
                ThrowDeferred(item.Location, $"the '{item.ItemType}' item operation", item.ItemType, existingOrigin);
            }

            ValidateExpression(item.Condition, item.ConditionLocation, $"the condition of item '{item.ItemType}'", requireStatic: true, isCondition: true);
            ValidateExpression(item.Include, item.IncludeLocation, $"the Include of item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.Exclude, item.ExcludeLocation, $"the Exclude of item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.Remove, item.RemoveLocation, $"the Remove of item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.MatchOnMetadata, item.MatchOnMetadataLocation, $"MatchOnMetadata on item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.MatchOnMetadataOptions, item.MatchOnMetadataOptionsLocation, $"MatchOnMetadataOptions on item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.KeepMetadata, item.KeepMetadataLocation, $"KeepMetadata on item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.RemoveMetadata, item.RemoveMetadataLocation, $"RemoveMetadata on item '{item.ItemType}'", requireStatic: true, isCondition: false);
            ValidateExpression(item.KeepDuplicates, item.KeepDuplicatesLocation, $"KeepDuplicates on item '{item.ItemType}'", requireStatic: true, isCondition: false);
        }
    }

    private void ValidateTask(ProjectTaskInstance task, string targetName)
    {
        if (!_taskClassifications.TryGetValue(task.Name, out HardenedTaskClassification classification))
        {
            ProjectFileErrorUtilities.ThrowInvalidProjectFile(
                new BuildEventFileInfo(task.Location),
                "HardenedGraphMissingTaskClassification",
                task.Name,
                targetName);
        }

        RejectNonEmpty(task.ContinueOnError, task.ContinueOnErrorLocation, "ContinueOnError", targetName);

        ValidateExpression(
            task.Condition,
            task.ConditionLocation,
            $"the condition of task '{task.Name}'",
            requireStatic: true,
            isCondition: true);

        foreach (KeyValuePair<string, (string, ElementLocation)> parameter in task.TestGetParameters)
        {
            ValidateExpression(
                parameter.Value.Item1,
                parameter.Value.Item2,
                $"parameter '{parameter.Key}' of task '{task.Name}'",
                requireStatic: classification == HardenedTaskClassification.Pure,
                isCondition: false);
        }

        bool outputsAreDeferred = classification != HardenedTaskClassification.Pure;
        foreach (ProjectTaskInstanceChild output in task.Outputs)
        {
            ValidateExpression(
                output.Condition,
                output.ConditionLocation,
                $"the condition of output '{GetTaskParameter(output)}' from task '{task.Name}'",
                requireStatic: true,
                isCondition: true);

            ValueOrigin origin = new($"output '{GetTaskParameter(output)}' of task '{task.Name}'");
            switch (output)
            {
                case ProjectTaskOutputPropertyInstance propertyOutput:
                    if (outputsAreDeferred)
                    {
                        _deferredProperties[propertyOutput.PropertyName] = origin;
                    }

                    break;

                case ProjectTaskOutputItemInstance itemOutput:
                    if (outputsAreDeferred)
                    {
                        _deferredItems[itemOutput.ItemType] = origin;
                    }

                    break;

                default:
                    ThrowUnsupported(output.Location, output.GetType().Name, $"target '{targetName}'");
                    break;
            }
        }
    }

    private ValueOrigin? ValidateExpression(
        string? expression,
        IElementLocation? location,
        string context,
        bool requireStatic,
        bool isCondition)
    {
        if (expression is null || expression.Length == 0)
        {
            return null;
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        ValidateProhibitedFunctions(expression, effectiveLocation, context, isCondition);

        ItemsAndMetadataPair references = ExpressionShredder.GetReferencedItemNamesAndMetadata([expression]);
        if (references.Metadata is not null)
        {
            ThrowUnsupported(effectiveLocation, $"metadata expressions in {context}", context);
        }

        ValueOrigin? origin = FindDeferredProperty(expression);
        if (origin is null && references.Items is not null)
        {
            foreach (string itemType in references.Items)
            {
                if (_deferredItems.TryGetValue(itemType, out origin))
                {
                    break;
                }
            }
        }

        if (requireStatic && origin is not null)
        {
            ThrowDeferred(effectiveLocation, context, expression, origin);
        }

        return origin;
    }

    private ValueOrigin? FindDeferredProperty(string expression)
    {
        int marker = ExpressionShredder.IndexOfPropertyMarker(expression);
        while (marker >= 0)
        {
            int bodyStart = marker + 2;
            int close = FindClosingParenthesis(expression, bodyStart);
            if (close < 0)
            {
                return null;
            }

            ReadOnlySpan<char> body = expression.AsSpan(bodyStart, close - bodyStart).Trim();
            if (!body.IsEmpty && body[0] != '[' && !body.StartsWith("registry:", StringComparison.OrdinalIgnoreCase))
            {
                int nameEnd = body.IndexOfAny('.', '[');
                ReadOnlySpan<char> propertyName = (nameEnd < 0 ? body : body[..nameEnd]).Trim();
                if (!propertyName.IsEmpty &&
                    _deferredProperties.TryGetValue(propertyName.ToString(), out ValueOrigin? origin))
                {
                    return origin;
                }
            }

            marker = ExpressionShredder.IndexOfPropertyMarker(expression, close + 1);
        }

        return null;
    }

    private static void ValidateProhibitedFunctions(
        string expression,
        IElementLocation location,
        string context,
        bool isCondition)
    {
        if (expression.Contains("$(registry:", StringComparison.OrdinalIgnoreCase))
        {
            ThrowProhibitedFunction(location, "$(registry:...)", context);
        }

        if (isCondition && expression.Contains("Exists(", StringComparison.OrdinalIgnoreCase))
        {
            ThrowProhibitedFunction(location, "Exists", context);
        }

        int marker = expression.IndexOf("$([", StringComparison.Ordinal);
        while (marker >= 0)
        {
            int typeStart = marker + 3;
            int typeEnd = expression.IndexOf(']', typeStart);
            if (typeEnd < 0)
            {
                return;
            }

            ReadOnlySpan<char> typeName = expression.AsSpan(typeStart, typeEnd - typeStart).Trim();
            int separator = expression.IndexOf("::", typeEnd + 1, StringComparison.Ordinal);
            if (separator < 0)
            {
                return;
            }

            int memberStart = separator + 2;
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
                ThrowProhibitedFunction(location, $"$([{typeName.ToString()}]::{memberName.ToString()})", context);
            }

            marker = expression.IndexOf("$([", typeEnd + 1, StringComparison.Ordinal);
        }
    }

    private static bool IsProhibitedFunction(ReadOnlySpan<char> typeName, ReadOnlySpan<char> memberName)
    {
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

    private static void RejectNonEmpty(string? value, IElementLocation? location, string construct, string targetName)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ThrowUnsupported(location ?? ElementLocation.EmptyLocation, construct, $"target '{targetName}'");
        }
    }

    private static void ThrowUnsupported(IElementLocation location, string construct, string context)
        => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
            new BuildEventFileInfo(location),
            "HardenedGraphUnsupportedConstruct",
            construct,
            context);

    private static void ThrowProhibitedFunction(IElementLocation location, string function, string context)
        => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
            new BuildEventFileInfo(location),
            "HardenedGraphProhibitedFunction",
            function,
            context);

    private static void ThrowDeferred(IElementLocation location, string context, string expression, ValueOrigin origin)
        => ProjectFileErrorUtilities.ThrowInvalidProjectFile(
            new BuildEventFileInfo(location),
            "HardenedGraphDeferredValueInStaticContext",
            context,
            expression,
            origin.ToString());

    private sealed class ValueOrigin(string description, ValueOrigin? previous = null)
    {
        public override string ToString()
            => previous is null ? description : $"{description} from {previous}";
    }
}
