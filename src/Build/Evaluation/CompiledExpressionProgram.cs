// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
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

        void EnterConditionEvaluation(bool oneSideIsEmpty);

        void LeaveConditionEvaluation();
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
                out CompiledConditionProgramData program)
                ? new CompiledConditionProgram(program)
                : null;
        }

        internal bool Evaluate(
            ICompiledExpressionEnvironment environment,
            IElementLocation location)
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
                            EvaluateComparison(environment, location, instruction.Argument0)
                                ? 1
                                : instruction.Argument1;
                        break;
                    case CompiledConditionInstructionKind.BranchIfComparisonTrue:
                        instructionIndex +=
                            EvaluateComparison(environment, location, instruction.Argument0)
                                ? instruction.Argument1
                                : 1;
                        break;
                    case CompiledConditionInstructionKind.ReturnComparison:
                        return EvaluateComparison(environment, location, instruction.Argument0);
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
            int comparisonId)
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
                        out bool leftIsEmpty);
                bool rightIsStatic =
                    TryGetStaticEmptiness(
                        comparison.Right,
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
                                comparison.Left).Length == 0;
                    }

                    if (!rightIsStatic)
                    {
                        rightIsEmpty =
                            EvaluateOperand(
                                environment,
                                location,
                                comparison.Right).Length == 0;
                    }

                    bool shortCircuitEqual =
                        leftIsEmpty == rightIsEmpty;
                    return comparison.Kind == CompiledConditionKind.Equal
                        ? shortCircuitEqual
                        : !shortCircuitEqual;
                }

                bool equal = CompiledConditionUtilities.CompareValues(
                    EvaluateOperand(environment, location, comparison.Left),
                    EvaluateOperand(environment, location, comparison.Right),
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
            out bool isEmpty)
        {
            if (operand.Kind == CompiledConditionOperandKind.Literal)
            {
                isEmpty = _program.Strings[operand.Value].Length == 0;
                return true;
            }

            if (operand.Kind ==
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
            CompiledConditionOperand operand)
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
                        unescape: true);
                case CompiledConditionOperandKind.ExpandedValue:
                    return EvaluateExpandedValue(
                        environment,
                        location,
                        operand.Value,
                        operand.Count);
                default:
                    throw new InternalErrorException(
                        "Unknown compiled task condition operand.");
            }
        }

        private string EvaluateExpandedValue(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int firstPart,
            int partCount)
        {
            var builder = new StringBuilder();
            for (int partIndex = firstPart;
                 partIndex < firstPart + partCount;
                 partIndex++)
            {
                CompiledConditionValuePart part =
                    _program.ValueParts[partIndex];
                builder.Append(
                    part.Kind == CompiledConditionValuePartKind.Literal
                        ? _program.Strings[part.Value]
                        : ReadProperty(
                            environment,
                            location,
                            part.Value,
                            unescape: false));
            }

            return EscapingUtilities.UnescapeAll(
                FileUtilities.MaybeAdjustFilePath(builder.ToString()));
        }

        private string ReadProperty(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            int propertyIndex,
            bool unescape)
        {
            string escapedValue = environment.GetEscapedPropertyValue(
                _program.PropertyNames[propertyIndex],
                location);
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
        private readonly CompiledConditionValuePart[] _parts;

        private CompiledScalarProgram(
            string[] strings,
            string[] propertyNames,
            CompiledConditionValuePart[] parts)
        {
            _strings = strings;
            _propertyNames = propertyNames;
            _parts = parts;
        }

        internal static CompiledScalarProgram TryCreate(string expression)
        {
            return CompiledConditionCompiler.TryCompileScalar(
                expression,
                out string[] strings,
                out string[] propertyNames,
                out CompiledConditionValuePart[] parts)
                ? new CompiledScalarProgram(strings, propertyNames, parts)
                : null;
        }

        internal string Evaluate(
            ICompiledExpressionEnvironment environment,
            IElementLocation location,
            string baseDirectory = "")
        {
            string escapedValue;
            if (_parts.Length == 1)
            {
                escapedValue = EvaluatePart(environment, location, _parts[0]);
            }
            else
            {
                var builder = new StringBuilder();
                for (int i = 0; i < _parts.Length; i++)
                {
                    builder.Append(EvaluatePart(environment, location, _parts[i]));
                }

                escapedValue = builder.ToString();
            }

            string adjustedValue =
                escapedValue.IndexOf('\\') == -1
                    ? escapedValue
                    : FileUtilities.MaybeAdjustFilePath(escapedValue, baseDirectory);
            return EscapingUtilities.UnescapeAll(adjustedValue);
        }

        internal bool TryEvaluateConstant(
            IElementLocation location,
            out string value)
        {
            if (_propertyNames.Length != 0)
            {
                value = null;
                return false;
            }

            for (int i = 0; i < _parts.Length; i++)
            {
                if (_strings[_parts[i].Value].IndexOf('\\') != -1)
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
            return part.Kind == CompiledConditionValuePartKind.Literal
                ? _strings[part.Value]
                : environment.GetEscapedPropertyValue(
                    _propertyNames[part.Value],
                    location);
        }
    }

    internal sealed class CompiledConditionProgramData
    {
        internal CompiledConditionProgramData(
            string[] strings,
            string[] propertyNames,
            CompiledConditionInstruction[] instructions,
            CompiledConditionComparison[] comparisons,
            CompiledConditionValuePart[] valueParts)
        {
            Strings = strings;
            PropertyNames = propertyNames;
            Instructions = instructions;
            Comparisons = comparisons;
            ValueParts = valueParts;
        }

        internal string[] Strings { get; }

        internal string[] PropertyNames { get; }

        internal CompiledConditionInstruction[] Instructions { get; }

        internal CompiledConditionComparison[] Comparisons { get; }

        internal CompiledConditionValuePart[] ValueParts { get; }
    }

    internal static class CompiledConditionCompiler
    {
        internal static bool TryCompile(
            string condition,
            ParserOptions parserOptions,
            ElementLocation location,
            out CompiledConditionProgramData program)
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

            var builder = new Builder();
            if (!builder.TryCompile(expression))
            {
                return false;
            }

            program = builder.ToProgram();
            return true;
        }

        internal static bool TryCompileScalar(
            string expression,
            out string[] strings,
            out string[] propertyNames,
            out CompiledConditionValuePart[] parts)
        {
            var builder = new Builder();
            if (!builder.TryCompileValue(expression, out _))
            {
                strings = null;
                propertyNames = null;
                parts = null;
                return false;
            }

            strings = builder._strings.ToArray();
            propertyNames = builder._propertyNames.ToArray();
            parts = builder._valueParts.ToArray();
            return true;
        }

        private sealed class Builder
        {
            internal readonly List<string> _strings = new();
            internal readonly List<string> _propertyNames = new();
            internal readonly List<CompiledConditionValuePart> _valueParts = new();
            private readonly Dictionary<string, int> _stringIds =
                new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _propertyIds =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly List<CompiledConditionInstruction> _instructions = new();
            private readonly List<CompiledConditionComparison> _comparisons = new();

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
                    _instructions.ToArray(),
                    _comparisons.ToArray(),
                    _valueParts.ToArray());

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

            private bool TryCompileOperand(
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
                if (value.Contains("@(", StringComparison.Ordinal) ||
                    value.Contains("%(", StringComparison.Ordinal))
                {
                    parts = default;
                    return false;
                }

                int partStart = _valueParts.Count;
                int propertyCount = _propertyNames.Count;
                int sourceIndex = 0;
                int propertyStart = value.IndexOf("$(", StringComparison.Ordinal);
                while (propertyStart >= 0)
                {
                    if (propertyStart > sourceIndex)
                    {
                        _valueParts.Add(
                            new CompiledConditionValuePart(
                                CompiledConditionValuePartKind.Literal,
                                GetStringId(value.Substring(
                                    sourceIndex,
                                    propertyStart - sourceIndex))));
                    }

                    int propertyEnd = value.IndexOf(')', propertyStart + 2);
                    if (propertyEnd < 0)
                    {
                        RollBackValue(partStart, propertyCount);
                        parts = default;
                        return false;
                    }

                    string propertyName = value.Substring(
                        propertyStart + 2,
                        propertyEnd - propertyStart - 2);
                    if (!CanCompileProperty(propertyName))
                    {
                        RollBackValue(partStart, propertyCount);
                        parts = default;
                        return false;
                    }

                    _valueParts.Add(
                        new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Property,
                            GetPropertyId(propertyName)));
                    sourceIndex = propertyEnd + 1;
                    propertyStart = value.IndexOf(
                        "$(",
                        sourceIndex,
                        StringComparison.Ordinal);
                }

                if (sourceIndex < value.Length || partStart == _valueParts.Count)
                {
                    _valueParts.Add(
                        new CompiledConditionValuePart(
                            CompiledConditionValuePartKind.Literal,
                            GetStringId(value.Substring(sourceIndex))));
                }

                parts = new TableRange(
                    partStart,
                    _valueParts.Count - partStart);
                return true;
            }

            private void RollBackValue(int partStart, int propertyCount)
            {
                _valueParts.RemoveRange(
                    partStart,
                    _valueParts.Count - partStart);
                if (_propertyNames.Count > propertyCount)
                {
                    _propertyNames.RemoveRange(
                        propertyCount,
                        _propertyNames.Count - propertyCount);
                    _propertyIds.Clear();
                    for (int i = 0; i < _propertyNames.Count; i++)
                    {
                        _propertyIds[_propertyNames[i]] = i;
                    }
                }
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
