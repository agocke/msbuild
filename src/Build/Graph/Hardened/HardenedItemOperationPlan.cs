// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedItemOperationPlan
{
    private readonly Dictionary<OperationKey, ResolvedOperation> _operations =
        new(OperationKeyComparer.Instance);
    private readonly Dictionary<ProjectItemInstance, ItemProvenance> _itemProvenance =
        new(ProjectItemReferenceComparer.Instance);

    internal HardenedItemOperationPlan(string? workspaceRoot)
    {
        if (workspaceRoot is not null)
        {
            string fullPath = Path.GetFullPath(workspaceRoot);
            string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            WorkspaceRoot = fullPath.Length == pathRoot.Length
                ? fullPath
                : fullPath.TrimTrailingSlashes();
        }

        GlobPolicy = new HardenedGlobPolicy(WorkspaceRoot);
    }

    internal string? WorkspaceRoot { get; }

    internal HardenedGlobPolicy GlobPolicy { get; }

    internal ExpansionCapture CreateExpansionCapture()
        => new();

    internal void Record(
        HardenedBucketPath bucketPath,
        ProjectItemGroupTaskItemInstance operation,
        IReadOnlyList<ProjectItemInstance> items,
        IReadOnlyDictionary<ProjectItemInstance, ProjectItemInstance> itemSources,
        ExpansionCapture expansionCapture)
    {
        var resolvedItems = ImmutableArray.CreateBuilder<ResolvedItem>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            ProjectItemInstance item = items[i];
            ItemProvenance? provenance = null;
            if (itemSources.TryGetValue(item, out ProjectItemInstance? sourceItem))
            {
                if (!_itemProvenance.TryGetValue(sourceItem, out provenance))
                {
                    provenance = new ItemProvenance(
                        sourceItem.ItemType,
                        ((IItem)sourceItem).EvaluatedIncludeEscaped,
                        Source: null);
                }
            }

            var metadata = ImmutableDictionary.CreateBuilder<string, string>(
                MSBuildNameIgnoreCaseComparer.Default);
            foreach (KeyValuePair<string, string> metadatum in ((IMetadataContainer)item).EnumerateMetadata())
            {
                metadata[metadatum.Key] = metadatum.Value;
            }

            var resolvedItem = new ResolvedItem(
                ((IItem)item).EvaluatedIncludeEscaped,
                item.EvaluatedIncludeBeforeWildcardExpansionEscaped,
                metadata.ToImmutable(),
                provenance);
            resolvedItems.Add(resolvedItem);
            _itemProvenance[item] = new ItemProvenance(
                item.ItemType,
                ((IItem)item).EvaluatedIncludeEscaped,
                provenance);
        }

        var key = new OperationKey(bucketPath, operation);
        var result = new ResolvedOperation(
            resolvedItems.MoveToImmutable(),
            expansionCapture.Includes.ToImmutableArray(),
            expansionCapture.Excludes.ToImmutableArray());
        if (_operations.TryGetValue(key, out ResolvedOperation? existing))
        {
            if (!ResultsEqual(existing, result))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    operation.IncludeLocation,
                    "HardenedGraphConflictingItemOperationResult",
                    operation.ItemType);
            }

            return;
        }

        _operations.Add(key, result);
    }

    private static bool ResultsEqual(
        ResolvedOperation left,
        ResolvedOperation right)
    {
        if (left.Items.Length != right.Items.Length ||
            !GlobExpansionsEqual(left.Includes, right.Includes) ||
            !GlobExpansionsEqual(left.Excludes, right.Excludes))
        {
            return false;
        }

        for (int i = 0; i < left.Items.Length; i++)
        {
            ResolvedItem leftItem = left.Items[i];
            ResolvedItem rightItem = right.Items[i];
            if (!string.Equals(
                    leftItem.EvaluatedIncludeEscaped,
                    rightItem.EvaluatedIncludeEscaped,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    leftItem.IncludeBeforeWildcardExpansionEscaped,
                    rightItem.IncludeBeforeWildcardExpansionEscaped,
                    StringComparison.Ordinal) ||
                leftItem.Metadata.Count != rightItem.Metadata.Count ||
                !Equals(leftItem.Provenance, rightItem.Provenance))
            {
                return false;
            }

            foreach (KeyValuePair<string, string> metadatum in leftItem.Metadata)
            {
                if (!rightItem.Metadata.TryGetValue(metadatum.Key, out string? rightValue) ||
                    !string.Equals(metadatum.Value, rightValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool GlobExpansionsEqual(
        ImmutableArray<GlobExpansion> left,
        ImmutableArray<GlobExpansion> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            GlobExpansion leftExpansion = left[i];
            GlobExpansion rightExpansion = right[i];
            if (!string.Equals(
                    leftExpansion.PatternEscaped,
                    rightExpansion.PatternEscaped,
                    StringComparison.Ordinal) ||
                leftExpansion.MatchesEscaped.Length != rightExpansion.MatchesEscaped.Length)
            {
                return false;
            }

            for (int matchIndex = 0; matchIndex < leftExpansion.MatchesEscaped.Length; matchIndex++)
            {
                if (!string.Equals(
                        leftExpansion.MatchesEscaped[matchIndex],
                        rightExpansion.MatchesEscaped[matchIndex],
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal bool TryMaterialize(
        HardenedBucketPath bucketPath,
        ProjectItemGroupTaskItemInstance operation,
        ProjectInstance project,
        out List<ProjectItemInstance>? items)
    {
        if (!_operations.TryGetValue(new OperationKey(bucketPath, operation), out ResolvedOperation? resolvedOperation))
        {
            items = null;
            return false;
        }

        items = new List<ProjectItemInstance>(resolvedOperation.Items.Length);
        foreach (ResolvedItem resolvedItem in resolvedOperation.Items)
        {
            items.Add(
                new ProjectItemInstance(
                    project,
                    operation.ItemType,
                    resolvedItem.EvaluatedIncludeEscaped,
                    resolvedItem.IncludeBeforeWildcardExpansionEscaped,
                    resolvedItem.Metadata,
                    itemDefinitions: null,
                    operation.Location.File,
                    useItemDefinitionsWithoutModification: false));
        }

        return true;
    }

    internal sealed class HardenedBucketPath
    {
        internal static readonly HardenedBucketPath Root = new(parent: null, owner: null, sequenceNumber: 0);

        private readonly int _hashCode;

        private HardenedBucketPath(HardenedBucketPath? parent, object? owner, int sequenceNumber)
        {
            Parent = parent;
            Owner = owner;
            SequenceNumber = sequenceNumber;
            _hashCode = owner is null
                ? 0
                : CombineHashCodes(
                    parent?._hashCode ?? 0,
                    RuntimeHelpers.GetHashCode(owner),
                    sequenceNumber);
        }

        private HardenedBucketPath? Parent { get; }

        private object? Owner { get; }

        private int SequenceNumber { get; }

        internal HardenedBucketPath Append(object owner, int sequenceNumber)
            => new(this, owner, sequenceNumber);

        public override int GetHashCode() => _hashCode;

        public override bool Equals(object? obj)
        {
            HardenedBucketPath? left = this;
            HardenedBucketPath? right = obj as HardenedBucketPath;
            while (left is not null && right is not null)
            {
                if (!ReferenceEquals(left.Owner, right.Owner) ||
                    left.SequenceNumber != right.SequenceNumber)
                {
                    return false;
                }

                left = left.Parent;
                right = right.Parent;
            }

            return left is null && right is null;
        }

        private static int CombineHashCodes(int first, int second, int third)
        {
            unchecked
            {
                int hash = (first * 397) ^ second;
                return (hash * 397) ^ third;
            }
        }
    }

    private readonly record struct OperationKey(
        HardenedBucketPath BucketPath,
        ProjectItemGroupTaskItemInstance Operation);

    private readonly record struct ResolvedItem(
        string EvaluatedIncludeEscaped,
        string IncludeBeforeWildcardExpansionEscaped,
        ImmutableDictionary<string, string> Metadata,
        ItemProvenance? Provenance);

    private sealed record ResolvedOperation(
        ImmutableArray<ResolvedItem> Items,
        ImmutableArray<GlobExpansion> Includes,
        ImmutableArray<GlobExpansion> Excludes);

    internal readonly record struct GlobExpansion(
        string PatternEscaped,
        ImmutableArray<string> MatchesEscaped);

    internal sealed class ExpansionCapture
    {
        internal List<GlobExpansion> Includes { get; } = [];

        internal List<GlobExpansion> Excludes { get; } = [];

        internal void RecordInclude(string patternEscaped, IEnumerable<string> matchesEscaped)
            => Includes.Add(new GlobExpansion(patternEscaped, [.. matchesEscaped]));

        internal void RecordInclude(string patternEscaped, IList<ProjectItemInstance> matches)
        {
            var matchesEscaped = ImmutableArray.CreateBuilder<string>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                matchesEscaped.Add(((IItem)matches[i]).EvaluatedIncludeEscaped);
            }

            Includes.Add(new GlobExpansion(patternEscaped, matchesEscaped.MoveToImmutable()));
        }

        internal void RecordExcludeUnescaped(string patternEscaped, IReadOnlyList<string> matches)
        {
            var matchesEscaped = ImmutableArray.CreateBuilder<string>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                matchesEscaped.Add(EscapingUtilities.Escape(matches[i]));
            }

            Excludes.Add(new GlobExpansion(patternEscaped, matchesEscaped.MoveToImmutable()));
        }
    }

    private sealed record ItemProvenance(
        string ItemType,
        string EvaluatedIncludeEscaped,
        ItemProvenance? Source);

    private sealed class OperationKeyComparer : IEqualityComparer<OperationKey>
    {
        internal static readonly OperationKeyComparer Instance = new();

        public bool Equals(OperationKey x, OperationKey y)
            => ReferenceEquals(x.Operation, y.Operation) &&
               x.BucketPath.Equals(y.BucketPath);

        public int GetHashCode(OperationKey obj)
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(obj.Operation) * 397) ^
                       obj.BucketPath.GetHashCode();
            }
        }
    }

    private sealed class ProjectItemReferenceComparer : IEqualityComparer<ProjectItemInstance>
    {
        internal static readonly ProjectItemReferenceComparer Instance = new();

        public bool Equals(ProjectItemInstance? x, ProjectItemInstance? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(ProjectItemInstance obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
