// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Eventing;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.CodeAnalysis.Collections;

namespace Microsoft.Build.Evaluation
{
    internal partial class LazyItemEvaluator<P, I, M, D>
    {
        private class IncludeOperation : LazyItemOperation
        {
            private readonly int _elementOrder;
            private readonly string? _rootDirectory;
            private readonly ImmutableSegmentedList<string> _excludes;
            private readonly DeferredMetadata _metadata;
            private readonly ConstantItemMetadata _constantMetadata;
            private List<ConstantLiteralInclude> _additionalConstantLiterals =
                null!;

            public IncludeOperation(IncludeOperationBuilder builder, LazyItemEvaluator<P, I, M, D> lazyEvaluator)
                : base(builder, lazyEvaluator)
            {
                _elementOrder = builder.ElementOrder;
                _rootDirectory = builder.RootDirectory;

                _excludes = builder.ToExcludes();
                _metadata = builder.ToMetadata();
                _constantMetadata = _metadata.GetConstantMetadata();
            }

            private readonly struct ConstantLiteralInclude
            {
                internal ConstantLiteralInclude(
                    ProjectItemElement itemElement,
                    int elementOrder,
                    string value,
                    ConstantItemMetadata metadata,
                    bool conditionResult)
                {
                    ItemElement = itemElement;
                    ElementOrder = elementOrder;
                    Value = value;
                    Metadata = metadata;
                    ConditionResult = conditionResult;
                }

                internal ProjectItemElement ItemElement { get; }

                internal int ElementOrder { get; }

                internal string Value { get; }

                internal ConstantItemMetadata Metadata { get; }

                internal bool ConditionResult { get; }
            }

            internal bool TryAppendConstantLiteral(
                ProjectItemElement itemElement,
                int elementOrder,
                string value,
                ConstantItemMetadata metadata,
                bool conditionResult)
            {
                if (!IsConstantLiteral)
                {
                    return false;
                }

                _additionalConstantLiterals ??=
                    new List<ConstantLiteralInclude>();
                _additionalConstantLiterals.Add(
                    new ConstantLiteralInclude(
                        itemElement,
                        elementOrder,
                        value,
                        metadata,
                        conditionResult));
                return true;
            }

            private bool IsConstantLiteral =>
                _excludes.IsEmpty &&
                _itemSpec.Fragments.Count == 1 &&
                _itemSpec.Fragments[0] is ValueFragment &&
                (_metadata.Count == 0 ||
                 _constantMetadata is not null);

            protected override void ApplyImpl(
                OrderedItemDataCollection.Builder listBuilder,
                ImmutableHashSet<string> globsToIgnore)
            {
                if (_excludes.IsEmpty &&
                    _itemSpec.Fragments.Count == 1 &&
                    _itemSpec.Fragments[0] is ValueFragment valueFragment)
                {
                    if (_metadata.Count == 0 ||
                        _constantMetadata is not null)
                    {
                        AddConstantLiteral(
                            listBuilder,
                            _itemElement,
                            _elementOrder,
                            valueFragment.TextFragment,
                            _constantMetadata,
                            _conditionResult);
                        if (_additionalConstantLiterals is not null)
                        {
                            foreach (ConstantLiteralInclude include
                                     in _additionalConstantLiterals)
                            {
                                using (_lazyEvaluator
                                           ._evaluationProfiler
                                           .TrackElement(
                                               include.ItemElement))
                                {
                                    AddConstantLiteral(
                                        listBuilder,
                                        include.ItemElement,
                                        include.ElementOrder,
                                        include.Value,
                                        include.Metadata,
                                        include.ConditionResult);
                                }
                            }
                        }

                        return;
                    }

                    string value = valueFragment.TextFragment;
                    I item = CreateItem(
                        _itemElement,
                        value,
                        value,
                        _itemElement.ContainingProject.FullPath);
                    DecorateItemsWithMetadata(
                        [new ItemBatchingContext(item)],
                        _metadata);
                    listBuilder.Add(
                        new ItemData(
                            item,
                            _itemElement,
                            _elementOrder,
                            _conditionResult));
                    return;
                }

                base.ApplyImpl(listBuilder, globsToIgnore);
            }

