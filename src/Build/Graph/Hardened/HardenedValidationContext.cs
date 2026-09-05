// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal enum ValueAvailability
{
    Static,
    Blocked,
    Deferred,
}

internal readonly record struct ValueState(ValueAvailability Availability, ValueOrigin? Origin = null)
{
    internal static ValueState Static => new(ValueAvailability.Static);

    internal static ValueState Deferred(ValueOrigin origin) => new(ValueAvailability.Deferred, origin);

    internal static ValueState Blocked(ValueOrigin origin) => new(ValueAvailability.Blocked, origin);

    internal bool IsStatic => Availability == ValueAvailability.Static;

    internal ValueState WithOrigin(string description)
        => IsStatic ? this : new ValueState(Availability, new ValueOrigin(description, Origin));

    internal static ValueState Combine(ValueState left, ValueState right)
        => left.Availability >= right.Availability ? left : right;
}

internal sealed class ValueOrigin(string description, ValueOrigin? previous = null)
{
    public override string ToString()
        => previous is null ? description : $"{description} from {previous}";
}

internal sealed class HardenedValidationContext
{
    private readonly Dictionary<string, ValueState> _properties = new(MSBuildNameIgnoreCaseComparer.Default);
    private readonly Dictionary<string, ItemListState> _items = new(MSBuildNameIgnoreCaseComparer.Default);

    internal HardenedValidationContext(ProjectInstance project)
    {
        foreach (ProjectPropertyInstance property in project.Properties)
        {
            _properties[property.Name] = ValueState.Static;
        }

        foreach (ProjectItemInstance item in project.Items)
        {
            ItemListState itemList = GetOrCreateItemList(item.ItemType);
            foreach (ProjectMetadataInstance metadata in item.Metadata)
            {
                itemList.Metadata[metadata.Name] = ValueState.Static;
            }
        }
    }

    internal ValueState GetProperty(string propertyName)
        => _properties.TryGetValue(propertyName, out ValueState state) ? state : ValueState.Static;

    internal void SetProperty(string propertyName, ValueState state, bool overwrite = false)
    {
        ValueState propertyState = state.WithOrigin($"property '{propertyName}'");
        _properties[propertyName] = overwrite
            ? propertyState
            : ValueState.Combine(GetProperty(propertyName), propertyState);
    }

    internal ValueState GetItemMembership(string itemType)
        => GetOrCreateItemList(itemType).Membership;

    internal ValueState GetItemValue(string itemType, bool includeMetadata)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        ValueState state = itemList.Membership;

        if (includeMetadata)
        {
            state = ValueState.Combine(state, itemList.DefaultMetadata);
            foreach (ValueState metadataState in itemList.Metadata.Values)
            {
                state = ValueState.Combine(state, metadataState);
            }
        }

