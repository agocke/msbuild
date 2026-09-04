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
    private readonly Dictionary<string, ValueOrigin> _deferredProperties = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly Dictionary<string, ValueOrigin> _deferredItems = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly List<InvalidProjectFileException> _diagnostics = [];
    private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);

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

        _deferredProperties.Clear();
        _deferredItems.Clear();
        _diagnostics.Clear();
        _diagnosticKeys.Clear();

        HashSet<string> visitedTargets = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (string targetName in targetNames)
        {
            ArgumentException.ThrowIfNullOrEmpty(targetName);
            ValidateTarget(project, targetName, visitedTargets);
        }

        return _diagnostics;
    }

    private void ValidateTarget(ProjectInstance project, string targetName, HashSet<string> visitedTargets)
    {
        if (!visitedTargets.Add(targetName))
        {
            return;
        }

        if (!project.Targets.TryGetValue(targetName, out ProjectTargetInstance? target))
        {
            throw new ArgumentException($"Target '{targetName}' does not exist.", nameof(targetName));
        }

        ValidateUnsupportedTargetConstructs(target);
        ValidateExpression(target.Condition, target.ConditionLocation, $"the condition of target '{target.Name}'", requireStatic: true, isCondition: true);
        ExpressionValidationResult dependenciesResult = ValidateExpression(
            target.DependsOnTargets,
            target.DependsOnTargetsLocation,
            $"the DependsOnTargets attribute of target '{target.Name}'",
            requireStatic: true,
            isCondition: false);
        ValidateExpression(target.BeforeTargets, target.BeforeTargetsLocation, $"the BeforeTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);
        ValidateExpression(target.AfterTargets, target.AfterTargetsLocation, $"the AfterTargets attribute of target '{target.Name}'", requireStatic: true, isCondition: false);

        if (dependenciesResult.CanEvaluate)
        {
            foreach (string dependency in ExpressionShredder.SplitSemiColonSeparatedList(project.ExpandString(target.DependsOnTargets)))
            {
                ValidateTarget(project, dependency, visitedTargets);
            }
        }

        foreach (TargetSpecification beforeTarget in project.GetTargetsWhichRunBefore(target.Name))
        {
            ValidateTarget(project, beforeTarget.TargetName, visitedTargets);
        }

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

        ValidateExpression(target.Returns, target.ReturnsLocation, $"the Returns attribute of target '{target.Name}'", requireStatic: false, isCondition: false);

        foreach (TargetSpecification afterTarget in project.GetTargetsWhichRunAfter(target.Name))
        {
            ValidateTarget(project, afterTarget.TargetName, visitedTargets);
        }
    }

    private void ValidateUnsupportedTargetConstructs(ProjectTargetInstance target)
    {
        RejectNonEmpty(target.Inputs, target.InputsLocation, "the Inputs attribute", target.Name);
        RejectNonEmpty(target.Outputs, target.OutputsLocation, "the Outputs attribute", target.Name);

        if (target.OnErrorChildren.Count > 0)
        {
            ReportUnsupported(target.OnErrorChildren[0].Location, "OnError", $"target '{target.Name}'");
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
                ReportUnsupported(item.Location, "item metadata in an in-target ItemGroup", $"target '{targetName}'");
            }

            if (_deferredItems.TryGetValue(item.ItemType, out ValueOrigin? existingOrigin))
            {
                ReportDeferred(item.Location, $"the '{item.ItemType}' item operation", item.ItemType, existingOrigin);
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
            classification = HardenedTaskClassification.Unaudited;
        }

        RejectNonEmpty(task.ContinueOnError, task.ContinueOnErrorLocation, "ContinueOnError", targetName);

        if (MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "CallTarget") ||
            MSBuildNameIgnoreCaseComparer.Default.Equals(task.Name, "MSBuild"))
        {
            ReportUnsupported(task.Location, $"the {task.Name} task", $"target '{targetName}'");
        }

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
                    ReportUnsupported(output.Location, output.GetType().Name, $"target '{targetName}'");
                    break;
            }
        }
    }

    private ExpressionValidationResult ValidateExpression(
        string? expression,
        IElementLocation? location,
        string context,
        bool requireStatic,
        bool isCondition)
    {
        if (expression is null || expression.Length == 0)
        {
            return new ExpressionValidationResult(null, CanEvaluate: true);
        }

        IElementLocation effectiveLocation = location ?? ElementLocation.EmptyLocation;
        bool canEvaluate = ValidateProhibitedFunctions(expression, effectiveLocation, context, isCondition);

        ItemsAndMetadataPair references = ExpressionShredder.GetReferencedItemNamesAndMetadata([expression]);
        if (references.Metadata is not null)
        {
            ReportUnsupported(effectiveLocation, $"metadata expressions in {context}", context);
            canEvaluate = false;
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
            ReportDeferred(effectiveLocation, context, expression, origin);
            canEvaluate = false;
        }

        return new ExpressionValidationResult(origin, canEvaluate);
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

    private void RejectNonEmpty(string? value, IElementLocation? location, string construct, string targetName)
    {
        if (!string.IsNullOrEmpty(value))
        {
            ReportUnsupported(location ?? ElementLocation.EmptyLocation, construct, $"target '{targetName}'");
        }
    }

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

    private void AddDiagnostic(Action throwDiagnostic)
    {
        try
        {
            throwDiagnostic();
        }
        catch (InvalidProjectFileException exception)
        {
            string key = $"{exception.ErrorCode}\0{exception.ProjectFile}\0{exception.LineNumber}\0{exception.ColumnNumber}\0{exception.Message}";
            if (_diagnosticKeys.Add(key))
            {
                _diagnostics.Add(exception);
            }
        }
    }

    private readonly record struct ExpressionValidationResult(ValueOrigin? Origin, bool CanEvaluate)
    {
        public static implicit operator ValueOrigin?(ExpressionValidationResult result) => result.Origin;
    }

    private sealed class ValueOrigin(string description, ValueOrigin? previous = null)
    {
        public override string ToString()
            => previous is null ? description : $"{description} from {previous}";
    }
}
