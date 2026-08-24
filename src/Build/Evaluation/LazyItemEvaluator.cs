// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
#if DEBUG
using System.Diagnostics;
#endif
using System.Linq;
using System.Threading;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
        where P : class, IProperty, IEquatable<P>, IValued
        where I : class, IItem<M>, IMetadataTable
        where M : class, IMetadatum
        where D : class, IItemDefinition<M>
    {
        private readonly IEvaluatorData<P, I, M, D> _outerEvaluatorData;
        private readonly Expander<P, I> _outerExpander;
        private readonly IEvaluatorData<P, I, M, D> _evaluatorData;
        private readonly Expander<P, I> _expander;
        private readonly IItemFactory<I, I> _itemFactory;
        private readonly LoggingContext _loggingContext;
        private readonly EvaluationProfiler _evaluationProfiler;
        private readonly ModuleEvaluationReadTracker _moduleEvaluationReadTracker;

        private int _nextElementOrder = 0;

        private Dictionary<string, LazyItemList> _itemLists = Traits.Instance.EscapeHatches.UseCaseSensitiveItemNames ?
            new Dictionary<string, LazyItemList>() :
            new Dictionary<string, LazyItemList>(StringComparer.OrdinalIgnoreCase);

        protected EvaluationContext EvaluationContext { get; }

        protected IFileSystem FileSystem => EvaluationContext.FileSystem;

        protected FileMatcher FileMatcher => EvaluationContext.FileMatcher;

        public LazyItemEvaluator(
            IEvaluatorData<P, I, M, D> data,
            IItemFactory<I, I> itemFactory,
            LoggingContext loggingContext,
            EvaluationProfiler evaluationProfiler,
            EvaluationContext evaluationContext,
            ModuleEvaluationReadTracker moduleEvaluationReadTracker)
        {
            _outerEvaluatorData = data;
            _outerExpander = new Expander<P, I>(_outerEvaluatorData, _outerEvaluatorData, evaluationContext, loggingContext);
            _moduleEvaluationReadTracker = moduleEvaluationReadTracker;
            _evaluatorData = new EvaluatorData(
                _outerEvaluatorData,
                _itemLists,
                _moduleEvaluationReadTracker);
            _expander = new Expander<P, I>(_evaluatorData, _evaluatorData, evaluationContext, loggingContext);
            _itemFactory = itemFactory;
            _loggingContext = loggingContext;
            _evaluationProfiler = evaluationProfiler;

            EvaluationContext = evaluationContext;
        }

        public bool EvaluateConditionWithCurrentState(ProjectElement element, ExpanderOptions expanderOptions, ParserOptions parserOptions)
        {
            return EvaluateCondition(element.Condition, element, expanderOptions, parserOptions, _expander, this);
        }

        internal bool EvaluateConditionWithCurrentState(
            string condition,
            ProjectElement element,
            ExpanderOptions expanderOptions,
            ParserOptions parserOptions)
        {
            return EvaluateCondition(
                condition,
                element,
                expanderOptions,
                parserOptions,
                _expander,
                this);
        }

        private static bool EvaluateCondition(
            string condition,
            ProjectElement element,
            ExpanderOptions expanderOptions,
            ParserOptions parserOptions,
            Expander<P, I> expander,
            LazyItemEvaluator<P, I, M, D> lazyEvaluator)
        {
            if (condition?.Length == 0)
            {
                return true;
            }
            MSBuildEventSource.Log.EvaluateConditionStart(condition);
            if (EvaluationPerformanceInstrumentation.Enabled)
            {
                EvaluationPerformanceInstrumentation
                    .RecordConditionContext(
                        element.GetType().Name,
                        condition);
            }

            using (EvaluationPerformanceInstrumentation.Measure(
                       EvaluationPerformanceMetric.ConditionEvaluation))
            using (lazyEvaluator._evaluationProfiler.TrackCondition(element.ConditionLocation, condition))
            {
                bool result = ConditionEvaluator.EvaluateCondition(
                    condition,
                    parserOptions,
                    expander,
                    expanderOptions,
                    GetCurrentDirectoryForConditionEvaluation(element, lazyEvaluator),
                    element.ConditionLocation,
                    lazyEvaluator.FileSystem,
                    loggingContext: lazyEvaluator._loggingContext);
                MSBuildEventSource.Log.EvaluateConditionStop(condition, result);

                return result;
            }
        }

        /// <summary>
        /// COMPAT: Whidbey used the "current project file/targets" directory for evaluating Import and PropertyGroup conditions
        /// Orcas broke this by using the current root project file for all conditions
        /// For Dev10+, we'll fix this, and use the current project file/targets directory for Import, ImportGroup and PropertyGroup
        /// but the root project file for the rest. Inside of targets will use the root project file as always.
        /// </summary>
        private static string GetCurrentDirectoryForConditionEvaluation(ProjectElement element, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
        {
            if (element is ProjectPropertyGroupElement || element is ProjectImportElement || element is ProjectImportGroupElement)
            {
                return element.ContainingProject.DirectoryPath;
            }
            else
            {
                return lazyEvaluator._outerEvaluatorData.Directory;
            }
        }

        public struct ItemData
        {
            public ItemData(I item, ProjectItemElement originatingItemElement, int elementOrder, bool conditionResult, string normalizedItemValue = null)
            {
                Item = item;
                OriginatingItemElement = originatingItemElement;
                ElementOrder = elementOrder;
                ConditionResult = conditionResult;
                _normalizedItemValue = normalizedItemValue;
            }

            public readonly ItemData Clone(IItemFactory<I, I> itemFactory, ProjectItemElement initialItemElementForFactory)
            {
                // setting the factory's item element to the original item element that produced the item
                // otherwise you get weird things like items that appear to have been produced by update elements
                itemFactory.ItemElement = OriginatingItemElement;
                var clonedItem = itemFactory.CreateItem(Item, OriginatingItemElement.ContainingProject.FullPath);
                itemFactory.ItemElement = initialItemElementForFactory;

                return new ItemData(clonedItem, OriginatingItemElement, ElementOrder, ConditionResult, _normalizedItemValue);
            }

            public I Item { get; }
            public ProjectItemElement OriginatingItemElement { get; }
            public int ElementOrder { get; }
            public bool ConditionResult { get; }

            /// <summary>
            /// Lazily created normalized item value.
            /// </summary>
            private string _normalizedItemValue;
            public string NormalizedItemValue
            {
                get
                {
                    var normalizedItemValue = Volatile.Read(ref _normalizedItemValue);
                    if (normalizedItemValue == null)
                    {
                        normalizedItemValue = FileUtilities.NormalizePathForComparisonNoThrow(Item.EvaluatedInclude, Item.ProjectDirectory);
                        Volatile.Write(ref _normalizedItemValue, normalizedItemValue);
                    }
                    return normalizedItemValue;
                }
            }
        }

        private class MemoizedOperation : IItemOperation
        {
            public LazyItemOperation Operation { get; }
            private Dictionary<ISet<string>, OrderedItemDataCollection> _cache;

            private bool _isReferenced;
#if DEBUG
            private int _applyCalls;
#endif

            public MemoizedOperation(LazyItemOperation operation)
            {
                Operation = operation;
            }

            public void Apply(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
#if DEBUG
                CheckInvariant();
#endif

                Operation.Apply(listBuilder, globsToIgnore);

                // cache results if somebody is referencing this operation
                if (_isReferenced)
                {
                    AddItemsToCache(globsToIgnore, listBuilder.ToImmutable());
                }
#if DEBUG
                _applyCalls++;
                CheckInvariant();
#endif
            }

#if DEBUG
            private void CheckInvariant()
            {
                if (_isReferenced)
                {
                    var cacheCount = _cache?.Count ?? 0;
                    Debug.Assert(_applyCalls == cacheCount, "Apply should only be called once per globsToIgnore. Otherwise caching is not working");
                }
                else
                {
                    // non referenced operations should not be cached
                    // non referenced operations should have as many apply calls as the number of cache keys of the immediate dominator with _isReferenced == true
                    Debug.Assert(_cache == null);
                }
            }
#endif

            public bool TryGetFromCache(ISet<string> globsToIgnore, out OrderedItemDataCollection items)
            {
                if (_cache != null)
                {
                    return _cache.TryGetValue(globsToIgnore, out items);
                }

                items = null;
                return false;
            }

            /// <summary>
            /// Somebody is referencing this operation
            /// </summary>
            public void MarkAsReferenced()
            {
                _isReferenced = true;
            }

            private void AddItemsToCache(ImmutableHashSet<string> globsToIgnore, OrderedItemDataCollection items)
            {
                if (_cache == null)
                {
                    _cache = new Dictionary<ISet<string>, OrderedItemDataCollection>();
                }

                _cache[globsToIgnore] = items;
            }
        }

        private class LazyItemList
        {
            private readonly LazyItemList _previous;
            private readonly MemoizedOperation _memoizedOperation;

            public LazyItemList(LazyItemList previous, LazyItemOperation operation)
            {
                _previous = previous;
                _memoizedOperation = new MemoizedOperation(operation);
            }

            public ImmutableList<I> GetMatchedItems(ImmutableHashSet<string> globsToIgnore)
            {
                ImmutableList<I>.Builder items = ImmutableList.CreateBuilder<I>();
                foreach (ItemData data in GetItemData(globsToIgnore))
                {
                    if (data.ConditionResult)
                    {
                        items.Add(data.Item);
                    }
                }

                return items.ToImmutable();
            }

            public OrderedItemDataCollection.Builder GetItemData(ImmutableHashSet<string> globsToIgnore)
            {
                // Cache results only on the LazyItemOperations whose results are required by an external caller (via GetItems). This means:
                //   - Callers of GetItems who have announced ahead of time that they would reference an operation (via MarkAsReferenced())
                // This includes: item references (Include="@(foo)") and metadata conditions (Condition="@(foo->Count()) == 0")
                // Without ahead of time notifications more computation is done than needed when the results of a future operation are requested
                // The future operation is part of another item list referencing this one (making this operation part of the tail).
                // The future operation will compute this list but since no ahead of time notifications have been made by callers, it won't cache the
                // intermediary operations that would be requested by those callers.
                //   - Callers of GetItems that cannot announce ahead of time. This includes item referencing conditions on
                // Item Groups and Item Elements. However, those conditions are performed eagerly outside of the LazyItemEvaluator, so they will run before
                // any item referencing operations from inside the LazyItemEvaluator. This
                //
                // If the head of this LazyItemList is uncached, then the tail may contain cached and un-cached nodes.
                // In this case we have to compute the head plus the part of the tail up to the first cached operation.
                //
                // The cache is based on a couple of properties:
                // - uses immutable lists for structural sharing between multiple cached nodes (multiple include operations won't have duplicated memory for the common items)
                // - if an operation is cached for a certain set of globsToIgnore, then the entire operation tail can be reused. This is because (i) the structure of LazyItemLists
                // does not mutate: one can add operations on top, but the base never changes, and (ii) the globsToIgnore passed to the tail is the concatenation between
                // the globsToIgnore received as an arg, and the globsToIgnore produced by the head (if the head is a Remove operation)

                OrderedItemDataCollection items;
                if (_memoizedOperation.TryGetFromCache(globsToIgnore, out items))
                {
                    return items.ToBuilder();
                }
                else
                {
                    // tell the cache that this operation's result is needed by an external caller
                    // this is required for callers that cannot tell the item list ahead of time that
                    // they would be using an operation
                    MarkAsReferenced();

                    return ComputeItems(this, globsToIgnore);
                }
            }

            /// <summary>
            /// Applies uncached item operations (include, remove, update) in order. Since Remove effectively overwrites Include or Update,
            /// Remove operations are preprocessed (adding to globsToIgnore) to create a longer list of globs we don't need to process
            /// properly because we know they will be removed. Update operations are batched as much as possible, meaning rather
            /// than being applied immediately, they are combined into a dictionary of UpdateOperations that need to be applied. This
            /// is to optimize the case in which as series of UpdateOperations, each of which affects a single ItemSpec, are applied to all
            /// items in the list, leading to a quadratic-time operation.
            /// </summary>
            private static OrderedItemDataCollection.Builder ComputeItems(LazyItemList lazyItemList, ImmutableHashSet<string> globsToIgnore)
            {
                // Stack of operations up to the first one that's cached (exclusive)
                Stack<LazyItemList> itemListStack = new Stack<LazyItemList>();

                OrderedItemDataCollection.Builder items = null;

                // Keep a separate stack of lists of globs to ignore that only gets modified for Remove operations
                Stack<ImmutableHashSet<string>> globsToIgnoreStack = null;

                for (var currentList = lazyItemList; currentList != null; currentList = currentList._previous)
                {
                    var globsToIgnoreFromFutureOperations = globsToIgnoreStack?.Peek() ?? globsToIgnore;

                    OrderedItemDataCollection itemsFromCache;
                    if (currentList._memoizedOperation.TryGetFromCache(globsToIgnoreFromFutureOperations, out itemsFromCache))
                    {
                        // the base items on top of which to apply the uncached operations are the items of the first operation that is cached
                        items = itemsFromCache.ToBuilder();
                        break;
                    }

                    // If this is a remove operation, then add any globs that will be removed
                    //  to a list of globs to ignore in previous operations
                    if (currentList._memoizedOperation.Operation is RemoveOperation removeOperation)
                    {
                        globsToIgnoreStack ??= new Stack<ImmutableHashSet<string>>();

                        var globsToIgnoreForPreviousOperations = removeOperation.GetRemovedGlobs();
                        foreach (var globToRemove in globsToIgnoreFromFutureOperations)
                        {
                            globsToIgnoreForPreviousOperations.Add(globToRemove);
                        }

                        globsToIgnoreStack.Push(globsToIgnoreForPreviousOperations.ToImmutable());
                    }

                    itemListStack.Push(currentList);
                }

                if (items == null)
                {
                    items = OrderedItemDataCollection.CreateBuilder();
                }

                ImmutableHashSet<string> currentGlobsToIgnore = globsToIgnoreStack == null ? globsToIgnore : globsToIgnoreStack.Peek();

                Dictionary<string, UpdateOperation> itemsWithNoWildcards = new Dictionary<string, UpdateOperation>(StringComparer.OrdinalIgnoreCase);
                bool addedToBatch = false;

                // Walk back down the stack of item lists applying operations
                while (itemListStack.Count > 0)
                {
                    var currentList = itemListStack.Pop();

                    if (currentList._memoizedOperation.Operation is UpdateOperation op)
                    {
                        bool addToBatch = true;
                        int i;
                        // The TextFragments are things like abc.def or x*y.*z.
                        for (i = 0; i < op.Spec.Fragments.Count; i++)
                        {
                            ItemSpecFragment frag = op.Spec.Fragments[i];
                            if (MSBuildConstants.CharactersForExpansion.Any(frag.TextFragment.Contains))
                            {
                                // Fragment contains wild cards, items, or properties. Cannot batch over it using a dictionary.
                                addToBatch = false;
                                break;
                            }

                            string fullPath = FileUtilities.NormalizePathForComparisonNoThrow(frag.TextFragment, frag.ProjectDirectory);
                            if (itemsWithNoWildcards.ContainsKey(fullPath))
                            {
                                // Another update will already happen on this path. Make that happen before evaluating this one.
                                addToBatch = false;
                                break;
                            }
                            else
                            {
                                itemsWithNoWildcards.Add(fullPath, op);
                            }
                        }
                        if (!addToBatch)
                        {
                            // We found a wildcard. Remove any fragments associated with the current operation and process them later.
                            for (int j = 0; j < i; j++)
                            {
                                itemsWithNoWildcards.Remove(currentList._memoizedOperation.Operation.Spec.Fragments[j].TextFragment);
                            }
                        }
                        else
                        {
                            addedToBatch = true;
                            continue;
                        }
                    }

                    if (addedToBatch)
                    {
                        addedToBatch = false;
                        ProcessNonWildCardItemUpdates(itemsWithNoWildcards, items);
                    }

                    // If this is a remove operation, then it could modify the globs to ignore, so pop the potentially
                    //  modified entry off the stack of globs to ignore
                    if (currentList._memoizedOperation.Operation is RemoveOperation)
                    {
                        globsToIgnoreStack.Pop();
                        currentGlobsToIgnore = globsToIgnoreStack.Count == 0 ? globsToIgnore : globsToIgnoreStack.Peek();
                    }

                    currentList._memoizedOperation.Apply(items, currentGlobsToIgnore);
                }

                // We finished looping through the operations. Now process the final batch if necessary.
                ProcessNonWildCardItemUpdates(itemsWithNoWildcards, items);

                return items;
            }

            private static void ProcessNonWildCardItemUpdates(Dictionary<string, UpdateOperation> itemsWithNoWildcards, OrderedItemDataCollection.Builder items)
            {
                if (itemsWithNoWildcards.Count > 0)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        string fullPath = FileUtilities.NormalizePathForComparisonNoThrow(items[i].Item.EvaluatedInclude, items[i].Item.ProjectDirectory);
                        if (itemsWithNoWildcards.TryGetValue(fullPath, out UpdateOperation op))
                        {
                            items[i] = op.UpdateItem(items[i]);
                        }
                    }
                    itemsWithNoWildcards.Clear();
                }
            }

            public void MarkAsReferenced()
            {
                _memoizedOperation.MarkAsReferenced();
            }
        }

        private class OperationBuilder
        {
            // WORKAROUND: Unnecessary boxed allocation: https://github.com/dotnet/corefx/issues/24563
            private static readonly ImmutableDictionary<string, LazyItemList> s_emptyIgnoreCase = ImmutableDictionary.Create<string, LazyItemList>(StringComparer.OrdinalIgnoreCase);

            public ProjectItemElement ItemElement { get; set; }
            public string ItemType { get; set; }
            public ItemSpec<P, I> ItemSpec { get; set; }

            public ImmutableDictionary<string, LazyItemList>.Builder ReferencedItemLists { get; } = Traits.Instance.EscapeHatches.UseCaseSensitiveItemNames ?
                ImmutableDictionary.CreateBuilder<string, LazyItemList>() :
                s_emptyIgnoreCase.ToBuilder();

            public bool ConditionResult { get; set; }

            public OperationBuilder(
                ProjectItemElement itemElement,
                string itemType,
                bool conditionResult)
            {
                ItemElement = itemElement;
                ItemType = itemType;
                ConditionResult = conditionResult;
            }
        }

        private class OperationBuilderWithMetadata : OperationBuilder
        {
            public readonly ImmutableArray<ProjectMetadataElement>.Builder Metadata = ImmutableArray.CreateBuilder<ProjectMetadataElement>();
            private EvaluationModule _metadataModule;
            private TableRange _metadataRange;

            public OperationBuilderWithMetadata(
                ProjectItemElement itemElement,
                string itemType,
                bool conditionResult)
                : base(itemElement, itemType, conditionResult)
            {
            }

            public void SetLoweredMetadata(
                EvaluationModule module,
                TableRange metadata)
            {
                _metadataModule = module;
                _metadataRange = metadata;
            }

            public DeferredMetadata ToMetadata() =>
                _metadataModule is null
                    ? new DeferredMetadata(Metadata.ToImmutable())
                    : new DeferredMetadata(_metadataModule, _metadataRange);
        }

        private readonly struct ItemOperationData
        {
            internal ItemOperationData(ProjectItemElement element)
            {
                Element = element;
                ItemType = element.ItemType;
                OperationKind =
                    element.IncludeLocation is not null
                        ? ItemOperationKind.Include
                        : element.RemoveLocation is not null
                            ? ItemOperationKind.Remove
                            : ItemOperationKind.Update;
                Include = element.Include;
                Exclude = element.Exclude;
                Remove = element.Remove;
                Update = element.Update;
                MatchOnMetadata = element.MatchOnMetadata;
                MatchOnMetadataOptions = element.MatchOnMetadataOptions;
                Module = null;
                Metadata = default;
            }

            internal ItemOperationData(
                EvaluationModule module,
                ItemTemplate template,
                ProjectItemElement element)
            {
                Element = element;
                ItemType =
                    module.GetStringValue(template.ItemTypeStringId);
                OperationKind = template.OperationKind;
                Include =
                    module.GetExpressionValue(template.IncludeExpressionId);
                Exclude =
                    module.GetExpressionValue(template.ExcludeExpressionId);
                Remove =
                    module.GetExpressionValue(template.RemoveExpressionId);
                Update =
                    module.GetExpressionValue(template.UpdateExpressionId);
                MatchOnMetadata =
                    module.GetExpressionValue(
                        template.MatchOnMetadataExpressionId);
                MatchOnMetadataOptions =
                    module.GetStringValue(
                        template.MatchOnMetadataOptionsStringId);
                Module = module;
                Metadata = template.Metadata;
            }

            internal ProjectItemElement Element { get; }

            internal string ItemType { get; }

            internal ItemOperationKind OperationKind { get; }

            internal string Include { get; }

            internal string Exclude { get; }

            internal string Remove { get; }

            internal string Update { get; }

            internal string MatchOnMetadata { get; }

            internal string MatchOnMetadataOptions { get; }

            internal EvaluationModule Module { get; }

            internal TableRange Metadata { get; }
        }

        private readonly struct DeferredMetadata
        {
            private readonly ImmutableArray<ProjectMetadataElement> _elements;
            private readonly EvaluationModule _module;
            private readonly TableRange _range;

            internal DeferredMetadata(
                ImmutableArray<ProjectMetadataElement> elements)
            {
                _elements = elements;
                _module = null;
                _range = default;
            }

            internal DeferredMetadata(
                EvaluationModule module,
                TableRange range)
            {
                _elements = default;
                _module = module;
                _range = range;
            }

            internal int Count =>
                _module is null ? _elements.Length : _range.Count;

            internal ProjectMetadataElement GetElement(int index)
            {
                if (_module is null)
                {
                    return _elements[index];
                }

                return (ProjectMetadataElement)_module.GetSource(
                    GetTemplate(index).SourceId);
            }

            internal string GetCondition(int index) =>
                _module is null
                    ? _elements[index].Condition
                    : _module.GetConditionValue(
                        GetTemplate(index).ConditionId);

            internal int GetCompiledConditionId(int index) =>
                _module is null
                    ? -1
                    : GetTemplate(index).CompiledConditionId;

            internal EvaluationModule Module => _module;

            internal string GetValue(int index) =>
                _module is null
                    ? _elements[index].Value
                    : _module.GetExpressionValue(
                        GetTemplate(index).ValueExpressionId);

            internal string GetDirectory(int index) =>
                _module is null
                    ? _elements[index].ContainingProject.DirectoryPath
                    : _module.Header.DirectoryPath;

            private MetadataTemplate GetTemplate(int index) =>
                _module.Metadata[_range.Start + index];
        }

        private void AddReferencedItemList(string itemType, IDictionary<string, LazyItemList> referencedItemLists)
        {
            if (_itemLists.TryGetValue(itemType, out LazyItemList itemList))
            {
                itemList.MarkAsReferenced();
                referencedItemLists[itemType] = itemList;
            }
        }

        public IEnumerable<ItemData> GetAllItemsDeferred()
        {
            return _itemLists.Values.SelectMany(itemList => itemList.GetItemData(ImmutableHashSet<string>.Empty))
                                    .OrderBy(itemData => itemData.ElementOrder);
        }

        public void ProcessItemElement(string rootDirectory, ProjectItemElement itemElement, bool conditionResult)
        {
            ProcessItemElement(
                rootDirectory,
                new ItemOperationData(itemElement),
                conditionResult);
        }

        public void ProcessItemElement(
            string rootDirectory,
            EvaluationModule module,
            ItemTemplate template,
            ProjectItemElement itemElement,
            bool conditionResult)
        {
            ProcessItemElement(
                rootDirectory,
                new ItemOperationData(module, template, itemElement),
                conditionResult);
        }

        private void ProcessItemElement(
            string rootDirectory,
            ItemOperationData item,
            bool conditionResult)
        {
            LazyItemOperation operation = item.OperationKind switch
            {
                ItemOperationKind.Include =>
                    BuildIncludeOperation(
                        rootDirectory,
                        item,
                        conditionResult),
                ItemOperationKind.Remove =>
                    BuildRemoveOperation(
                        rootDirectory,
                        item,
                        conditionResult),
                ItemOperationKind.Update =>
                    BuildUpdateOperation(
                        rootDirectory,
                        item,
                        conditionResult),
                _ => throw new InvalidOperationException(
                    $"Unexpected item operation {item.OperationKind}."),
            };

            _itemLists.TryGetValue(
                item.ItemType,
                out LazyItemList previousItemList);
            LazyItemList newList = new LazyItemList(previousItemList, operation);
            _itemLists[item.ItemType] = newList;
        }

        private UpdateOperation BuildUpdateOperation(
            string rootDirectory,
            ItemOperationData item,
            bool conditionResult)
        {
            OperationBuilderWithMetadata operationBuilder =
                new OperationBuilderWithMetadata(
                    item.Element,
                    item.ItemType,
                    conditionResult);

            // Proces Update attribute
            ProcessItemSpec(
                rootDirectory,
                item.Update,
                item.Element.UpdateLocation,
                operationBuilder);

            ProcessMetadataElements(item, operationBuilder);

            return new UpdateOperation(operationBuilder, this);
        }

        private IncludeOperation BuildIncludeOperation(
            string rootDirectory,
            ItemOperationData item,
            bool conditionResult)
        {
            IncludeOperationBuilder operationBuilder =
                new IncludeOperationBuilder(
                    item.Element,
                    item.ItemType,
                    conditionResult);
            operationBuilder.ElementOrder = _nextElementOrder++;
            operationBuilder.RootDirectory = rootDirectory;
            operationBuilder.ConditionResult = conditionResult;

            // Process include
            ProcessItemSpec(
                rootDirectory,
                item.Include,
                item.Element.IncludeLocation,
                operationBuilder);

            // Code corresponds to Evaluator.EvaluateItemElement

            // Process exclude (STEP 4: Evaluate, split, expand and subtract any Exclude)
            if (item.Exclude.Length > 0)
            {
                // Expand properties here, because a property may have a value which is an item reference (ie "@(Bar)"), and
                //  if so we need to add the right item reference
                string evaluatedExclude =
                    _expander.ExpandIntoStringLeaveEscaped(
                        item.Exclude,
                        ExpanderOptions.ExpandProperties,
                        item.Element.ExcludeLocation);

                if (evaluatedExclude.Length > 0)
                {
                    var excludeSplits = ExpressionShredder.SplitSemiColonSeparatedList(evaluatedExclude);

                    foreach (var excludeSplit in excludeSplits)
                    {
                        operationBuilder.Excludes.Add(excludeSplit);
                        AddItemReferences(
                            excludeSplit,
                            operationBuilder,
                            item.Element.ExcludeLocation);
                    }
                }
            }

            // Process Metadata (STEP 5: Evaluate each metadata XML and apply them to each item we have so far)
            ProcessMetadataElements(item, operationBuilder);

            return new IncludeOperation(operationBuilder, this);
        }

        private RemoveOperation BuildRemoveOperation(
            string rootDirectory,
            ItemOperationData item,
            bool conditionResult)
        {
            RemoveOperationBuilder operationBuilder =
                new RemoveOperationBuilder(
                    item.Element,
                    item.ItemType,
                    conditionResult);

            ProcessItemSpec(
                rootDirectory,
                item.Remove,
                item.Element.RemoveLocation,
                operationBuilder);

            // Process MatchOnMetadata
            if (item.MatchOnMetadata.Length > 0)
            {
                string evaluatedmatchOnMetadata =
                    _expander.ExpandIntoStringLeaveEscaped(
                        item.MatchOnMetadata,
                        ExpanderOptions.ExpandProperties,
                        item.Element.MatchOnMetadataLocation);

                if (evaluatedmatchOnMetadata.Length > 0)
                {
                    var matchOnMetadataSplits = ExpressionShredder.SplitSemiColonSeparatedList(evaluatedmatchOnMetadata);

                    foreach (var matchOnMetadataSplit in matchOnMetadataSplits)
                    {
                        AddItemReferences(
                            matchOnMetadataSplit,
                            operationBuilder,
                            item.Element.MatchOnMetadataLocation);
                        string metadataExpanded =
                            _expander.ExpandIntoStringLeaveEscaped(
                                matchOnMetadataSplit,
                                ExpanderOptions.ExpandPropertiesAndItems,
                                item.Element.MatchOnMetadataLocation);
                        var metadataSplits = ExpressionShredder.SplitSemiColonSeparatedList(metadataExpanded);
                        operationBuilder.MatchOnMetadata.AddRange(metadataSplits);
                    }
                }
            }

            operationBuilder.MatchOnMetadataOptions = MatchOnMetadataOptions.CaseSensitive;
            if (Enum.TryParse(
                    item.MatchOnMetadataOptions,
                    out MatchOnMetadataOptions options))
            {
                operationBuilder.MatchOnMetadataOptions = options;
            }

            return new RemoveOperation(operationBuilder, this);
        }

        private void ProcessItemSpec(string rootDirectory, string itemSpec, IElementLocation itemSpecLocation, OperationBuilder builder)
        {
            using (EvaluationPerformanceInstrumentation.Measure(
                       EvaluationPerformanceMetric.ItemSpecConstruction))
            {
                builder.ItemSpec = new ItemSpec<P, I>(
                    itemSpec,
                    _outerExpander,
                    itemSpecLocation,
                    rootDirectory);
            }

            foreach (ItemSpecFragment fragment in builder.ItemSpec.Fragments)
            {
                if (fragment is ItemSpec<P, I>.ItemExpressionFragment itemExpression)
                {
                    AddReferencedItemLists(builder, itemExpression.Capture);
                }
            }
        }

        private void ProcessMetadataElements(
            ItemOperationData item,
            OperationBuilderWithMetadata operationBuilder)
        {
            using var measurement =
                EvaluationPerformanceInstrumentation.Measure(
                    EvaluationPerformanceMetric.MetadataAnalysis);
            if (item.Module is not null)
            {
                operationBuilder.SetLoweredMetadata(
                    item.Module,
                    item.Metadata);
            }

            if (item.Module is null
                    ? item.Element.HasMetadata
                    : item.Metadata.Count > 0)
            {
                ItemsAndMetadataPair itemsAndMetadataFound = new ItemsAndMetadataPair(null, null);

                // Since we're just attempting to expand properties in order to find referenced items and not expanding metadata,
                // unexpected errors may occur when evaluating property functions on unexpanded metadata. Just ignore them if that happens.
                // See: https://github.com/dotnet/msbuild/issues/3460
                const ExpanderOptions expanderOptions = ExpanderOptions.ExpandProperties | ExpanderOptions.LeavePropertiesUnexpandedOnError;
                if (item.Module is null)
                {
                    foreach (ProjectMetadataElement metadatumElement
                             in item.Element.MetadataEnumerable)
                    {
                        operationBuilder.Metadata.Add(metadatumElement);
                        ProcessMetadataExpression(
                            metadatumElement.Value,
                            metadatumElement.Condition,
                            metadatumElement,
                            ref itemsAndMetadataFound);
                    }
                }
                else
                {
                    int end = item.Metadata.Start + item.Metadata.Count;
                    for (int i = item.Metadata.Start; i < end; i++)
                    {
                        MetadataTemplate template = item.Module.Metadata[i];
                        ProjectMetadataElement metadatumElement =
                            (ProjectMetadataElement)item.Module.GetSource(
                                template.SourceId);
                        ProcessMetadataExpression(
                            item.Module.GetExpressionValue(
                                template.ValueExpressionId),
                            item.Module.GetConditionValue(
                                template.ConditionId),
                            metadatumElement,
                            ref itemsAndMetadataFound);
                    }
                }

                void ProcessMetadataExpression(
                    string value,
                    string condition,
                    ProjectMetadataElement metadatumElement,
                    ref ItemsAndMetadataPair found)
                {
                    string expression = _expander.ExpandIntoStringLeaveEscaped(
                        value,
                        expanderOptions,
                        metadatumElement.Location);

                    ExpressionShredder.GetReferencedItemNamesAndMetadata(
                        expression,
                        0,
                        expression.Length,
                        ref found,
                        ShredderOptions.All);

                    expression = _expander.ExpandIntoStringLeaveEscaped(
                        condition,
                        expanderOptions,
                        metadatumElement.ConditionLocation);

                    ExpressionShredder.GetReferencedItemNamesAndMetadata(
                        expression,
                        0,
                        expression.Length,
                        ref found,
                        ShredderOptions.All);
                }

                if (itemsAndMetadataFound.Items != null)
                {
                    foreach (var itemType in itemsAndMetadataFound.Items)
                    {
                        AddReferencedItemList(itemType, operationBuilder.ReferencedItemLists);
                    }
                }
            }
        }

        private void AddItemReferences(string expression, OperationBuilder operationBuilder, IElementLocation elementLocation)
        {
            if (Expander<P, I>.TryExpandSingleItemVectorExpression(
                    expression,
                    ExpanderOptions.ExpandItems,
                    elementLocation,
                    out ExpressionShredder.ItemExpressionCapture itemVector))
            {
                AddReferencedItemLists(operationBuilder, itemVector);
            }
        }

        private void AddReferencedItemLists(OperationBuilder operationBuilder, ExpressionShredder.ItemExpressionCapture match)
        {
            if (match.ItemType != null)
            {
                AddReferencedItemList(match.ItemType, operationBuilder.ReferencedItemLists);
            }
            if (match.Captures != null)
            {
                foreach (var subMatch in match.Captures)
                {
                    AddReferencedItemLists(operationBuilder, subMatch);
                }
            }
        }
    }
}