        return state;
    }

    internal ValueState GetMetadata(string itemType, string metadataName)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        ValueState metadataState = MSBuildNameIgnoreCaseComparer.Default.Equals(metadataName, "Identity")
            ? itemList.Membership
            : itemList.Metadata.TryGetValue(metadataName, out ValueState state)
                ? state
                : itemList.DefaultMetadata;

        return ValueState.Combine(itemList.Membership, metadataState);
    }

    internal IReadOnlyDictionary<string, ValueState> GetMetadata(string itemType)
        => GetOrCreateItemList(itemType).Metadata;

    internal ValueState GetDefaultMetadata(string itemType)
        => GetOrCreateItemList(itemType).DefaultMetadata;

    internal void AddItems(
        string itemType,
        ValueState membership,
        IReadOnlyDictionary<string, ValueState>? inheritedMetadata,
        ValueState inheritedDefaultMetadata,
        IReadOnlyDictionary<string, ValueState> assignedMetadata,
        string? keepMetadata,
        string? removeMetadata,
        string description)
    {
        ItemListState destination = GetOrCreateItemList(itemType);
        destination.Membership = ValueState.Combine(destination.Membership, membership.WithOrigin(description));
        bool hasKeepFilter = TryParseLiteralMetadataNames(keepMetadata, out HashSet<string>? metadataToKeep);
        bool hasRemoveFilter = TryParseLiteralMetadataNames(removeMetadata, out HashSet<string>? metadataToRemove);

        if (!hasKeepFilter)
        {
            destination.DefaultMetadata = ValueState.Combine(
                destination.DefaultMetadata,
                inheritedDefaultMetadata.WithOrigin(description));
        }

        if (inheritedMetadata is not null)
        {
            foreach (KeyValuePair<string, ValueState> metadata in inheritedMetadata)
            {
                if ((hasKeepFilter && !metadataToKeep!.Contains(metadata.Key)) ||
                    (hasRemoveFilter && metadataToRemove!.Contains(metadata.Key)))
                {
                    continue;
                }

                MergeMetadata(destination, metadata.Key, metadata.Value.WithOrigin(description));
            }
        }

        foreach (KeyValuePair<string, ValueState> metadata in assignedMetadata)
        {
            MergeMetadata(
                destination,
                metadata.Key,
                metadata.Value.WithOrigin($"metadata '{metadata.Key}' on item '{itemType}'"));
        }
    }

    internal void UpdateMetadata(
        string itemType,
        IReadOnlyDictionary<string, ValueState> assignedMetadata)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        foreach (KeyValuePair<string, ValueState> metadata in assignedMetadata)
        {
            MergeMetadata(
                itemList,
                metadata.Key,
                metadata.Value.WithOrigin($"metadata '{metadata.Key}' on item '{itemType}'"));
        }
    }

    internal void ApplyMetadataFilters(string itemType, string? keepMetadata, string? removeMetadata)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);

        if (TryParseLiteralMetadataNames(keepMetadata, out HashSet<string>? metadataToKeep))
        {
            List<string> keysToRemove = [];
            foreach (string metadataName in itemList.Metadata.Keys)
            {
                if (!metadataToKeep!.Contains(metadataName))
                {
                    keysToRemove.Add(metadataName);
                }
            }

            foreach (string metadataName in keysToRemove)
            {
                itemList.Metadata.Remove(metadataName);
            }

            itemList.DefaultMetadata = ValueState.Static;
        }

        if (TryParseLiteralMetadataNames(removeMetadata, out HashSet<string>? metadataToRemove))
        {
            foreach (string metadataName in metadataToRemove!)
            {
                itemList.Metadata.Remove(metadataName);
            }
        }
    }

    internal void AddTaskOutputItems(string itemType, ValueState outputState)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        ValueState state = outputState.WithOrigin($"item '{itemType}'");
        itemList.Membership = ValueState.Combine(itemList.Membership, state);
        itemList.DefaultMetadata = ValueState.Combine(itemList.DefaultMetadata, state);
    }

    internal void BlockItemMembership(string itemType, ValueState cause, string description)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        ValueState blocked = cause.Availability == ValueAvailability.Blocked
            ? cause.WithOrigin(description)
            : ValueState.Blocked(new ValueOrigin(description, cause.Origin));

        itemList.Membership = blocked;
        itemList.DefaultMetadata = blocked;
    }

    internal void BlockMetadata(string itemType, string metadataName, ValueState cause, string description)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        ValueState blocked = cause.Availability == ValueAvailability.Blocked
            ? cause.WithOrigin(description)
            : ValueState.Blocked(new ValueOrigin(description, cause.Origin));
        itemList.Metadata[metadataName] = blocked;
    }

    private ItemListState GetOrCreateItemList(string itemType)
    {
        if (!_items.TryGetValue(itemType, out ItemListState? itemList))
        {
            itemList = new ItemListState();
            _items.Add(itemType, itemList);
        }

        return itemList;
    }

    private static void MergeMetadata(ItemListState itemList, string metadataName, ValueState state)
    {
        ValueState existing = itemList.Metadata.TryGetValue(metadataName, out ValueState metadataState)
            ? metadataState
            : itemList.DefaultMetadata;
        itemList.Metadata[metadataName] = ValueState.Combine(existing, state);
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

    private sealed class ItemListState
    {
        internal ValueState Membership { get; set; } = ValueState.Static;

        internal ValueState DefaultMetadata { get; set; } = ValueState.Static;

        internal Dictionary<string, ValueState> Metadata { get; } = new(MSBuildNameIgnoreCaseComparer.Default);
    }
}
