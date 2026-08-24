// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        private abstract class LazyItemOperation : IItemOperation
        {
            private readonly string _itemType;
            private readonly ImmutableDictionary<string, LazyItemList> _referencedItemLists;

            protected readonly LazyItemEvaluator<P, I, M, D> _lazyEvaluator;
            protected readonly ProjectItemElement _itemElement;
            protected readonly ItemSpec<P, I> _itemSpec;
            protected readonly EvaluatorData _evaluatorData;
            protected readonly Expander<P, I> _expander;
            protected readonly bool _conditionResult;

            // This is used only when evaluating an expression, which instantiates
            //  the items and then removes them
            protected readonly IItemFactory<I, I> _itemFactory;
            internal ItemSpec<P, I> Spec => _itemSpec;

            protected LazyItemOperation(OperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
            {
                _itemElement = builder.ItemElement;
                _itemType = builder.ItemType;
                _itemSpec = builder.ItemSpec;
                _referencedItemLists = builder.ReferencedItemLists.ToImmutable();
                _conditionResult = builder.ConditionResult;

                _lazyEvaluator = lazyEvaluator;

                _evaluatorData = new EvaluatorData(
                    _lazyEvaluator._outerEvaluatorData,
                    _referencedItemLists,
                    _lazyEvaluator._moduleEvaluationReadTracker);
                _itemFactory = new ItemFactoryWrapper(_itemElement, _lazyEvaluator._itemFactory);
                _expander = new Expander<P, I>(_evaluatorData, _evaluatorData, _lazyEvaluator.EvaluationContext, _lazyEvaluator._loggingContext);

                _itemSpec.Expander = _expander;
            }

            protected FileMatcher FileMatcher => _lazyEvaluator.FileMatcher;

            public void Apply(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                MSBuildEventSource.Log.ApplyLazyItemOperationsStart(_itemElement.ItemType);
                EvaluationPerformanceInstrumentation.Scope
                    operationMeasurement = default;
                EvaluationPerformanceInstrumentation
                    .LazyItemOperationShapeScope shapeMeasurement =
                        default;
                if (EvaluationPerformanceInstrumentation.Enabled)
                {
                    EvaluationPerformanceMetric operationMetric =
                        this switch
                        {
                            IncludeOperation =>
                                EvaluationPerformanceMetric
                                    .LazyItemIncludeApplication,
                            RemoveOperation =>
                                EvaluationPerformanceMetric
                                    .LazyItemRemoveApplication,
                            UpdateOperation =>
                                EvaluationPerformanceMetric
                                    .LazyItemUpdateApplication,
                            _ => throw new InternalErrorException(
                                "Unknown lazy item operation."),
                        };
                    string operationExpression = operationMetric switch
                    {
                        EvaluationPerformanceMetric
                            .LazyItemIncludeApplication =>
                            _itemElement.Include,
                        EvaluationPerformanceMetric
                            .LazyItemRemoveApplication =>
                            _itemElement.Remove,
                        EvaluationPerformanceMetric
                            .LazyItemUpdateApplication =>
                            _itemElement.Update,
                        _ => string.Empty,
                    };
                    operationMeasurement =
                        EvaluationPerformanceInstrumentation.Measure(
                            operationMetric);
                    shapeMeasurement =
                        EvaluationPerformanceInstrumentation
                            .MeasureLazyItemOperationShape(
                                operationMetric,
                                _itemElement.ItemType,
                                operationExpression);
                }

                using (shapeMeasurement)
                using (operationMeasurement)
                using (EvaluationPerformanceInstrumentation.Measure(
                           EvaluationPerformanceMetric
                               .LazyItemOperationApplication))
                using (_lazyEvaluator._evaluationProfiler.TrackElement(_itemElement))
                using (ModuleEvaluationReadTracker.Scope readTrackingScope =
                           _lazyEvaluator._moduleEvaluationReadTracker.Track(
                               _itemElement,
                               "ItemOperationApplication",
                               _itemElement.ItemType))
                {
                    if (readTrackingScope is not null)
                    {
                        _lazyEvaluator._moduleEvaluationReadTracker.RecordItems<I, M>(
                            _itemElement.ItemType,
                            listBuilder.Select(itemData => itemData.Item));
                    }

                    ApplyImpl(listBuilder, globsToIgnore);
                }
                MSBuildEventSource.Log.ApplyLazyItemOperationsStop(_itemElement.ItemType);
            }

            protected virtual void ApplyImpl(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                var items = SelectItems(listBuilder, globsToIgnore);
                MutateItems(items);
                SaveItems(items, listBuilder);
            }

            /// <summary>
            /// Produce the items to operate on. For example, create new ones or select existing ones
            /// </summary>
            protected virtual ImmutableArray<I> SelectItems(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                return listBuilder.Select(itemData => itemData.Item)
                                  .ToImmutableArray();
            }

            // todo Refactoring: MutateItems should clone each item before mutation. See https://github.com/dotnet/msbuild/issues/2328
            protected virtual void MutateItems(ImmutableArray<I> items) { }

            protected virtual void SaveItems(ImmutableArray<I> items, OrderedItemDataCollection.Builder listBuilder) { }

            [DebuggerDisplay(@"{DebugString()}")]
            protected readonly struct ItemBatchingContext
            {
                public I OperationItem { get; }
                private Dictionary<string, I> CapturedItems { get; }

                public ItemBatchingContext(I operationItem, Dictionary<string, I> capturedItems = null)
                {
                    OperationItem = operationItem;

                    CapturedItems = capturedItems == null || capturedItems.Count == 0
                        ? null
                        : capturedItems;
                }

                public IMetadataTable GetMetadataTable()
                {
                    return CapturedItems == null
                        ? (IMetadataTable)OperationItem
                        : new ItemOperationMetadataTable(OperationItem, CapturedItems);
                }

                private string DebugString()
                {
                    var referencedItemsString = CapturedItems == null
                        ? "none"
                        : string.Join(";", CapturedItems.Select(kvp => $"{kvp.Key} : {kvp.Value.EvaluatedInclude}"));

                    return $"{OperationItem.Key} : {OperationItem.EvaluatedInclude}; CapturedItems: {referencedItemsString}";
                }
            }

            private class ItemOperationMetadataTable : IMetadataTable
            {
                private readonly I _operationItem;
                private readonly Dictionary<string, I> _capturedItems;

                public ItemOperationMetadataTable(I operationItem, Dictionary<string, I> capturedItems)
                {
                    Assumed.Equal(capturedItems.Comparer, StringComparer.OrdinalIgnoreCase, "MSBuild assumes case insensitive item name comparison");

                    _operationItem = operationItem;
                    _capturedItems = capturedItems;
                }

                public string GetEscapedValue(string name)
                {
                    return _operationItem.GetEscapedValue(name);
                }

                public string GetEscapedValue(string itemType, string name)
                {
                    return RouteCall(itemType, name, (t, it, n) => t.GetEscapedValue(it, n));
                }

                public string GetEscapedValueIfPresent(string itemType, string name)
                {
                    return RouteCall(itemType, name, (t, it, n) => t.GetEscapedValueIfPresent(it, n));
                }

                private string RouteCall(string itemType, string name, Func<IMetadataTable, string, string, string> getEscapedValueFunc)
                {
                    if (itemType?.Equals(_operationItem.Key, StringComparison.OrdinalIgnoreCase) != false)
                    {
                        return getEscapedValueFunc(_operationItem, itemType, name);
                    }
                    else if (_capturedItems.TryGetValue(itemType, out var item))
                    {
                        return getEscapedValueFunc(item, itemType, name);
                    }
                    else
                    {
                        return string.Empty;
                    }
                }
            }

            protected void DecorateItemsWithMetadata(
                IEnumerable<ItemBatchingContext> itemBatchingContexts,
                DeferredMetadata metadata,
                bool? needToExpandMetadata = null)
            {
                if (metadata.Count > 0)
                {
                    ////////////////////////////////////////////////////
                    // UNDONE: Implement batching here.
                    //
                    // We want to allow built-in metadata in metadata values here.
                    // For example, so that an Idl file can specify that its Tlb output should be named %(Filename).tlb.
                    //
                    // In other words, we want batching. However, we won't need to go to the trouble of using the regular batching code!
                    // That's because that code is all about grouping into buckets of similar items. In this context, we're not
                    // invoking a task, and it's fine to process each item individually, which will always give the correct results.
                    //
                    // For the CTP, to make the minimal change, we will not do this quite correctly.
                    //
                    // We will do this:
                    // -- check whether any metadata values or their conditions contain any bare built-in metadata expressions,
                    //    or whether they contain any custom metadata && the Include involved an @(itemlist) expression.
                    // -- if either case is found, we go ahead and evaluate all the metadata separately for each item.
                    // -- otherwise we can do the old thing (evaluating all metadata once then applying to all items)
                    //
                    // This algorithm gives the correct results except when:
                    // -- batchable expressions exist on the include, exclude, or condition on the item element itself
                    //
                    // It means that 99% of cases still go through the old code, which is best for the CTP.
                    // When we ultimately implement this correctly, we should make sure we optimize for the case of very many items
                    // and little metadata, none of which varies between items.

                    // Do not expand properties as they have been already expanded by the lazy evaluator upon item operation construction.
                    // Prior to lazy evaluation ExpanderOptions.ExpandAll was used.
                    const ExpanderOptions metadataExpansionOptions = ExpanderOptions.ExpandAll;

                    needToExpandMetadata ??= NeedToExpandMetadataForEachItem(metadata, out _);

                    if (needToExpandMetadata.Value)
                    {
                        foreach (var itemContext in itemBatchingContexts)
                        {
                            _expander.Metadata = itemContext.GetMetadataTable();

                            for (int i = 0; i < metadata.Count; i++)
                            {
                                ProjectMetadataElement metadataElement =
                                    metadata.GetElement(i);
                                if (!EvaluateMetadataCondition(
                                        metadata,
                                        i,
                                        metadataElement,
                                        metadataExpansionOptions,
                                        ParserOptions.AllowAll))
                                {
                                    continue;
                                }

                                string evaluatedValue =
                                    _expander.ExpandIntoStringLeaveEscaped(
                                        metadata.GetValue(i),
                                        metadataExpansionOptions,
                                        metadataElement.Location);

                                itemContext.OperationItem.SetMetadata(
                                    metadataElement,
                                    FileUtilities.MaybeAdjustFilePath(
                                        evaluatedValue,
                                        metadata.GetDirectory(i)));
                            }
                        }

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }
                    // End of pseudo batching
                    ////////////////////////////////////////////////////
                    // Start of old code
                    else
                    {
                        // Metadata expressions are allowed here.
                        // Temporarily gather and expand these in a table so they can reference other metadata elements above.
                        EvaluatorMetadataTable metadataTable =
                            new EvaluatorMetadataTable(
                                _itemType,
                                capacity: metadata.Count);
                        _expander.Metadata = metadataTable;

                        // Also keep a list of everything so we can get the predecessor objects correct.
                        List<KeyValuePair<ProjectMetadataElement, string>>
                            metadataList = new(metadata.Count);

                        for (int i = 0; i < metadata.Count; i++)
                        {
                            ProjectMetadataElement metadataElement =
                                metadata.GetElement(i);
                            // Because of the checking above, it should be safe to expand metadata in conditions; the condition
                            // will be true for either all the items or none
                            if (!EvaluateMetadataCondition(
                                    metadata,
                                    i,
                                    metadataElement,
                                    metadataExpansionOptions,
                                    ParserOptions.AllowAll))
                            {
                                continue;
                            }

                            string evaluatedValue =
                                _expander.ExpandIntoStringLeaveEscaped(
                                    metadata.GetValue(i),
                                    metadataExpansionOptions,
                                    metadataElement.Location);
                            evaluatedValue = FileUtilities.MaybeAdjustFilePath(
                                evaluatedValue,
                                metadata.GetDirectory(i));

                            metadataTable.SetValue(metadataElement, evaluatedValue);
                            metadataList.Add(new KeyValuePair<ProjectMetadataElement, string>(metadataElement, evaluatedValue));
                        }

                        // Apply those metadata to each item
                        // Note that several items could share the same metadata objects

                        // Set all the items at once to make a potential copy-on-write optimization possible.
                        // This is valuable in the case where one item element evaluates to
                        // many items (either by semicolon or wildcards)
                        // and that item also has the same piece/s of metadata for each item.
                        _itemFactory.SetMetadata(metadataList, itemBatchingContexts.Select(i => i.OperationItem));

                        // End of legal area for metadata expressions.
                        _expander.Metadata = null;
                    }
                }
            }

            private bool EvaluateMetadataCondition(
                DeferredMetadata metadata,
                int index,
                ProjectMetadataElement metadataElement,
                ExpanderOptions expanderOptions,
                ParserOptions parserOptions)
            {
                int compiledConditionId =
                    metadata.GetCompiledConditionId(index);
                if (_lazyEvaluator.EvaluationContext
                        .UseCompiledModuleEffectBatches &&
                    compiledConditionId >= 0)
                {
                    return compiledConditionId == 0 ||
                        EvaluateCompiledMetadataCondition(
                            metadata.Module,
                            compiledConditionId);
                }

                return EvaluateCondition(
                    metadata.GetCondition(index),
                    metadataElement,
                    expanderOptions,
                    parserOptions,
                    _expander,
                    _lazyEvaluator);
            }

            private bool EvaluateCompiledMetadataCondition(
                EvaluationModule module,
                int conditionId)
            {
                using var conditionMeasurement =
                    EvaluationPerformanceInstrumentation.Measure(
                        EvaluationPerformanceMetric.ConditionEvaluation);
                using var compiledMeasurement =
                    EvaluationPerformanceInstrumentation.Measure(
                        EvaluationPerformanceMetric
                            .CompiledConditionEvaluation);
                CompiledCondition condition =
                    module.CompiledConditions[conditionId];
                ProjectElement source =
                    module.GetSource(condition.SourceId);
                if (EvaluationPerformanceInstrumentation.Enabled)
                {
                    EvaluationPerformanceInstrumentation
                        .RecordConditionShape(
                            "Compiled",
                            source.Condition);
                }

                IElementLocation location = source.ConditionLocation;
                TableRange instructions = condition.Instructions;
                int instructionIndex = instructions.Start;
                while (true)
                {
                    CompiledConditionInstruction instruction =
                        module.CompiledConditionInstructions[
                            instructionIndex];
                    switch (instruction.Kind)
                    {
                        case CompiledConditionInstructionKind
                            .BranchIfComparisonFalse:
                            if (!EvaluateCompiledMetadataComparison(
                                    module,
                                    instruction.Argument0,
                                    location))
                            {
                                instructionIndex +=
                                    instruction.Argument1;
                            }
                            else
                            {
                                instructionIndex++;
                            }

                            break;
                        case CompiledConditionInstructionKind
                            .BranchIfComparisonTrue:
                            if (EvaluateCompiledMetadataComparison(
                                    module,
                                    instruction.Argument0,
                                    location))
                            {
                                instructionIndex +=
                                    instruction.Argument1;
                            }
                            else
                            {
                                instructionIndex++;
                            }

                            break;
                        case CompiledConditionInstructionKind
                            .ReturnComparison:
                            return EvaluateCompiledMetadataComparison(
                                module,
                                instruction.Argument0,
                                location);
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

            private bool EvaluateCompiledMetadataComparison(
                EvaluationModule module,
                int comparisonId,
                IElementLocation location)
            {
                CompiledConditionComparison comparison =
                    module.CompiledConditionComparisons[comparisonId];
                string left = EvaluateCompiledMetadataOperand(
                    module,
                    comparison.Left,
                    location);
                string right = EvaluateCompiledMetadataOperand(
                    module,
                    comparison.Right,
                    location);
                bool equal =
                    Evaluator<P, I, M, D>
                        .CompareCompiledConditionValues(
                            left,
                            right,
                            out _);
                return comparison.Kind == CompiledConditionKind.Equal
                    ? equal
                    : !equal;
            }

            private string EvaluateCompiledMetadataOperand(
                EvaluationModule module,
                CompiledConditionOperand operand,
                IElementLocation location)
            {
                return operand.Kind switch
                {
                    CompiledConditionOperandKind.Literal =>
                        module.GetStringValue(operand.Value),
                    CompiledConditionOperandKind.Property =>
                        EvaluateCompiledMetadataProperty(
                            module,
                            operand.Value,
                            location,
                            unescape: true),
                    CompiledConditionOperandKind.ExpandedValue =>
                        EvaluateCompiledMetadataExpandedValue(
                            module,
                            operand.Value,
                            operand.Count,
                            location),
                    CompiledConditionOperandKind.Metadata =>
                        EvaluateCompiledMetadataValue(
                            module,
                            operand),
                    _ => throw new InternalErrorException(
                        "Unknown compiled condition operand."),
                };
            }

            private string EvaluateCompiledMetadataProperty(
                EvaluationModule module,
                int readIndex,
                IElementLocation location,
                bool unescape)
            {
                CompiledPropertyExternalRead read =
                    module.CompiledConditionPropertyReads[readIndex];
                if (!_evaluatorData.TryGetEscapedPropertyValue(
                        read.PropertyId,
                        module.GetStringValue(read.NameStringId),
                        location,
                        out string escapedValue))
                {
                    return string.Empty;
                }

                return unescape
                    ? FileUtilities.MaybeAdjustFilePath(
                        EscapingUtilities.UnescapeAll(escapedValue))
                    : escapedValue;
            }

            private string EvaluateCompiledMetadataExpandedValue(
                EvaluationModule module,
                int firstPart,
                int partCount,
                IElementLocation location)
            {
                string expanded;
                if (partCount == 1)
                {
                    expanded = EvaluateCompiledMetadataValuePart(
                        module,
                        firstPart,
                        location);
                }
                else if (partCount == 2)
                {
                    expanded = string.Concat(
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 1,
                            location));
                }
                else if (partCount == 3)
                {
                    expanded = string.Concat(
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 1,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 2,
                            location));
                }
                else if (partCount == 4)
                {
                    expanded = string.Concat(
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 1,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 2,
                            location),
                        EvaluateCompiledMetadataValuePart(
                            module,
                            firstPart + 3,
                            location));
                }
                else
                {
                    var builder = new StringBuilder();
                    for (int partIndex = firstPart;
                         partIndex < firstPart + partCount;
                         partIndex++)
                    {
                        builder.Append(
                            EvaluateCompiledMetadataValuePart(
                                module,
                                partIndex,
                                location));
                    }

                    expanded = builder.ToString();
                }

                return FileUtilities.MaybeAdjustFilePath(
                    EscapingUtilities.UnescapeAll(expanded));
            }

            private string EvaluateCompiledMetadataValuePart(
                EvaluationModule module,
                int partIndex,
                IElementLocation location)
            {
                CompiledConditionValuePart part =
                    module.CompiledConditionValueParts[partIndex];
                return part.Kind switch
                {
                    CompiledConditionValuePartKind.Literal =>
                        module.GetStringValue(part.Value),
                    CompiledConditionValuePartKind.Property =>
                        EvaluateCompiledMetadataProperty(
                            module,
                            part.Value,
                            location,
                            unescape: false),
                    _ => throw new InternalErrorException(
                        "Unknown compiled condition value part."),
                };
            }

            private string EvaluateCompiledMetadataValue(
                EvaluationModule module,
                CompiledConditionOperand operand)
            {
                string itemType =
                    module.GetStringValue(operand.Value);
                string metadataName =
                    module.GetStringValue(operand.Count);
                string escapedValue = itemType.Length == 0
                    ? _expander.Metadata.GetEscapedValue(metadataName)
                    : _expander.Metadata.GetEscapedValue(
                        itemType,
                        metadataName);
                return FileUtilities.MaybeAdjustFilePath(
                    EscapingUtilities.UnescapeAll(escapedValue));
            }

            protected bool NeedToExpandMetadataForEachItem(
                DeferredMetadata metadata,
                out ItemsAndMetadataPair itemsAndMetadataFound)
            {
                itemsAndMetadataFound = new ItemsAndMetadataPair(null, null);

                for (int i = 0; i < metadata.Count; i++)
                {
                    string expression = metadata.GetValue(i);
                    ExpressionShredder.GetReferencedItemNamesAndMetadata(expression, 0, expression.Length, ref itemsAndMetadataFound, ShredderOptions.All);

                    expression = metadata.GetCondition(i);
                    ExpressionShredder.GetReferencedItemNamesAndMetadata(expression, 0, expression.Length, ref itemsAndMetadataFound, ShredderOptions.All);
                }

                bool needToExpandMetadataForEachItem = false;

                if (itemsAndMetadataFound.Metadata?.Values.Count > 0)
                {
                    // If there is any metadata present, we need to expand items individually.
                    // This ensures correct results for:
                    // - Built-in metadata expressions (like %(FileName)) which vary between items
                    // - Custom metadata when item list references are involved
                    needToExpandMetadataForEachItem = true;
                }

                return needToExpandMetadataForEachItem;
            }

            /// <summary>
            /// Is this spec a single reference to a specific item?
            /// </summary>
            /// <returns>True if the item is a simple reference to the referenced item type.</returns>
            protected static bool ItemspecContainsASingleBareItemReference(ItemSpec<P, I> itemSpec, string referencedItemType)
            {
                if (itemSpec.Fragments.Count != 1)
                {
                    return false;
                }

                var itemExpressionFragment = itemSpec.Fragments[0] as ItemSpec<P, I>.ItemExpressionFragment;
                if (itemExpressionFragment == null)
                {
                    return false;
                }

                if (!itemExpressionFragment.Capture.ItemType.Equals(referencedItemType, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // If the itemSpec is a single call to an item function, like @(X->Something(...)), it may get this
                // far, but shouldn't be treated as a single reference: the item function may return entirely
                // different results from a bare reference like @(X).
                if (itemExpressionFragment.Capture.Captures is object)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
