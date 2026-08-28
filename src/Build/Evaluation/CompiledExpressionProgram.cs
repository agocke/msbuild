// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal enum CompiledTaskValueKind : byte
    {
        Unsupported,
        Scalar,
        ItemVector,
    }

    internal readonly struct CompiledTaskParameterProgram
    {
        internal CompiledTaskParameterProgram(
            string name,
            string value,
            ElementLocation location,
            CompiledScalarProgram scalarProgram,
            string itemType)
        {
            Name = name;
            Value = value;
            Location = location;
            ScalarProgram = scalarProgram;
            ItemType = itemType;
            Kind = scalarProgram != null
                ? CompiledTaskValueKind.Scalar
                : itemType != null
                    ? CompiledTaskValueKind.ItemVector
                    : CompiledTaskValueKind.Unsupported;
        }

        internal string Name { get; }

        internal string Value { get; }

        internal ElementLocation Location { get; }

        internal CompiledTaskValueKind Kind { get; }

        internal CompiledScalarProgram ScalarProgram { get; }

        internal string ItemType { get; }
    }

    internal readonly struct CompiledTaskOutputProgram
    {
        internal CompiledTaskOutputProgram(
            string taskParameter,
            string destinationName,
            string condition,
            bool isItem,
            ElementLocation location,
            ElementLocation taskParameterLocation,
            ElementLocation destinationLocation,
            ElementLocation conditionLocation)
        {
            TaskParameter = taskParameter;
            DestinationName = destinationName;
            Condition = condition;
            IsItem = isItem;
            Location = location;
            TaskParameterLocation = taskParameterLocation;
            DestinationLocation = destinationLocation;
            ConditionLocation = conditionLocation;
        }

        internal string TaskParameter { get; }

        internal string DestinationName { get; }

        internal string Condition { get; }

        internal bool IsItem { get; }

        internal ElementLocation Location { get; }

        internal ElementLocation TaskParameterLocation { get; }

        internal ElementLocation DestinationLocation { get; }

        internal ElementLocation ConditionLocation { get; }
    }

    /// <summary>
    /// Module-owned source program for one ordinary task site. It contains no task type or
    /// assembly-load-context state.
    /// </summary>
    internal sealed class CompiledTaskSourceProgram
    {
        private static readonly ConditionalWeakTable<ProjectTaskInstance, CompiledTaskSourceProgram>
            s_programsByTask = new();

        private CompiledTaskSourceProgram(
            string name,
            string condition,
            string continueOnError,
            string msBuildRuntime,
            string msBuildArchitecture,
            ElementLocation location,
            ElementLocation conditionLocation,
            ElementLocation continueOnErrorLocation,
            CompiledTaskParameterProgram[] parameters,
            CompiledTaskOutputProgram[] outputs)
        {
            Name = name;
            Condition = condition;
            ContinueOnError = continueOnError;
            MSBuildRuntime = msBuildRuntime;
            MSBuildArchitecture = msBuildArchitecture;
            Location = location;
            ConditionLocation = conditionLocation;
            ContinueOnErrorLocation = continueOnErrorLocation;
            Parameters = parameters;
            Outputs = outputs;

            if (!string.IsNullOrEmpty(condition))
            {
                ConditionProgram = CompiledConditionProgram.TryCreate(
                    condition,
                    conditionLocation);
                ConditionDisplayProgram = CompiledScalarProgram.TryCreate(condition);
            }

            if (continueOnErrorLocation != null)
            {
                ContinueOnErrorProgram =
                    CompiledScalarProgram.TryCreate(continueOnError);
            }
        }

        internal string Name { get; }

        internal string Condition { get; }

        internal string ContinueOnError { get; }

        internal string MSBuildRuntime { get; }

        internal string MSBuildArchitecture { get; }

        internal ElementLocation Location { get; }

        internal ElementLocation ConditionLocation { get; }

        internal ElementLocation ContinueOnErrorLocation { get; }

        internal CompiledTaskParameterProgram[] Parameters { get; }

        internal CompiledTaskOutputProgram[] Outputs { get; }

        internal CompiledConditionProgram ConditionProgram { get; }

        internal CompiledScalarProgram ConditionDisplayProgram { get; }

        internal CompiledScalarProgram ContinueOnErrorProgram { get; }

        internal bool HasStaticCurrentProcessIdentity =>
            string.IsNullOrEmpty(MSBuildRuntime) &&
            string.IsNullOrEmpty(MSBuildArchitecture);

        internal bool SupportsFastExecution
        {
            get
            {
                if ((!string.IsNullOrEmpty(Condition) &&
                        (ConditionProgram == null || ConditionDisplayProgram == null)) ||
                    (ContinueOnErrorLocation != null &&
                        ContinueOnErrorProgram == null))
                {
                    return false;
                }

                for (int i = 0; i < Parameters.Length; i++)
                {
                    if (Parameters[i].Kind == CompiledTaskValueKind.Unsupported)
                    {
                        return false;
                    }
                }

                for (int i = 0; i < Outputs.Length; i++)
                {
                    CompiledTaskOutputProgram output = Outputs[i];
                    if (!output.IsItem ||
                        !string.IsNullOrEmpty(output.Condition) ||
                        ContainsExpansion(output.TaskParameter) ||
                        ContainsExpansion(output.DestinationName))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        internal static CompiledTaskSourceProgram Create(ProjectTaskElement task)
        {
            var parameters =
                new CompiledTaskParameterProgram[task.ParametersForEvaluation.Count];
            int parameterIndex = 0;
            foreach (KeyValuePair<string, (string, ElementLocation)> parameter
                in task.ParametersForEvaluation)
            {
                parameters[parameterIndex++] = CreateParameter(
                    parameter.Key,
                    parameter.Value.Item1,
                    parameter.Value.Item2);
            }

            var outputs = new CompiledTaskOutputProgram[task.Outputs.Count];
            int outputIndex = 0;
            foreach (ProjectOutputElement output in task.Outputs)
            {
                outputs[outputIndex++] = new CompiledTaskOutputProgram(
                    output.TaskParameter,
                    output.IsOutputItem ? output.ItemType : output.PropertyName,
                    output.Condition,
                    output.IsOutputItem,
                    output.Location,
                    output.TaskParameterLocation,
                    output.IsOutputItem
                        ? output.ItemTypeLocation
                        : output.PropertyNameLocation,
                    output.ConditionLocation);
            }

            return new CompiledTaskSourceProgram(
                task.Name,
                task.Condition,
                task.ContinueOnError,
                task.MSBuildRuntime,
                task.MSBuildArchitecture,
                task.Location,
                task.ConditionLocation,
                task.ContinueOnErrorLocation,
                parameters,
                outputs);
        }

        internal static CompiledTaskSourceProgram GetOrCreate(
            ProjectTaskInstance task) =>
            s_programsByTask.GetValue(task, Create);

        internal static void Associate(
            ProjectTaskInstance task,
            CompiledTaskSourceProgram program)
        {
            if (program != null)
            {
                s_programsByTask.GetValue(task, _ => program);
            }
        }

        private static CompiledTaskSourceProgram Create(
            ProjectTaskInstance task)
        {
            var parameters =
                new CompiledTaskParameterProgram[task.ParametersForBuild.Count];
            int parameterIndex = 0;
            foreach (KeyValuePair<string, (string, ElementLocation)> parameter
                in task.ParametersForBuild)
            {
                parameters[parameterIndex++] = CreateParameter(
                    parameter.Key,
                    parameter.Value.Item1,
                    parameter.Value.Item2);
            }

            var outputs = new CompiledTaskOutputProgram[task.Outputs.Count];
            for (int i = 0; i < outputs.Length; i++)
            {
                ProjectTaskInstanceChild output = task.Outputs[i];
                outputs[i] = output switch
                {
                    ProjectTaskOutputItemInstance item =>
                        new CompiledTaskOutputProgram(
                            item.TaskParameter,
                            item.ItemType,
                            item.Condition,
                            isItem: true,
                            item.Location,
                            item.TaskParameterLocation,
                            item.ItemTypeLocation,
                            item.ConditionLocation),
                    ProjectTaskOutputPropertyInstance property =>
                        new CompiledTaskOutputProgram(
                            property.TaskParameter,
                            property.PropertyName,
                            property.Condition,
                            isItem: false,
                            property.Location,
                            property.TaskParameterLocation,
                            property.PropertyNameLocation,
                            property.ConditionLocation),
                    _ => throw new InvalidOperationException(
                        "Unknown task output instance."),
                };
            }

            return new CompiledTaskSourceProgram(
                task.Name,
                task.Condition,
                task.ContinueOnError,
                task.MSBuildRuntime,
                task.MSBuildArchitecture,
                task.Location,
                task.ConditionLocation,
                task.ContinueOnErrorLocation,
                parameters,
                outputs);
        }

        private static CompiledTaskParameterProgram CreateParameter(
            string name,
            string value,
            ElementLocation location)
        {
            if (TryParseItemVector(value, out string itemType))
            {
                return new CompiledTaskParameterProgram(
                    name,
                    value,
                    location,
                    scalarProgram: null,
                    itemType);
            }

            CompiledScalarProgram scalarProgram =
                ContainsItemOrMetadataExpansion(value)
                    ? null
                    : CompiledScalarProgram.TryCreate(value);
            return new CompiledTaskParameterProgram(
                name,
                value,
                location,
                scalarProgram,
                itemType: null);
        }

        private static bool TryParseItemVector(
            string value,
            out string itemType)
        {
            itemType = null;
            if (value?.Length < 4 ||
                !value.StartsWith("@(", StringComparison.Ordinal) ||
                value[value.Length - 1] != ')')
            {
                return false;
            }

            string candidate = value.Substring(2, value.Length - 3);
            if (!XmlUtilities.IsValidElementName(candidate))
            {
                return false;
            }

            itemType = candidate;
            return true;
        }

        private static bool ContainsItemOrMetadataExpansion(string value) =>
            value?.Contains("@(", StringComparison.Ordinal) == true ||
            value?.Contains("%(", StringComparison.Ordinal) == true;

        private static bool ContainsExpansion(string value) =>
            value?.Contains("$(", StringComparison.Ordinal) == true ||
            ContainsItemOrMetadataExpansion(value);
    }

    internal interface ICompiledExpressionEnvironment
    {
        string GetEscapedPropertyValue(string propertyName, IElementLocation location);

        string GetEscapedMetadataValue(
            string itemType,
            string metadataName,
            IElementLocation location);

        string ExpandItems(string escapedValue, IElementLocation location);

        void EnterConditionEvaluation(bool oneSideIsEmpty);

        void LeaveConditionEvaluation();
    }

    internal readonly struct CompiledExpressionFunction
    {
        internal CompiledExpressionFunction(
            CompiledPropertyFunctionKind kind,
            TableRange receiver,
            TableRange arguments,
            string expression)
        {
            Kind = kind;
            Receiver = receiver;
            Arguments = arguments;
            Expression = expression;
        }

        internal CompiledPropertyFunctionKind Kind { get; }

        internal TableRange Receiver { get; }

        internal TableRange Arguments { get; }

        internal string Expression { get; }
    }

    internal readonly struct CompiledExpressionFunctionArgument
    {
        internal CompiledExpressionFunctionArgument(TableRange valueParts)
        {
            ValueParts = valueParts;
        }

        internal TableRange ValueParts { get; }
    }

    internal sealed class CompiledConditionProgram
    {
        private readonly CompiledConditionProgramData _program;

        private CompiledConditionProgram(CompiledConditionProgramData program)
        {
            _program = program;
        }

        internal static CompiledConditionProgram TryCreate(
            string condition,
            ElementLocation location)
        {
            return CompiledConditionCompiler.TryCompile(
                condition,
                ParserOptions.AllowProperties,
                location,
                out CompiledConditionProgramData program,
                allowExtendedExpressions: true)
                ? new CompiledConditionProgram(program)
                : null;
        }

        internal static CompiledConditionProgram TryCreateForItemGroup(
            string condition,
            ElementLocation location)
        {
            return CompiledConditionCompiler.TryCompile(
                condition,
                ParserOptions.AllowAll,
                location,
                out CompiledConditionProgramData program,
                allowExtendedExpressions: true)
                ? new CompiledConditionProgram(program)
                : null;
        }

        internal bool Evaluate(
            ICompiledExpressionEnvironment environment,
            IElementLocation location)
        {
            return Evaluate(
                environment,
                location,
                expandItems: false);
        }

        internal bool EvaluateForItemGroup(
            ICompiledExpressionEnvironment environment,
            IElementLocation location)
        {
            return Evaluate(
                environment,
                location,
                expandItems: true);
        }

        private bool Evaluate(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            bool expandItems)
        {
            int instructionIndex = 0;
            while (true)
            {
                CompiledConditionInstruction instruction =
                    _program.Instructions[instructionIndex];
                switch (instruction.Kind)
                {
                    case CompiledConditionInstructionKind.BranchIfComparisonFalse:
                        instructionIndex +=
                            EvaluateComparison(
                                environment,
                                location,
                                instruction.Argument0,
                                expandItems)
                                ? 1
                                : instruction.Argument1;
                        break;
                    case CompiledConditionInstructionKind.BranchIfComparisonTrue:
                        instructionIndex +=
                            EvaluateComparison(
                                environment,
                                location,
                                instruction.Argument0,
                                expandItems)
                                ? instruction.Argument1
                                : 1;
                        break;
                    case CompiledConditionInstructionKind.ReturnComparison:
                        return EvaluateComparison(
                            environment,
                            location,
                            instruction.Argument0,
                            expandItems);
                    case CompiledConditionInstructionKind.ReturnFalse:
                        return false;
                    case CompiledConditionInstructionKind.ReturnTrue:
                        return true;
                    default:
                        throw new InternalErrorException(
                            "Unknown compiled condition instruction.");
                }
            }
        }

        private bool EvaluateComparison(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int comparisonId,
            bool expandItems)
        {
            CompiledConditionComparison comparison =
                _program.Comparisons[comparisonId];
            environment.EnterConditionEvaluation(
                IsUnexpandedValueEmpty(comparison.Left) ||
                IsUnexpandedValueEmpty(comparison.Right));
            try
            {
                bool leftIsStatic =
                    TryGetStaticEmptiness(
                        comparison.Left,
                        expandItems,
                        out bool leftIsEmpty);
                bool rightIsStatic =
                    TryGetStaticEmptiness(
                        comparison.Right,
                        expandItems,
                        out bool rightIsEmpty);
                if ((leftIsStatic && leftIsEmpty) ||
                    (rightIsStatic && rightIsEmpty))
                {
                    if (!leftIsStatic)
                    {
                        leftIsEmpty =
                            EvaluateOperand(
                                environment,
                                location,
                                comparison.Left,
                                expandItems).Length == 0;
                    }

                    if (!rightIsStatic)
                    {
                        rightIsEmpty =
                            EvaluateOperand(
                                environment,
                                location,
                                comparison.Right,
                                expandItems).Length == 0;
                    }

                    bool shortCircuitEqual =
                        leftIsEmpty == rightIsEmpty;
                    return comparison.Kind == CompiledConditionKind.Equal
                        ? shortCircuitEqual
                        : !shortCircuitEqual;
                }

                bool equal = CompiledConditionUtilities.CompareValues(
                    EvaluateOperand(
                        environment,
                        location,
                        comparison.Left,
                        expandItems),
                    EvaluateOperand(
                        environment,
                        location,
                        comparison.Right,
                        expandItems),
                    out _);
                return comparison.Kind == CompiledConditionKind.Equal
                    ? equal
                    : !equal;
            }
            finally
            {
                environment.LeaveConditionEvaluation();
            }
        }

        private bool IsUnexpandedValueEmpty(
            CompiledConditionOperand operand) =>
            operand.Kind == CompiledConditionOperandKind.Literal &&
            _program.Strings[operand.Value].Length == 0;

        private bool TryGetStaticEmptiness(
            CompiledConditionOperand operand,
            bool expandItems,
            out bool isEmpty)
        {
            if (operand.Kind == CompiledConditionOperandKind.Literal)
            {
                isEmpty = _program.Strings[operand.Value].Length == 0;
                return true;
            }

            if (!expandItems &&
                operand.Kind ==
                CompiledConditionOperandKind.ExpandedValue)
            {
                for (int partIndex = operand.Value;
                     partIndex < operand.Value + operand.Count;
                     partIndex++)
                {
                    CompiledConditionValuePart part =
                        _program.ValueParts[partIndex];
                    if (part.Kind ==
                            CompiledConditionValuePartKind.Literal &&
                        _program.Strings[part.Value].Length != 0)
                    {
                        isEmpty = false;
                        return true;
                    }
                }
            }

            isEmpty = false;
            return false;
        }

        private string EvaluateOperand(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            CompiledConditionOperand operand,
            bool expandItems)
        {
            switch (operand.Kind)
            {
                case CompiledConditionOperandKind.Literal:
                    return _program.Strings[operand.Value];
                case CompiledConditionOperandKind.Property:
                    return ReadProperty(
                        environment,
                        location,
                        operand.Value,
                        unescape: true,
                        expandItems: expandItems);
                case CompiledConditionOperandKind.ExpandedValue:
                    return EvaluateExpandedValue(
                        environment,
                        location,
                        operand.Value,
                        operand.Count,
                        expandItems);
                case CompiledConditionOperandKind.Metadata:
                    return EscapingUtilities.UnescapeAll(
                        FileUtilities.MaybeAdjustFilePath(
                            environment.GetEscapedMetadataValue(
                                _program.Strings[operand.Value],
                                _program.Strings[operand.Count],
                                location)));
                default:
                    throw new InternalErrorException(
                        "Unknown compiled task condition operand.");
            }
        }

        private string EvaluateExpandedValue(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int firstPart,
            int partCount,
            bool expandItems)
        {
            var builder = new StringBuilder();
            for (int partIndex = firstPart;
                 partIndex < firstPart + partCount;
                 partIndex++)
            {
                CompiledConditionValuePart part =
                    _program.ValueParts[partIndex];
                builder.Append(EvaluateValuePart(
                    environment,
                    location,
                    part));
            }

            string escapedValue = builder.ToString();
            if (expandItems)
            {
                escapedValue = environment.ExpandItems(
                    escapedValue,
                    location);
            }

            return EscapingUtilities.UnescapeAll(
                FileUtilities.MaybeAdjustFilePath(escapedValue));
        }

        private string EvaluateValuePart(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            CompiledConditionValuePart part)
        {
            return part.Kind switch
            {
                CompiledConditionValuePartKind.Literal =>
                    _program.Strings[part.Value],
                CompiledConditionValuePartKind.Property =>
                    ReadProperty(
                        environment,
                        location,
                        part.Value,
                        unescape: false,
                        expandItems: false),
                CompiledConditionValuePartKind.Metadata =>
                    environment.GetEscapedMetadataValue(
                        _program.MetadataReferences[part.Value].ItemName,
                        _program.MetadataReferences[part.Value].MetadataName,
                        location),
                CompiledConditionValuePartKind.Function =>
                    EvaluateFunction(
                        environment,
                        location,
                        part.Value),
                _ => throw new InternalErrorException(
                    "Unknown compiled condition value part."),
            };
        }

        private string EvaluateFunction(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int functionIndex)
        {
            CompiledExpressionFunction function =
                _program.Functions[functionIndex];
            try
            {
                string receiver = function.Receiver.Count == 0
                    ? null
                    : EscapingUtilities.UnescapeAll(
                        EvaluateValue(
                            environment,
                            location,
                            function.Receiver));
                string argument0 = function.Arguments.Count > 0
                    ? EvaluateFunctionArgument(
                        environment,
                        location,
                        _program.FunctionArguments[
                            function.Arguments.Start])
                    : null;
                string argument1 = function.Arguments.Count > 1
                    ? EvaluateFunctionArgument(
                        environment,
                        location,
                        _program.FunctionArguments[
                            function.Arguments.Start + 1])
                    : null;
                return EscapingUtilities.Escape(
                    CompiledExpressionFunctionUtilities.Evaluate(
                        function.Kind,
                        receiver,
                        argument0,
                        argument1));
            }
            catch (Exception ex)
                when (!ExceptionHandling.NotExpectedFunctionException(ex))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    location,
                    "InvalidFunctionPropertyExpression",
                    function.Expression,
                    ex.Message.Replace("\r\n", " "));
                return null;
            }
        }

        private string EvaluateFunctionArgument(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            CompiledExpressionFunctionArgument argument) =>
            EscapingUtilities.UnescapeAll(
                FileUtilities.MaybeAdjustFilePath(
                    EvaluateValue(
                        environment,
                        location,
                        argument.ValueParts)));

        private string EvaluateValue(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            TableRange range)
        {
            if (range.Count == 1)
            {
                return EvaluateValuePart(
                    environment,
                    location,
                    _program.ValueParts[range.Start]);
            }

            var builder = new StringBuilder();
            for (int i = 0; i < range.Count; i++)
            {
                builder.Append(EvaluateValuePart(
                    environment,
                    location,
                    _program.ValueParts[range.Start + i]));
            }

            return builder.ToString();
        }

        private string ReadProperty(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int propertyIndex,
            bool unescape,
            bool expandItems)
        {
            string escapedValue = environment.GetEscapedPropertyValue(
                _program.PropertyNames[propertyIndex],
                location);
            if (expandItems)
            {
                escapedValue = environment.ExpandItems(
                    escapedValue,
                    location);
            }

            return unescape
                ? EscapingUtilities.UnescapeAll(
                    FileUtilities.MaybeAdjustFilePath(escapedValue))
                : escapedValue;
        }
    }

    internal sealed class CompiledScalarProgram
    {
        private readonly string[] _strings;
        private readonly string[] _propertyNames;
        private readonly MetadataReference[] _metadataReferences;
        private readonly CompiledConditionValuePart[] _parts;
        private readonly CompiledExpressionFunction[] _functions;
        private readonly CompiledExpressionFunctionArgument[] _functionArguments;
        private readonly TableRange _root;

        private CompiledScalarProgram(CompiledScalarProgramData program)
        {
            _strings = program.Strings;
            _propertyNames = program.PropertyNames;
            _metadataReferences = program.MetadataReferences;
            _parts = program.ValueParts;
            _functions = program.Functions;
            _functionArguments = program.FunctionArguments;
            _root = program.Root;
        }

        internal static CompiledScalarProgram TryCreate(string expression)
        {
            return CompiledConditionCompiler.TryCompileScalar(
                expression,
                allowItemVectors: false,
                allowMetadata: false,
                allowPropertyFunctions: true,
                out CompiledScalarProgramData program)
                ? new CompiledScalarProgram(program)
                : null;
        }

        internal static CompiledScalarProgram TryCreateItemSpecification(
            string expression)
        {
            return CompiledConditionCompiler.TryCompileScalar(
                expression,
                allowItemVectors: true,
                allowMetadata: true,
                allowPropertyFunctions: true,
                out CompiledScalarProgramData program)
                ? new CompiledScalarProgram(program)
                : null;
        }

        internal string Evaluate(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            string baseDirectory = "")
        {
            string escapedValue = EvaluateLeaveEscaped(
                environment,
                location,
                baseDirectory);
            return EscapingUtilities.UnescapeAll(escapedValue);
        }

        internal string EvaluateLeaveEscaped(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            string baseDirectory = "")
        {
            string escapedValue;
            if (_root.Count == 1)
            {
                escapedValue = EvaluatePart(
                    environment,
                    location,
                    _parts[_root.Start]);
            }
            else
            {
                var builder = new StringBuilder();
                for (int i = 0; i < _root.Count; i++)
                {
                    builder.Append(EvaluatePart(
                        environment,
                        location,
                        _parts[_root.Start + i]));
                }

                escapedValue = builder.ToString();
            }

            return escapedValue.IndexOf('\\') == -1
                ? escapedValue
                : FileUtilities.MaybeAdjustFilePath(escapedValue, baseDirectory);
        }

        internal bool TryEvaluateConstant(
            IElementLocation location,
            out string value)
        {
            if (_propertyNames.Length != 0 ||
                _metadataReferences.Length != 0 ||
                _functions.Length != 0)
            {
                value = null;
                return false;
            }

            for (int i = 0; i < _parts.Length; i++)
            {
                if (_parts[i].Kind ==
                        CompiledConditionValuePartKind.Literal &&
                    _strings[_parts[i].Value].IndexOf('\\') != -1)
                {
                    // Unix path adjustment depends on the current project directory.
                    value = null;
                    return false;
                }
            }

            value = Evaluate(environment: null, location);
            return true;
        }

        private string EvaluatePart(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            CompiledConditionValuePart part)
        {
            return part.Kind switch
            {
                CompiledConditionValuePartKind.Literal =>
                    _strings[part.Value],
                CompiledConditionValuePartKind.Property =>
                    environment.GetEscapedPropertyValue(
                        _propertyNames[part.Value],
                        location),
                CompiledConditionValuePartKind.Metadata =>
                    GetEscapedMetadataValue(
                        environment,
                        location,
                        part.Value),
                CompiledConditionValuePartKind.Function =>
                    EvaluateFunction(
                        environment,
                        location,
                        part.Value),
                _ => throw new InternalErrorException(
                    "Unknown compiled scalar value part."),
            };
        }

        private string EvaluateFunction(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int functionIndex)
        {
            CompiledExpressionFunction function = _functions[functionIndex];
            try
            {
                string receiver = function.Receiver.Count == 0
                    ? null
                    : EscapingUtilities.UnescapeAll(
                        EvaluateValue(
                            environment,
                            location,
                            function.Receiver));
                string argument0 = function.Arguments.Count > 0
                    ? EvaluateFunctionArgument(
                        environment,
                        location,
                        _functionArguments[function.Arguments.Start])
                    : null;
                string argument1 = function.Arguments.Count > 1
                    ? EvaluateFunctionArgument(
                        environment,
                        location,
                        _functionArguments[function.Arguments.Start + 1])
                    : null;
                string result = CompiledExpressionFunctionUtilities.Evaluate(
                    function.Kind,
                    receiver,
                    argument0,
                    argument1);
                return EscapingUtilities.Escape(result);
            }
            catch (Exception ex)
                when (!ExceptionHandling.NotExpectedFunctionException(ex))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    location,
                    "InvalidFunctionPropertyExpression",
                    function.Expression,
                    ex.Message.Replace("\r\n", " "));
                return null;
            }
        }

        private string EvaluateFunctionArgument(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            CompiledExpressionFunctionArgument argument) =>
            EscapingUtilities.UnescapeAll(
                FileUtilities.MaybeAdjustFilePath(
                    EvaluateValue(
                        environment,
                        location,
                        argument.ValueParts)));

        private string EvaluateValue(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            TableRange range)
        {
            if (range.Count == 1)
            {
                return EvaluatePart(
                    environment,
                    location,
                    _parts[range.Start]);
            }

            var builder = new StringBuilder();
            for (int i = 0; i < range.Count; i++)
            {
                builder.Append(
                    EvaluatePart(
                        environment,
                        location,
                        _parts[range.Start + i]));
            }

            return builder.ToString();
        }

        private string GetEscapedMetadataValue(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int metadataReferenceIndex)
        {
            MetadataReference metadata =
                _metadataReferences[metadataReferenceIndex];
            return environment.GetEscapedMetadataValue(
                metadata.ItemName,
                metadata.MetadataName,
                location);
        }
    }

    internal sealed class CompiledScalarProgramData
    {
        internal CompiledScalarProgramData(
            string[] strings,
            string[] propertyNames,
            MetadataReference[] metadataReferences,
            CompiledConditionValuePart[] valueParts,
            CompiledExpressionFunction[] functions,
            CompiledExpressionFunctionArgument[] functionArguments,
            TableRange root)
        {
            Strings = strings;
            PropertyNames = propertyNames;
            MetadataReferences = metadataReferences;
            ValueParts = valueParts;
            Functions = functions;
            FunctionArguments = functionArguments;
            Root = root;
        }

        internal string[] Strings { get; }

        internal string[] PropertyNames { get; }

        internal MetadataReference[] MetadataReferences { get; }

        internal CompiledConditionValuePart[] ValueParts { get; }

        internal CompiledExpressionFunction[] Functions { get; }

        internal CompiledExpressionFunctionArgument[] FunctionArguments { get; }

        internal TableRange Root { get; }
    }

    internal sealed class CompiledConditionProgramData
    {
        internal CompiledConditionProgramData(
            string[] strings,
            string[] propertyNames,
            MetadataReference[] metadataReferences,
            CompiledConditionInstruction[] instructions,
            CompiledConditionComparison[] comparisons,
            CompiledConditionValuePart[] valueParts,
            CompiledExpressionFunction[] functions,
            CompiledExpressionFunctionArgument[] functionArguments)
        {
            Strings = strings;
            PropertyNames = propertyNames;
            MetadataReferences = metadataReferences;
            Instructions = instructions;
            Comparisons = comparisons;
            ValueParts = valueParts;
            Functions = functions;
            FunctionArguments = functionArguments;
        }

        internal string[] Strings { get; }

        internal string[] PropertyNames { get; }

        internal MetadataReference[] MetadataReferences { get; }

        internal CompiledConditionInstruction[] Instructions { get; }

        internal CompiledConditionComparison[] Comparisons { get; }

        internal CompiledConditionValuePart[] ValueParts { get; }

        internal CompiledExpressionFunction[] Functions { get; }

        internal CompiledExpressionFunctionArgument[] FunctionArguments { get; }
    }

    internal static class CompiledConditionCompiler
    {
        internal static bool TryCompile(
            string condition,
            ParserOptions parserOptions,
            ElementLocation location,
            out CompiledConditionProgramData program,
            bool allowExtendedExpressions = false)
        {
            program = null;
            if (string.IsNullOrEmpty(condition))
            {
                return false;
            }

            GenericExpressionNode expression;
            try
            {
                expression = new Parser().Parse(
                    condition,
                    parserOptions,
                    location);
            }
            catch (InvalidProjectFileException)
            {
                return false;
            }

            if (expression.PotentialAndOrConflict())
            {
                return false;
            }

            var builder = new Builder(
                allowItemVectors: allowExtendedExpressions &&
                    parserOptions != ParserOptions.AllowProperties,
                allowMetadata:
                    parserOptions == ParserOptions.AllowAll,
                allowPropertyFunctions: allowExtendedExpressions,
                allowMetadataValueParts: allowExtendedExpressions);
            if (!builder.TryCompile(expression))
            {
                return false;
            }

            program = builder.ToProgram();
            return true;
        }

        internal static bool TryCompileScalar(
            string expression,
            bool allowItemVectors,
            bool allowMetadata,
            bool allowPropertyFunctions,
            out CompiledScalarProgramData program)
        {
            bool containsItemVector =
                ExpressionShredder.ContainsItemVectorMarker(expression);
            if (allowMetadata &&
                containsItemVector &&
                ExpressionShredder
                    .ContainsMetadataExpressionOutsideTransform(expression))
            {
                program = null;
                return false;
            }

            var builder = new Builder(
                allowItemVectors,
                allowMetadata,
                allowPropertyFunctions && !containsItemVector,
                allowMetadataValueParts:
                    allowMetadata && !containsItemVector);
            if (!builder.TryCompileValue(
                    expression,
                    out TableRange root))
            {
                program = null;
                return false;
            }

            program = builder.ToScalarProgram(root);
            return true;
        }

        private sealed class Builder
        {
            private readonly bool _allowItemVectors;
            private readonly bool _allowMetadata;
            private readonly bool _allowPropertyFunctions;
            private readonly bool _allowMetadataValueParts;
            internal readonly List<string> _strings = new();
            internal readonly List<string> _propertyNames = new();
            internal readonly List<CompiledConditionValuePart> _valueParts = new();
            private readonly List<MetadataReference> _metadataReferences = new();
            private readonly List<CompiledExpressionFunction> _functions = new();
            private readonly List<CompiledExpressionFunctionArgument>
                _functionArguments = new();
            private readonly Dictionary<string, int> _stringIds =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _propertyIds =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _metadataReferenceIds =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly List<CompiledConditionInstruction> _instructions = new();
            private readonly List<CompiledConditionComparison> _comparisons = new();

            internal Builder(
                bool allowItemVectors,
                bool allowMetadata,
                bool allowPropertyFunctions,
                bool allowMetadataValueParts)
            {
                _allowItemVectors = allowItemVectors;
                _allowMetadata = allowMetadata;
                _allowPropertyFunctions = allowPropertyFunctions;
                _allowMetadataValueParts = allowMetadataValueParts;
            }

            internal bool TryCompile(GenericExpressionNode expression)
            {
                if (expression is StringExpressionNode booleanLiteral &&
                    ConversionUtilities.TryConvertStringToBool(
                        booleanLiteral.UnexpandedValue,
                        out bool literalValue))
                {
                    _instructions.Add(
                        new CompiledConditionInstruction(
                            literalValue
                                ? CompiledConditionInstructionKind.ReturnTrue
                                : CompiledConditionInstructionKind.ReturnFalse));
                    return true;
                }

                if (expression is EqualExpressionNode or NotEqualExpressionNode)
                {
                    if (!TryAddComparison(expression, out int comparisonId))
                    {
                        return false;
                    }

                    _instructions.Add(
                        new CompiledConditionInstruction(
                            CompiledConditionInstructionKind.ReturnComparison,
                            comparisonId));
                    return true;
                }

                var falseBranches = new List<int>();
                if (!TryEmitBranchIfFalse(expression, falseBranches))
                {
                    return false;
                }

                _instructions.Add(
                    new CompiledConditionInstruction(
                        CompiledConditionInstructionKind.ReturnTrue));
                int falseTarget = _instructions.Count;
                _instructions.Add(
                    new CompiledConditionInstruction(
                        CompiledConditionInstructionKind.ReturnFalse));
                PatchBranches(falseBranches, falseTarget);
                return true;
            }

            internal CompiledConditionProgramData ToProgram() =>
                new(
                    _strings.ToArray(),
                    _propertyNames.ToArray(),
                    _metadataReferences.ToArray(),
                    _instructions.ToArray(),
                    _comparisons.ToArray(),
                    _valueParts.ToArray(),
                    _functions.ToArray(),
                    _functionArguments.ToArray());

            internal CompiledScalarProgramData ToScalarProgram(
                TableRange root) =>
                new(
                    _strings.ToArray(),
                    _propertyNames.ToArray(),
                    _metadataReferences.ToArray(),
                    _valueParts.ToArray(),
                    _functions.ToArray(),
                    _functionArguments.ToArray(),
                    root);

            private bool TryEmitBranchIfFalse(
                GenericExpressionNode expression,
                List<int> targetBranches)
            {
                if (expression is AndExpressionNode and)
                {
                    return TryEmitBranchIfFalse(and.LeftChild, targetBranches) &&
                        TryEmitBranchIfFalse(and.RightChild, targetBranches);
                }

                if (expression is OrExpressionNode or)
                {
                    var trueBranches = new List<int>();
                    if (!TryEmitBranchIfTrue(or.LeftChild, trueBranches) ||
                        !TryEmitBranchIfFalse(or.RightChild, targetBranches))
                    {
                        return false;
                    }

                    PatchBranches(trueBranches, _instructions.Count);
                    return true;
                }

                if (!TryAddComparison(expression, out int comparisonId))
                {
                    return false;
                }

                targetBranches.Add(_instructions.Count);
                _instructions.Add(
                    new CompiledConditionInstruction(
                        CompiledConditionInstructionKind.BranchIfComparisonFalse,
                        comparisonId));
                return true;
            }

            private bool TryEmitBranchIfTrue(
                GenericExpressionNode expression,
                List<int> targetBranches)
            {
                if (expression is OrExpressionNode or)
                {
                    return TryEmitBranchIfTrue(or.LeftChild, targetBranches) &&
                        TryEmitBranchIfTrue(or.RightChild, targetBranches);
                }

                if (expression is AndExpressionNode and)
                {
                    var falseBranches = new List<int>();
                    if (!TryEmitBranchIfFalse(and.LeftChild, falseBranches) ||
                        !TryEmitBranchIfTrue(and.RightChild, targetBranches))
                    {
                        return false;
                    }

                    PatchBranches(falseBranches, _instructions.Count);
                    return true;
                }

                if (!TryAddComparison(expression, out int comparisonId))
                {
                    return false;
                }

                targetBranches.Add(_instructions.Count);
                _instructions.Add(
                    new CompiledConditionInstruction(
                        CompiledConditionInstructionKind.BranchIfComparisonTrue,
                        comparisonId));
                return true;
            }

            private void PatchBranches(List<int> branches, int target)
            {
                foreach (int branch in branches)
                {
                    CompiledConditionInstruction instruction =
                        _instructions[branch];
                    _instructions[branch] =
                        new CompiledConditionInstruction(
                            instruction.Kind,
                            instruction.Argument0,
                            target - branch);
                }
            }

            private bool TryAddComparison(
                GenericExpressionNode expression,
                out int comparisonId)
            {
                if (expression is StringExpressionNode booleanExpression &&
                    booleanExpression.IsExpandable &&
                    TryCompileOperand(
                        booleanExpression,
                        out CompiledConditionOperand booleanOperand) &&
                    IsSingleFunction(booleanOperand))
                {
                    comparisonId = _comparisons.Count;
                    int trueStringId = GetStringId("true");
                    _comparisons.Add(
                        new CompiledConditionComparison(
                            CompiledConditionKind.Equal,
                            booleanOperand,
                            new CompiledConditionOperand(
                                CompiledConditionOperandKind.Literal,
                                trueStringId),
                            GetStringId(booleanExpression.UnexpandedValue),
                            trueStringId));
                    return true;
                }

                CompiledConditionKind kind;
                if (expression is EqualExpressionNode)
                {
                    kind = CompiledConditionKind.Equal;
                }
                else if (expression is NotEqualExpressionNode)
                {
                    kind = CompiledConditionKind.NotEqual;
                }
                else
                {
                    comparisonId = 0;
                    return false;
                }

                var comparison = (OperatorExpressionNode)expression;
                if (comparison.LeftChild is not StringExpressionNode left ||
                    comparison.RightChild is not StringExpressionNode right ||
                    !TryCompileOperand(left, out CompiledConditionOperand leftOperand) ||
                    !TryCompileOperand(right, out CompiledConditionOperand rightOperand))
                {
                    comparisonId = 0;
                    return false;
                }

                comparisonId = _comparisons.Count;
                _comparisons.Add(
                    new CompiledConditionComparison(
                        kind,
                        leftOperand,
                        rightOperand,
                        GetStringId(left.UnexpandedValue),
                        GetStringId(right.UnexpandedValue)));
                return true;
            }

            private bool IsSingleFunction(
                CompiledConditionOperand operand) =>
                operand.Kind ==
                    CompiledConditionOperandKind.ExpandedValue &&
                operand.Count == 1 &&
                _valueParts[operand.Value].Kind ==
                    CompiledConditionValuePartKind.Function;

            private bool TryCompileOperand(
                StringExpressionNode operand,
                out CompiledConditionOperand compiledOperand)
            {
                string value = operand.UnexpandedValue;
                if (_allowMetadata &&
                    value.StartsWith("%(", StringComparison.Ordinal))
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
                    if (!CanCompileProperty(propertyName))
                    {
                        compiledOperand = default;
                        return false;
                    }

                    compiledOperand = new CompiledConditionOperand(
                        CompiledConditionOperandKind.Property,
                        GetPropertyId(propertyName));
                    return true;
                }

                if (operand.IsExpandable)
                {
                    if (!TryCompileValue(value, out TableRange parts))
                    {
                        compiledOperand = default;
                        return false;
                    }

                    compiledOperand = new CompiledConditionOperand(
                        CompiledConditionOperandKind.ExpandedValue,
                        parts.Start,
                        parts.Count);
                    return true;
                }

                compiledOperand = new CompiledConditionOperand(
                    CompiledConditionOperandKind.Literal,
                    GetStringId(value));
                return true;
            }

            internal bool TryCompileValue(
                string value,
                out TableRange parts)
            {
                if ((!_allowItemVectors &&
                     value.Contains("@(", StringComparison.Ordinal)) ||
                    (!_allowMetadata &&
                     value.Contains("%(", StringComparison.Ordinal)))
                {
                    parts = default;
                    return false;
                }

                if (!TryCompileValueParts(
                        value,
                        out List<CompiledConditionValuePart> compiledParts))
                {
                    parts = default;
                    return false;
                }

                int partStart = _valueParts.Count;
                _valueParts.AddRange(compiledParts);
                parts = new TableRange(
                    partStart,
                    compiledParts.Count);
                return true;
            }

            private bool TryCompileValueParts(
                string value,
                out List<CompiledConditionValuePart> parts)
            {
                parts = new List<CompiledConditionValuePart>();
                int sourceIndex = 0;
                while (sourceIndex < value.Length)
                {
                    int propertyStart = value.IndexOf(
                        "$(",
                        sourceIndex,
                        StringComparison.Ordinal);
                    int metadataStart = _allowMetadataValueParts
                        ? value.IndexOf(
                            "%(",
                            sourceIndex,
                            StringComparison.Ordinal)
                        : -1;
                    int expansionStart =
                        propertyStart < 0
                            ? metadataStart
                            : metadataStart < 0
                                ? propertyStart
                                : Math.Min(propertyStart, metadataStart);
                    if (expansionStart < 0)
                    {
                        AddLiteral(
                            value,
                            sourceIndex,
                            value.Length - sourceIndex,
                            parts);
                        sourceIndex = value.Length;
                        break;
                    }

                    AddLiteral(
                        value,
                        sourceIndex,
                        expansionStart - sourceIndex,
                        parts);
                    if (expansionStart == metadataStart)
                    {
                        int metadataEnd = metadataStart + 2;
                        if (!ExpressionShredder.TryParseMetadataExpression(
                                value,
                                ref metadataEnd,
                                value.Length,
                                out string itemType,
                                out string metadataName))
                        {
                            parts = null;
                            return false;
                        }

                        parts.Add(new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Metadata,
                            GetMetadataReferenceId(
                                itemType,
                                metadataName)));
                        sourceIndex = metadataEnd;
                        continue;
                    }

                    int propertyEnd =
                        FindClosingParenthesis(
                            value,
                            propertyStart + 2);
                    if (propertyEnd < 0)
                    {
                        parts = null;
                        return false;
                    }

                    string propertyBody = value.Substring(
                        propertyStart + 2,
                        propertyEnd - propertyStart - 2);
                    if (CanCompileProperty(propertyBody))
                    {
                        parts.Add(new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Property,
                            GetPropertyId(propertyBody)));
                    }
                    else if (!_allowPropertyFunctions ||
                             !TryCompilePropertyFunction(
                                 propertyBody,
                                 out int functionIndex))
                    {
                        parts = null;
                        return false;
                    }
                    else
                    {
                        parts.Add(new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Function,
                            functionIndex));
                    }

                    sourceIndex = propertyEnd + 1;
                }

                if (parts.Count == 0)
                {
                    parts.Add(new CompiledConditionValuePart(
                        CompiledConditionValuePartKind.Literal,
                        GetStringId(string.Empty)));
                }

                return true;
            }

            private void AddLiteral(
                string value,
                int start,
                int length,
                List<CompiledConditionValuePart> parts)
            {
                if (length != 0)
                {
                    parts.Add(new CompiledConditionValuePart(
                        CompiledConditionValuePartKind.Literal,
                        GetStringId(value.Substring(start, length))));
                }
            }

            private bool TryCompilePropertyFunction(
                string body,
                out int functionIndex)
            {
                const string intrinsicPrefix = "[MSBuild]::";
                string receiverName = null;
                string methodName;
                int argumentsStart;
                if (body.StartsWith(
                        intrinsicPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
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
                    if (!CompiledExpressionFunctionUtilities
                            .TryGetIntrinsicKind(
                                methodName,
                                out CompiledPropertyFunctionKind kind))
                    {
                        functionIndex = 0;
                        return false;
                    }

                    return TryAddFunction(
                        body,
                        kind,
                        receiverName,
                        argumentsStart,
                        minimumArgumentCount: 2,
                        maximumArgumentCount: 2,
                        out functionIndex);
                }

                int receiverEnd = body.IndexOf('.');
                if (receiverEnd <= 0)
                {
                    functionIndex = 0;
                    return false;
                }

                receiverName = body.Substring(0, receiverEnd).Trim();
                if (!CanCompileProperty(receiverName))
                {
                    functionIndex = 0;
                    return false;
                }

                int receiverMethodStart = receiverEnd + 1;
                argumentsStart = body.IndexOf(
                    '(',
                    receiverMethodStart);
                if (argumentsStart < 0)
                {
                    functionIndex = 0;
                    return false;
                }

                methodName = body.Substring(
                        receiverMethodStart,
                        argumentsStart - receiverMethodStart)
                    .Trim();
                if (!methodName.Equals(
                        nameof(string.Contains),
                        StringComparison.OrdinalIgnoreCase))
                {
                    functionIndex = 0;
                    return false;
                }

                return TryAddFunction(
                    body,
                    CompiledPropertyFunctionKind.StringContains,
                    receiverName,
                    argumentsStart,
                    minimumArgumentCount: 1,
                    maximumArgumentCount: 1,
                    out functionIndex);
            }

            private bool TryAddFunction(
                string body,
                CompiledPropertyFunctionKind kind,
                string receiverName,
                int argumentsStart,
                int minimumArgumentCount,
                int maximumArgumentCount,
                out int functionIndex)
            {
                int argumentsEnd =
                    FindClosingParenthesis(
                        body,
                        argumentsStart + 1);
                if (argumentsEnd < 0 ||
                    body.Substring(argumentsEnd + 1).Trim().Length != 0 ||
                    !TrySplitFunctionArguments(
                        body,
                        argumentsStart + 1,
                        argumentsEnd,
                        out List<string> argumentValues) ||
                    argumentValues.Count < minimumArgumentCount ||
                    argumentValues.Count > maximumArgumentCount)
                {
                    functionIndex = 0;
                    return false;
                }

                TableRange receiver = default;
                if (receiverName != null)
                {
                    int receiverStart = _valueParts.Count;
                    _valueParts.Add(new CompiledConditionValuePart(
                        CompiledConditionValuePartKind.Property,
                        GetPropertyId(receiverName)));
                    receiver = new TableRange(receiverStart, 1);
                }

                var argumentRanges =
                    new List<TableRange>(argumentValues.Count);
                foreach (string argumentValue in argumentValues)
                {
                    if (argumentValue == null ||
                        !TryCompileValueParts(
                            argumentValue,
                            out List<CompiledConditionValuePart>
                                argumentParts))
                    {
                        functionIndex = 0;
                        return false;
                    }

                    int valueStart = _valueParts.Count;
                    _valueParts.AddRange(argumentParts);
                    argumentRanges.Add(new TableRange(
                        valueStart,
                        argumentParts.Count));
                }

                int argumentStart = _functionArguments.Count;
                foreach (TableRange argumentRange in argumentRanges)
                {
                    _functionArguments.Add(
                        new CompiledExpressionFunctionArgument(
                            argumentRange));
                }

                functionIndex = _functions.Count;
                _functions.Add(new CompiledExpressionFunction(
                    kind,
                    receiver,
                    new TableRange(
                        argumentStart,
                        argumentValues.Count),
                    body));
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
                        int quoteEnd =
                            expression.IndexOf(current, i + 1);
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
                            FindClosingParenthesis(
                                expression,
                                i + 2);
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
                string argument =
                    expression.Substring(start, length).Trim();
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
                    return argument.Substring(
                        1,
                        argument.Length - 2);
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

            private int GetStringId(string value)
            {
                if (!_stringIds.TryGetValue(value, out int id))
                {
                    id = _strings.Count;
                    _strings.Add(value);
                    _stringIds.Add(value, id);
                }

                return id;
            }

            private int GetPropertyId(string propertyName)
            {
                if (!_propertyIds.TryGetValue(propertyName, out int id))
                {
                    id = _propertyNames.Count;
                    _propertyNames.Add(propertyName);
                    _propertyIds.Add(propertyName, id);
                }

                return id;
            }

            private int GetMetadataReferenceId(
                string itemType,
                string metadataName)
            {
                string key = itemType == null
                    ? metadataName
                    : $"{itemType}.{metadataName}";
                if (!_metadataReferenceIds.TryGetValue(key, out int id))
                {
                    id = _metadataReferences.Count;
                    _metadataReferences.Add(
                        new MetadataReference(itemType, metadataName));
                    _metadataReferenceIds.Add(key, id);
                }

                return id;
            }

            private static bool CanCompileProperty(string propertyName)
            {
                if (propertyName.Length == 0 ||
                    propertyName.StartsWith(
                        "Registry:",
                        StringComparison.OrdinalIgnoreCase) ||
                    propertyName.Equals(
                        "MSBuildToolsVersion",
                        StringComparison.OrdinalIgnoreCase) ||
                    IsContextualPropertyName(propertyName) ||
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
        }
    }

    internal static class CompiledExpressionFunctionUtilities
    {
        internal static bool TryGetIntrinsicKind(
            string methodName,
            out CompiledPropertyFunctionKind kind)
        {
            if (methodName.Equals(
                    nameof(IntrinsicFunctions.IsTargetFrameworkCompatible),
                    StringComparison.OrdinalIgnoreCase))
            {
                kind =
                    CompiledPropertyFunctionKind.IsTargetFrameworkCompatible;
                return true;
            }

            if (methodName.Equals(
                    nameof(IntrinsicFunctions.VersionEquals),
                    StringComparison.OrdinalIgnoreCase))
            {
                kind = CompiledPropertyFunctionKind.VersionEquals;
                return true;
            }

            if (methodName.Equals(
                    nameof(IntrinsicFunctions.VersionGreaterThanOrEquals),
                    StringComparison.OrdinalIgnoreCase))
            {
                kind =
                    CompiledPropertyFunctionKind.VersionGreaterThanOrEquals;
                return true;
            }

            kind = default;
            return false;
        }

        internal static string Evaluate(
            CompiledPropertyFunctionKind kind,
            string receiver,
            string argument0,
            string argument1)
        {
            bool result = kind switch
            {
                CompiledPropertyFunctionKind.IsTargetFrameworkCompatible =>
                    IntrinsicFunctions.IsTargetFrameworkCompatible(
                        argument0,
                        argument1),
                CompiledPropertyFunctionKind.VersionEquals =>
                    IntrinsicFunctions.VersionEquals(
                        argument0,
                        argument1),
                CompiledPropertyFunctionKind.VersionGreaterThanOrEquals =>
                    IntrinsicFunctions.VersionGreaterThanOrEquals(
                        argument0,
                        argument1),
                CompiledPropertyFunctionKind.StringContains =>
                    receiver.Contains(argument0),
                _ => throw new InternalErrorException(
                    "Unknown shared compiled property function."),
            };
            return Convert.ToString(
                result,
                CultureInfo.InvariantCulture);
        }
    }

    internal static class CompiledConditionUtilities
    {
        internal static bool CompareValues(
            string left,
            string right,
            out bool updateConditionedProperties)
        {
            bool leftEmpty = left.Length == 0;
            bool rightEmpty = right.Length == 0;
            if (leftEmpty || rightEmpty)
            {
                updateConditionedProperties = true;
                return leftEmpty == rightEmpty;
            }

            if (ConversionUtilities.TryConvertDecimalOrHexToDouble(
                    left,
                    out double leftNumber) &&
                ConversionUtilities.TryConvertDecimalOrHexToDouble(
                    right,
                    out double rightNumber))
            {
                updateConditionedProperties = false;
                return leftNumber == rightNumber;
            }

            if (ConversionUtilities.TryConvertStringToBool(
                    left,
                    out bool leftBoolean) &&
                ConversionUtilities.TryConvertStringToBool(
                    right,
                    out bool rightBoolean))
            {
                updateConditionedProperties = false;
                return leftBoolean == rightBoolean;
            }

            updateConditionedProperties = true;
            return string.Equals(
                left,
                right,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