            private void AddConstantLiteral(
                OrderedItemDataCollection.Builder listBuilder,
                ProjectItemElement itemElement,
                int elementOrder,
                string value,
                ConstantItemMetadata metadata,
                bool conditionResult)
            {
                I item = CreateItem(
                    itemElement,
                    value,
                    value,
                    itemElement.ContainingProject.FullPath);
                if (metadata is not null)
                {
                    SetMetadata(
                        itemElement,
                        metadata,
                        item);
                }

                listBuilder.Add(
                    new ItemData(
                        item,
                        itemElement,
                        elementOrder,
                        conditionResult));
            }

            [SuppressMessage("Microsoft.Dispose", "CA2000:Dispose objects before losing scope", Justification = "_lazyEvaluator._evaluationProfiler has own dipose logic.")]
            protected override ImmutableArray<I> SelectItems(OrderedItemDataCollection.Builder listBuilder, ImmutableHashSet<string> globsToIgnore)
            {
                ImmutableArray<I>.Builder? itemsToAdd = null;

                List<string>? excludePatterns = null;
                if (!_excludes.IsEmpty)
                {
                    // STEP 4: Evaluate, split, expand and subtract any Exclude
                    foreach (string exclude in _excludes)
                    {
                        string excludeExpanded = _expander.ExpandIntoStringLeaveEscaped(exclude, ExpanderOptions.ExpandPropertiesAndItems, _itemElement.ExcludeLocation);
                        var excludeSplits = ExpressionShredder.SplitSemiColonSeparatedList(excludeExpanded);
                        excludePatterns ??= new List<string>();
                        excludePatterns.AddRange(excludeSplits);
                    }
                }

                ISet<string>? excludePatternsForGlobs = null;
                FileSpecMatcherTester?[]? matchers = null;

                foreach (var fragment in _itemSpec.Fragments)
                {
                    if (fragment is ItemSpec<P, I>.ItemExpressionFragment itemReferenceFragment)
                    {
                        // STEP 3: If expression is "@(x)" copy specified list with its metadata, otherwise just treat as string
                        var itemsFromExpression = _expander.ExpandExpressionCaptureIntoItems(
                            itemReferenceFragment.Capture,
                            _evaluatorData,
                            _itemFactory,
                            ExpanderOptions.ExpandItems,
                            includeNullEntries: false,
                            isTransformExpression: out _,
                            elementLocation: _itemElement.IncludeLocation);

                        itemsToAdd ??= ImmutableArray.CreateBuilder<I>();

                        if (excludePatterns is not null)
                        {
                            matchers ??= new FileSpecMatcherTester?[excludePatterns.Count];

                            foreach (var item in itemsFromExpression)
                            {
                                if (!ExcludeTester(_rootDirectory, excludePatterns, matchers, item.EvaluatedInclude))
                                {
                                    itemsToAdd.Add(item);
                                }
                            }
                        }
                        else
                        {
                            itemsToAdd.AddRange(itemsFromExpression);
                        }
                    }
                    else if (fragment is ValueFragment valueFragment)
                    {
                        string value = valueFragment.TextFragment;

                        if (excludePatterns is null ||
                            !ExcludeTester(
                                _rootDirectory,
                                excludePatterns,
                                matchers ??=
                                    new FileSpecMatcherTester?[
                                        excludePatterns.Count],
                                EscapingUtilities.UnescapeAll(value)))
                        {
                            itemsToAdd ??= ImmutableArray.CreateBuilder<I>();
                            itemsToAdd.Add(_itemFactory.CreateItem(value, value, _itemElement.ContainingProject.FullPath));
                        }
                    }
                    else if (fragment is GlobFragment globFragment)
                    {
                        // If this item is behind a false condition and represents a full drive/filesystem scan, expanding it is
                        // almost certainly undesired. It should be skipped to avoid evaluation taking an excessive amount of time.
                        bool skipGlob = !_conditionResult && globFragment.IsFullFileSystemScan && !Traits.Instance.EscapeHatches.AlwaysEvaluateDangerousGlobs;
                        if (!skipGlob)
                        {
                            string glob = globFragment.TextFragment;

                            if (excludePatternsForGlobs == null)
                            {
                                excludePatternsForGlobs = BuildExcludePatternsForGlobs(globsToIgnore, excludePatterns);
                            }

                            string[] includeSplitFilesEscaped;
                            if (MSBuildEventSource.Log.IsEnabled())
                            {
                                MSBuildEventSource.Log.ExpandGlobStart(_rootDirectory ?? string.Empty, glob, string.Join(", ", excludePatternsForGlobs));
                            }

                            using (_lazyEvaluator?._evaluationProfiler.TrackGlob(_rootDirectory, glob, excludePatternsForGlobs))
                            {
                                includeSplitFilesEscaped = EngineFileUtilities.GetFileListEscaped(
                                    _rootDirectory,
                                    glob,
                                    excludePatternsForGlobs,
                                    fileMatcher: FileMatcher,
                                    loggingMechanism: _lazyEvaluator?._loggingContext,
                                    includeLocation: _itemElement.IncludeLocation,
                                    excludeLocation: _itemElement.ExcludeLocation);
                            }

                            if (MSBuildEventSource.Log.IsEnabled())
                            {
                                MSBuildEventSource.Log.ExpandGlobStop(_rootDirectory ?? string.Empty, glob, string.Join(", ", excludePatternsForGlobs));
                            }

                            foreach (string includeSplitFileEscaped in includeSplitFilesEscaped)
                            {
                                itemsToAdd ??= ImmutableArray.CreateBuilder<I>();
                                itemsToAdd.Add(_itemFactory.CreateItem(includeSplitFileEscaped, glob, _itemElement.ContainingProject.FullPath));
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(fragment.GetType().ToString());
                    }
                }

                return itemsToAdd?.ToImmutable() ?? ImmutableArray<I>.Empty;

                static bool ExcludeTester(string? directory, List<string> excludePatterns, FileSpecMatcherTester?[] matchers, string item)
                {
                    if (excludePatterns.Count == 0)
                    {
                        return false;
                    }

                    bool found = false;
                    for (int i = 0; i < matchers.Length; ++i)
                    {
                        FileSpecMatcherTester? matcher = matchers[i];
                        if (!matcher.HasValue)
                        {
                            matcher = FileSpecMatcherTester.Parse(directory, excludePatterns[i]);
                            matchers[i] = matcher;
                        }

                        if (matcher.Value.IsMatch(item))
                        {
                            found = true;
                            break;
                        }
                    }

                    return found;
                }
            }

            private static ISet<string> BuildExcludePatternsForGlobs(
                ImmutableHashSet<string> globsToIgnore,
                List<string>? excludePatterns)
            {
                var anyExcludes = excludePatterns is not null;
                var anyGlobsToIgnore = globsToIgnore.Count > 0;

                if (anyGlobsToIgnore && anyExcludes)
                {
                    return excludePatterns!.Concat(globsToIgnore).ToImmutableHashSet();
                }

                return anyExcludes
                    ? excludePatterns!.ToImmutableHashSet()
                    : globsToIgnore;
            }

            protected override void MutateItems(ImmutableArray<I> items)
            {
                if (_metadata.Count > 0)
                {
                    DecorateItemsWithMetadata(
                        items.Select(
                            i => new ItemBatchingContext(i)),
                        _metadata);
                }
            }

            protected override void SaveItems(ImmutableArray<I> items, OrderedItemDataCollection.Builder listBuilder)
            {
                foreach (var item in items)
                {
                    listBuilder.Add(new ItemData(item, _itemElement, _elementOrder, _conditionResult));
                }
            }
        }

        private class IncludeOperationBuilder : OperationBuilderWithMetadata
        {
            private ImmutableSegmentedList<string>.Builder _excludes = null!;

            public int ElementOrder { get; set; }
            public string? RootDirectory { get; set; }

            public ImmutableSegmentedList<string>.Builder Excludes =>
                _excludes ??=
                    ImmutableSegmentedList.CreateBuilder<string>();

            public IncludeOperationBuilder(
                ProjectItemElement itemElement,
                string itemType,
                bool conditionResult)
                : base(itemElement, itemType, conditionResult)
            {
            }

            public ImmutableSegmentedList<string> ToExcludes() =>
                _excludes?.ToImmutable() ??
                ImmutableSegmentedList<string>.Empty;
        }
    }
}
