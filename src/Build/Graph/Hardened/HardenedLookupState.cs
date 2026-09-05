// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedLookupState
{
    private static readonly IReadOnlyDictionary<string, ValueState> s_emptyMetadata =
        new Dictionary<string, ValueState>();

    private Scope _scope;

    private HardenedLookupState(Scope scope)
    {
        _scope = scope;
    }

    internal static HardenedLookupState Create()
        => new(new Scope(parent: null));

    internal HardenedLookupState Fork()
        => new(_scope);

    internal HardenedLookupState Snapshot()
        => new(_scope.CloneChain());

    internal void EnterScope()
    {
        _scope = new Scope(_scope);
    }

    internal void LeaveScope()
    {
        Scope parent = _scope.Parent ??
            throw new InvalidOperationException("The outer hardened lookup scope cannot be left.");

        _scope.MergeInto(parent);
        _scope = parent;
    }

    internal ValueState GetProperty(string propertyName)
    {
        for (Scope? scope = _scope; scope is not null; scope = scope.Parent)
        {
            if (scope.Properties is not null &&
                scope.Properties.TryGetValue(propertyName, out ValueState state))
            {
                return state;
            }
        }

        return ValueState.Static;
    }

    internal void SetProperty(string propertyName, ValueState state, bool overwrite = false)
    {
        ValueState propertyState = state.WithOrigin($"property '{propertyName}'");
        SetPropertyState(
            propertyName,
            overwrite
                ? propertyState
                : ValueState.Combine(GetProperty(propertyName), propertyState));
    }

    internal void SetConcreteProperty(string propertyName)
    {
        SetPropertyState(propertyName, ValueState.Static);
    }

    internal void CopyPropertyFrom(HardenedLookupState source, string propertyName)
    {
        SetPropertyState(propertyName, source.GetProperty(propertyName));
    }

    internal ValueState GetItemMembership(string itemType)
        => FindItemList(itemType)?.Membership ?? ValueState.Static;

    internal ValueState GetItemValue(string itemType, bool includeMetadata)
    {
        ItemListState? itemList = FindItemList(itemType);
        if (itemList is null)
        {
            return ValueState.Static;
        }

        ValueState state = itemList.Membership;
        if (!includeMetadata)
        {
            return state;
        }

        state = ValueState.Combine(state, itemList.DefaultMetadata);
        foreach (ValueState metadataState in itemList.Metadata.Values)
        {
            state = ValueState.Combine(state, metadataState);
        }

        foreach (ItemState itemState in itemList.Items.Values)
        {
            state = ValueState.Combine(state, itemState.Identity);
            foreach (ValueState metadataState in itemState.Metadata.Values)
            {
                state = ValueState.Combine(state, metadataState);
            }
        }

        return state;
    }

    internal ValueState GetMetadata(string itemType, string metadataName)
    {
        ItemListState? itemList = FindItemList(itemType);
        if (itemList is null)
        {
            return ValueState.Static;
        }

        ValueState metadataState = MSBuildNameIgnoreCaseComparer.Default.Equals(metadataName, "Identity")
            ? itemList.Membership
            : itemList.Metadata.TryGetValue(metadataName, out ValueState state)
                ? state
                : itemList.DefaultMetadata;

        foreach (ItemState itemState in itemList.Items.Values)
        {
            ValueState concreteState;
            if (MSBuildNameIgnoreCaseComparer.Default.Equals(metadataName, "Identity"))
            {
                concreteState = itemState.Identity;
            }
            else if (itemState.Metadata.TryGetValue(metadataName, out state))
            {
                concreteState = state;
            }
            else
            {
                continue;
            }

            metadataState = ValueState.Combine(metadataState, concreteState);
        }

        return ValueState.Combine(itemList.Membership, metadataState);
    }

    internal ValueState GetMetadata(ProjectItemInstance item, string metadataName)
    {
        ItemListState? itemList = FindItemList(item.ItemType);
        if (itemList is null)
        {
            return ValueState.Static;
        }

        if (!itemList.Items.TryGetValue(item, out ItemState? itemState))
        {
            ValueState listMetadataState = MSBuildNameIgnoreCaseComparer.Default.Equals(metadataName, "Identity")
                ? itemList.Membership
                : itemList.Metadata.TryGetValue(metadataName, out ValueState state)
                    ? state
                    : itemList.DefaultMetadata;
            return ValueState.Combine(itemList.Membership, listMetadataState);
        }

        ValueState metadataState;
        if (MSBuildNameIgnoreCaseComparer.Default.Equals(metadataName, "Identity"))
        {
            metadataState = itemState.Identity;
        }
        else if (itemState.Metadata.TryGetValue(metadataName, out ValueState state))
        {
            metadataState = state;
        }
        else if (itemState.IgnoreListMetadata)
        {
            metadataState = ValueState.Static;
        }
        else
        {
            metadataState = itemList.Metadata.TryGetValue(metadataName, out state)
                ? state
                : itemList.DefaultMetadata;
        }

        return ValueState.Combine(itemList.Membership, metadataState);
    }

    internal IReadOnlyDictionary<string, ValueState> GetMetadata(string itemType)
    {
        ItemListState? itemList = FindItemList(itemType);
        if (itemList is null)
        {
            return s_emptyMetadata;
        }

        Dictionary<string, ValueState>? metadata = null;
        foreach (ItemState itemState in itemList.Items.Values)
        {
            foreach (KeyValuePair<string, ValueState> itemMetadata in itemState.Metadata)
            {
                metadata ??= new Dictionary<string, ValueState>(
                    itemList.Metadata,
                    MSBuildNameIgnoreCaseComparer.Default);
                ValueState existing = metadata.TryGetValue(itemMetadata.Key, out ValueState state)
                    ? state
                    : itemList.DefaultMetadata;
                metadata[itemMetadata.Key] = ValueState.Combine(existing, itemMetadata.Value);
            }
        }

        return metadata ?? itemList.Metadata;
    }

    internal ValueState GetDefaultMetadata(string itemType)
        => FindItemList(itemType)?.DefaultMetadata ?? ValueState.Static;

    internal void SetItemIdentity(ProjectItemInstance item, ValueState state)
    {
        GetOrCreateItemState(item).Identity = state;
        _scope.MarkItemChanged(item.ItemType);
    }

    internal void SetMetadata(ProjectItemInstance item, string metadataName, ValueState state)
    {
        GetOrCreateItemState(item).Metadata[metadataName] = state;
        _scope.MarkItemChanged(item.ItemType);
    }

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
        _scope.MarkItemListChanged(itemType);
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
        _scope.MarkItemListChanged(itemType);
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
        _scope.MarkItemListChanged(itemType);

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
        _scope.MarkItemListChanged(itemType);
        ValueState state = outputState.WithOrigin($"item '{itemType}'");
        itemList.Membership = ValueState.Combine(itemList.Membership, state);
        itemList.DefaultMetadata = ValueState.Combine(itemList.DefaultMetadata, state);
    }

    internal void BlockItemMembership(string itemType, ValueState cause, string description)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        _scope.MarkItemListChanged(itemType);
        ValueState blocked = cause.Availability == ValueAvailability.Blocked
            ? cause.WithOrigin(description)
            : ValueState.Blocked(new ValueOrigin(description, cause.Origin));

        itemList.Membership = blocked;
        itemList.DefaultMetadata = blocked;
    }

    internal void BlockMetadata(string itemType, string metadataName, ValueState cause, string description)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        _scope.MarkItemListChanged(itemType);
        ValueState blocked = cause.Availability == ValueAvailability.Blocked
            ? cause.WithOrigin(description)
            : ValueState.Blocked(new ValueOrigin(description, cause.Origin));
        itemList.Metadata[metadataName] = blocked;
    }

    internal void CopyItemFrom(HardenedLookupState source, string itemType)
    {
        ItemListState? sourceItem = source.FindItemList(itemType);
        SetItemList(itemType, sourceItem is null ? new ItemListState() : new ItemListState(sourceItem));
        _scope.MarkItemListChanged(itemType);
    }

    internal void TruncateLookupsForItemTypes(ICollection<string> itemTypes)
    {
        _scope.TruncatedItemTypes ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        foreach (string itemType in itemTypes)
        {
            _scope.TruncatedItemTypes.Add(itemType);
        }
    }

    internal void PopulateWithItems(
        string itemType,
        ICollection<ProjectItemInstance> items,
        Func<ProjectItemInstance, ProjectItemInstance> getSourceItem)
    {
        ItemListState? source = FindItemList(_scope.Parent, itemType);
        ItemListState selection = source is null
            ? new ItemListState()
            : new ItemListState(source, items, getSourceItem);
        SetItemList(itemType, selection);
        _scope.MarkPopulated(itemType);
        foreach (ProjectItemInstance item in items)
        {
            _scope.MapPopulatedItem(itemType, item, getSourceItem(item));
        }
    }

    internal void PopulateWithItem(ProjectItemInstance item, ProjectItemInstance sourceItem)
    {
        ItemListState? source = FindItemList(_scope.Parent, item.ItemType);
        ItemListState selection;
        if (!_scope.IsPopulated(item.ItemType))
        {
            selection = source is null
                ? new ItemListState()
                : new ItemListState(source, [], static selectedItem => selectedItem);
            SetItemList(item.ItemType, selection);
            _scope.MarkPopulated(item.ItemType);
        }
        else
        {
            selection = GetOrCreateItemList(item.ItemType);
        }

        _scope.MapPopulatedItem(item.ItemType, item, sourceItem);
        selection.Items.Remove(item);
        if (source?.Items.TryGetValue(sourceItem, out ItemState? itemState) == true)
        {
            selection.Items[item] = new ItemState(itemState);
        }
    }

    internal void AddConcreteItems(string itemType, IEnumerable<ProjectItemInstance> items)
    {
        ItemListState itemList = GetOrCreateItemList(itemType);
        foreach (ProjectItemInstance item in items)
        {
            itemList.Items[item] = new ItemState { IgnoreListMetadata = true };
        }

        _scope.MarkItemChanged(itemType);
    }

    internal void RemoveItems(
        ICollection<ProjectItemInstance> items,
        Func<ProjectItemInstance, ProjectItemInstance> getSourceItem)
    {
        foreach (ProjectItemInstance item in items)
        {
            ItemListState itemList = GetOrCreateItemList(item.ItemType);
            itemList.Items.Remove(item);
            ProjectItemInstance sourceItem = getSourceItem(item);
            itemList.Items.Remove(sourceItem);
            _scope.MarkItemRemoved(item.ItemType, sourceItem);
        }
    }

    internal void ApplyConcreteMetadataModifications(
        ICollection<ProjectItemInstance> items,
        Lookup.MetadataModifications metadataChanges,
        Func<ProjectItemInstance, ProjectItemInstance> getSourceItem)
    {
        foreach (ProjectItemInstance item in items)
        {
            ItemState itemState = GetOrCreateItemState(item);
            if (metadataChanges.KeepOnlySpecified)
            {
                itemState.IgnoreListMetadata = true;
                itemState.Metadata.Clear();
            }

            foreach (KeyValuePair<string, Lookup.MetadataModification> change in metadataChanges.ExplicitModifications)
            {
                if (!change.Value.KeepValue)
                {
                    itemState.Metadata[change.Key] = ValueState.Static;
                }
            }

            _scope.MarkItemChanged(item.ItemType);
            _scope.MapPopulatedItem(item.ItemType, item, getSourceItem(item));
        }
    }

    private void SetPropertyState(string propertyName, ValueState state)
    {
        _scope.Properties ??= new Dictionary<string, ValueState>(MSBuildNameIgnoreCaseComparer.Default);
        _scope.Properties[propertyName] = state;
    }

    private ItemState GetOrCreateItemState(ProjectItemInstance item)
    {
        ItemListState itemList = GetOrCreateItemList(item.ItemType);
        if (!itemList.Items.TryGetValue(item, out ItemState? itemState))
        {
            itemState = new ItemState();
            itemList.Items.Add(item, itemState);
        }

        return itemState;
    }

    private ItemListState GetOrCreateItemList(string itemType)
    {
        if (_scope.Items is not null &&
            _scope.Items.TryGetValue(itemType, out ItemListState? itemList))
        {
            return itemList;
        }

        ItemListState? inherited = FindItemList(itemType);
        itemList = inherited is null ? new ItemListState() : new ItemListState(inherited);
        SetItemList(itemType, itemList);
        return itemList;
    }

    private void SetItemList(string itemType, ItemListState itemList)
    {
        _scope.Items ??= new Dictionary<string, ItemListState>(MSBuildNameIgnoreCaseComparer.Default);
        _scope.Items[itemType] = itemList;
    }

    private ItemListState? FindItemList(string itemType)
        => FindItemList(_scope, itemType);

    private static ItemListState? FindItemList(Scope? startingScope, string itemType)
    {
        for (Scope? scope = startingScope; scope is not null; scope = scope.Parent)
        {
            if (scope.Items is not null &&
                scope.Items.TryGetValue(itemType, out ItemListState? itemList))
            {
                return itemList;
            }

            if (scope.TruncatedItemTypes?.Contains(itemType) == true)
            {
                return null;
            }
        }

        return null;
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

    private sealed class Scope(Scope? parent)
    {
        internal Scope? Parent { get; } = parent;

        internal Dictionary<string, ValueState>? Properties { get; set; }

        internal Dictionary<string, ItemListState>? Items { get; set; }

        internal HashSet<string>? TruncatedItemTypes { get; set; }

        private HashSet<string>? PopulatedItemTypes { get; set; }

        private HashSet<string>? ChangedItemTypes { get; set; }

        private HashSet<string>? ChangedItemListTypes { get; set; }

        private Dictionary<string, HashSet<ProjectItemInstance>>? RemovedItems { get; set; }

        private Dictionary<string, Dictionary<ProjectItemInstance, ProjectItemInstance>>? PopulatedItemSources { get; set; }

        internal bool IsPopulated(string itemType)
            => PopulatedItemTypes?.Contains(itemType) == true;

        internal void MarkPopulated(string itemType)
        {
            PopulatedItemTypes ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
            PopulatedItemTypes.Add(itemType);
        }

        internal void MarkItemChanged(string itemType)
        {
            ChangedItemTypes ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
            ChangedItemTypes.Add(itemType);
        }

        internal void MarkItemListChanged(string itemType)
        {
            ChangedItemListTypes ??= new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
            ChangedItemListTypes.Add(itemType);
        }

        internal void MarkItemRemoved(string itemType, ProjectItemInstance item)
        {
            RemovedItems ??= new Dictionary<string, HashSet<ProjectItemInstance>>(
                MSBuildNameIgnoreCaseComparer.Default);
            if (!RemovedItems.TryGetValue(itemType, out HashSet<ProjectItemInstance>? items))
            {
                items = [];
                RemovedItems.Add(itemType, items);
            }

            items.Add(item);
            MarkItemChanged(itemType);
        }

        internal void MapPopulatedItem(
            string itemType,
            ProjectItemInstance item,
            ProjectItemInstance sourceItem)
        {
            if (!IsPopulated(itemType))
            {
                return;
            }

            PopulatedItemSources ??=
                new Dictionary<string, Dictionary<ProjectItemInstance, ProjectItemInstance>>(
                    MSBuildNameIgnoreCaseComparer.Default);
            if (!PopulatedItemSources.TryGetValue(
                    itemType,
                    out Dictionary<ProjectItemInstance, ProjectItemInstance>? itemSources))
            {
                itemSources = [];
                PopulatedItemSources.Add(itemType, itemSources);
            }

            itemSources[item] = sourceItem;
        }

        internal Scope CloneChain()
        {
            Scope clone = new(Parent?.CloneChain());
            if (Properties is not null)
            {
                clone.Properties = new Dictionary<string, ValueState>(
                    Properties,
                    MSBuildNameIgnoreCaseComparer.Default);
            }

            if (Items is not null)
            {
                clone.Items = new Dictionary<string, ItemListState>(
                    Items.Count,
                    MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, ItemListState> item in Items)
                {
                    clone.Items.Add(item.Key, new ItemListState(item.Value));
                }
            }

            if (TruncatedItemTypes is not null)
            {
                clone.TruncatedItemTypes = new HashSet<string>(
                    TruncatedItemTypes,
                    MSBuildNameIgnoreCaseComparer.Default);
            }

            if (PopulatedItemTypes is not null)
            {
                clone.PopulatedItemTypes = new HashSet<string>(
                    PopulatedItemTypes,
                    MSBuildNameIgnoreCaseComparer.Default);
            }

            if (ChangedItemTypes is not null)
            {
                clone.ChangedItemTypes = new HashSet<string>(
                    ChangedItemTypes,
                    MSBuildNameIgnoreCaseComparer.Default);
            }

            if (ChangedItemListTypes is not null)
            {
                clone.ChangedItemListTypes = new HashSet<string>(
                    ChangedItemListTypes,
                    MSBuildNameIgnoreCaseComparer.Default);
            }

            if (RemovedItems is not null)
            {
                clone.RemovedItems = new Dictionary<string, HashSet<ProjectItemInstance>>(
                    RemovedItems.Count,
                    MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, HashSet<ProjectItemInstance>> removedItems in RemovedItems)
                {
                    clone.RemovedItems.Add(removedItems.Key, [.. removedItems.Value]);
                }
            }

            if (PopulatedItemSources is not null)
            {
                clone.PopulatedItemSources =
                    new Dictionary<string, Dictionary<ProjectItemInstance, ProjectItemInstance>>(
                        PopulatedItemSources.Count,
                        MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, Dictionary<ProjectItemInstance, ProjectItemInstance>> itemType in PopulatedItemSources)
                {
                    clone.PopulatedItemSources.Add(itemType.Key, new(itemType.Value));
                }
            }

            return clone;
        }

        internal void MergeInto(Scope destination)
        {
            if (Properties is not null)
            {
                destination.Properties ??= new Dictionary<string, ValueState>(
                    MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, ValueState> property in Properties)
                {
                    if (property.Value.IsStatic && destination.Parent is null)
                    {
                        destination.Properties.Remove(property.Key);
                    }
                    else
                    {
                        destination.Properties[property.Key] = property.Value;
                    }
                }
            }

            if (Items is not null)
            {
                destination.Items ??= new Dictionary<string, ItemListState>(
                    MSBuildNameIgnoreCaseComparer.Default);
                foreach (KeyValuePair<string, ItemListState> item in Items)
                {
                    if (IsPopulated(item.Key))
                    {
                        MergePopulatedItemState(destination, item.Key, item.Value);
                    }
                    else if (ChangedItemTypes?.Contains(item.Key) == true ||
                             ChangedItemListTypes?.Contains(item.Key) == true)
                    {
                        if (item.Value.IsStatic && destination.Parent is null)
                        {
                            destination.Items.Remove(item.Key);
                        }
                        else
                        {
                            destination.Items[item.Key] = item.Value;
                        }
                    }
                }
            }

            PropagateChangeTracking(destination);
        }

        private void MergePopulatedItemState(
            Scope destination,
            string itemType,
            ItemListState source)
        {
            bool itemStateChanged = ChangedItemTypes?.Contains(itemType) == true;
            bool listStateChanged = ChangedItemListTypes?.Contains(itemType) == true;
            if (!itemStateChanged && !listStateChanged)
            {
                return;
            }

            ItemListState? inherited = FindItemList(destination, itemType);
            ItemListState merged = inherited is null
                ? new ItemListState()
                : new ItemListState(inherited);

            if (listStateChanged)
            {
                merged.Membership = source.Membership;
                merged.DefaultMetadata = source.DefaultMetadata;
                merged.Metadata.Clear();
                foreach (KeyValuePair<string, ValueState> metadata in source.Metadata)
                {
                    merged.Metadata.Add(metadata.Key, metadata.Value);
                }
            }

            if (itemStateChanged)
            {
                foreach (KeyValuePair<ProjectItemInstance, ItemState> item in source.Items)
                {
                    ProjectItemInstance destinationItem =
                        PopulatedItemSources?.TryGetValue(
                            itemType,
                            out Dictionary<ProjectItemInstance, ProjectItemInstance>? itemSources) == true &&
                        itemSources.TryGetValue(item.Key, out ProjectItemInstance? sourceItem)
                            ? sourceItem
                            : item.Key;
                    merged.Items[destinationItem] = new ItemState(item.Value);
                }

                if (RemovedItems?.TryGetValue(itemType, out HashSet<ProjectItemInstance>? removedItems) == true)
                {
                    foreach (ProjectItemInstance removedItem in removedItems)
                    {
                        merged.Items.Remove(removedItem);
                    }
                }
            }

            if (merged.IsStatic && destination.Parent is null)
            {
                destination.Items!.Remove(itemType);
            }
            else
            {
                destination.Items![itemType] = merged;
            }
        }

        private void PropagateChangeTracking(Scope destination)
        {
            if (ChangedItemTypes is not null)
            {
                foreach (string itemType in ChangedItemTypes)
                {
                    destination.MarkItemChanged(itemType);
                }
            }

            if (ChangedItemListTypes is not null)
            {
                foreach (string itemType in ChangedItemListTypes)
                {
                    destination.MarkItemListChanged(itemType);
                }
            }

            if (RemovedItems is not null)
            {
                foreach (KeyValuePair<string, HashSet<ProjectItemInstance>> removedItems in RemovedItems)
                {
                    foreach (ProjectItemInstance removedItem in removedItems.Value)
                    {
                        destination.MarkItemRemoved(removedItems.Key, removedItem);
                    }
                }
            }
        }
    }

    private sealed class ItemListState
    {
        internal ItemListState()
        {
        }

        internal ItemListState(ItemListState other)
        {
            Membership = other.Membership;
            DefaultMetadata = other.DefaultMetadata;
            foreach (KeyValuePair<string, ValueState> metadata in other.Metadata)
            {
                Metadata.Add(metadata.Key, metadata.Value);
            }

            foreach (KeyValuePair<ProjectItemInstance, ItemState> item in other.Items)
            {
                Items.Add(item.Key, new ItemState(item.Value));
            }
        }

        internal ItemListState(
            ItemListState source,
            ICollection<ProjectItemInstance> selectedItems,
            Func<ProjectItemInstance, ProjectItemInstance> getSourceItem)
        {
            Membership = source.Membership;
            DefaultMetadata = source.DefaultMetadata;
            foreach (KeyValuePair<string, ValueState> metadata in source.Metadata)
            {
                Metadata.Add(metadata.Key, metadata.Value);
            }

            foreach (ProjectItemInstance selectedItem in selectedItems)
            {
                ProjectItemInstance sourceItem = getSourceItem(selectedItem);
                if (source.Items.TryGetValue(sourceItem, out ItemState? itemState))
                {
                    Items.Add(selectedItem, new ItemState(itemState));
                }
            }
        }

        internal ValueState Membership { get; set; } = ValueState.Static;

        internal ValueState DefaultMetadata { get; set; } = ValueState.Static;

        internal Dictionary<string, ValueState> Metadata { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal Dictionary<ProjectItemInstance, ItemState> Items { get; } = [];

        internal bool IsStatic
        {
            get
            {
                if (!Membership.IsStatic || !DefaultMetadata.IsStatic)
                {
                    return false;
                }

                foreach (ValueState metadata in Metadata.Values)
                {
                    if (!metadata.IsStatic)
                    {
                        return false;
                    }
                }

                foreach (ItemState item in Items.Values)
                {
                    if (!item.IsStatic)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    private sealed class ItemState
    {
        internal ItemState()
        {
        }

        internal ItemState(ItemState other)
        {
            Identity = other.Identity;
            IgnoreListMetadata = other.IgnoreListMetadata;
            foreach (KeyValuePair<string, ValueState> metadata in other.Metadata)
            {
                Metadata.Add(metadata.Key, metadata.Value);
            }
        }

        internal ValueState Identity { get; set; } = ValueState.Static;

        internal bool IgnoreListMetadata { get; set; }

        internal Dictionary<string, ValueState> Metadata { get; } =
            new(MSBuildNameIgnoreCaseComparer.Default);

        internal bool IsStatic
        {
            get
            {
                if (!Identity.IsStatic || IgnoreListMetadata)
                {
                    return false;
                }

                foreach (ValueState metadata in Metadata.Values)
                {
                    if (!metadata.IsStatic)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
