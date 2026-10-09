// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal readonly struct PropertyId : IEquatable<PropertyId>
    {
        internal PropertyId(int value)
        {
            Value = value;
        }

        internal int Value { get; }

        public bool Equals(PropertyId other) => Value == other.Value;

        public override bool Equals(object obj) =>
            obj is PropertyId other && Equals(other);

        public override int GetHashCode() => Value;
    }

    internal sealed class PropertyIdentityTable
    {
        private readonly ConcurrentDictionary<string, PropertyId> _ids =
            new ConcurrentDictionary<string, PropertyId>(
                StringComparer.OrdinalIgnoreCase);
        private int _nextId;

        internal PropertyId GetOrCreate(string name)
        {
            if (_ids.TryGetValue(name, out PropertyId existing))
            {
                return existing;
            }

            var candidate = new PropertyId(
                Interlocked.Increment(ref _nextId));
            return _ids.GetOrAdd(name, candidate);
        }

        internal bool TryGetId(string name, out PropertyId id) =>
            _ids.TryGetValue(name, out id);
    }

    internal readonly struct SourceId
    {
        internal SourceId(int moduleHandle, int elementId)
        {
            ModuleHandle = moduleHandle;
            ElementId = elementId;
        }

        internal int ModuleHandle { get; }

        internal int ElementId { get; }
    }

    [Flags]
    internal enum PropertyFlags : byte
    {
        None = 0,
    }

    internal readonly struct PropertyValueRef
    {
        internal PropertyValueRef(
            string escapedValue,
            SourceId source,
            PropertyFlags flags)
        {
            EscapedValue = escapedValue;
            Source = source;
            Flags = flags;
        }

        internal string EscapedValue { get; }

        internal SourceId Source { get; }

        internal PropertyFlags Flags { get; }
    }

    internal readonly struct PropertyDeltaEntry
    {
        internal PropertyDeltaEntry(
            PropertyId id,
            string name,
            PropertyValueRef value)
        {
            Id = id;
            Name = name;
            Value = value;
        }

        internal PropertyId Id { get; }

        internal string Name { get; }

        internal PropertyValueRef Value { get; }
    }

    internal sealed class PropertyDelta
    {
        internal PropertyDelta(PropertyDeltaEntry[] entries)
        {
            Entries = ImmutableArray.Create(entries);
        }

        internal ImmutableArray<PropertyDeltaEntry> Entries { get; }
    }

    internal sealed class PropertyPrefixState
    {
        private readonly Dictionary<PropertyId, PropertyDeltaEntry>
            _entriesById;

        internal PropertyPrefixState(
            Dictionary<PropertyId, PropertyValueRef> values,
            Dictionary<PropertyId, string> names)
        {
            _entriesById =
                new Dictionary<PropertyId, PropertyDeltaEntry>(
                    values.Count);
            foreach (KeyValuePair<PropertyId, PropertyValueRef> pair in values)
            {
                _entriesById.Add(pair.Key, new PropertyDeltaEntry(
                    pair.Key,
                    names[pair.Key],
                    pair.Value));
            }

            Count = values.Count;
        }

        internal int Count { get; }

        internal bool TryGet(
            PropertyId id,
            out PropertyValueRef value)
        {
            if (_entriesById.TryGetValue(
                    id,
                    out PropertyDeltaEntry entry))
            {
                value = entry.Value;
                return true;
            }

            value = default;
            return false;
        }

        internal string GetName(PropertyId id) =>
            _entriesById[id].Name;

        internal Dictionary<PropertyId, PropertyDeltaEntry>.ValueCollection
            Entries =>
            _entriesById.Values;
    }

    internal sealed class ConstantPropertySegmentState
    {
        private PropertyDelta _constantEffects;

        internal PropertyDelta GetConstantEffects(
            EvaluationModule module,
            TableRange properties)
        {
            PropertyDelta effects = Volatile.Read(ref _constantEffects);
            if (effects is not null)
            {
                return effects;
            }

            effects = module.CreateConstantPropertyDelta(properties);
            return Interlocked.CompareExchange(
                       ref _constantEffects,
                       effects,
                       null) ??
                   effects;
        }
    }

    internal sealed class CompactPropertyEnvironment
    {
        private readonly PropertyIdentityTable _identities;
        private readonly Dictionary<PropertyId, PropertyValueRef> _values =
            new Dictionary<PropertyId, PropertyValueRef>();
        private readonly Dictionary<PropertyId, string> _names =
            new Dictionary<PropertyId, string>();
        private readonly HashSet<PropertyId> _hiddenBaseValues =
            new HashSet<PropertyId>();
        private PropertyPrefixState _baseState;

        internal CompactPropertyEnvironment(PropertyIdentityTable identities)
        {
            _identities = identities;
        }

        internal int Count =>
            _values.Count +
            (_baseState?.Count ?? 0) -
            _hiddenBaseValues.Count;

        internal string GetName(PropertyId id) =>
            _names.TryGetValue(id, out string name)
                ? name
                : _baseState.GetName(id);

        internal void Apply(PropertyDelta delta)
        {
            foreach (PropertyDeltaEntry entry in delta.Entries)
            {
                Set(entry.Id, entry.Name, entry.Value);
            }
        }

        internal void Set(
            PropertyId id,
            string name,
            PropertyValueRef value)
        {
            if (_baseState?.TryGet(id, out _) == true)
            {
                _hiddenBaseValues.Add(id);
            }

            _values[id] = value;
            _names[id] = name;
        }

        internal bool TryGet(PropertyId id, out PropertyValueRef value)
        {
            if (_values.TryGetValue(id, out value))
            {
                return true;
            }

            if (!_hiddenBaseValues.Contains(id) &&
                _baseState?.TryGet(id, out value) == true)
            {
                return true;
            }

            value = default;
            return false;
        }

        internal bool TryGet(string name, out PropertyValueRef value)
        {
            if (_identities.TryGetId(name, out PropertyId id))
            {
                return TryGet(id, out value);
            }

            value = default;
            return false;
        }

        internal bool TryGet(
            string name,
            out PropertyId id,
            out PropertyValueRef value)
        {
            if (_identities.TryGetId(name, out id))
            {
                return TryGet(id, out value);
            }

            value = default;
            return false;
        }

        internal bool Remove(PropertyId id)
        {
            _names.Remove(id);
            bool removed = _values.Remove(id);
            if (_baseState?.TryGet(id, out _) == true)
            {
                removed |= _hiddenBaseValues.Add(id);
            }

            return removed;
        }

        internal bool Remove(string name)
        {
            return _identities.TryGetId(name, out PropertyId id) &&
                   Remove(id);
        }

        internal bool TryCreatePrefixState(
            out PropertyPrefixState state)
        {
            if (_baseState is not null ||
                _hiddenBaseValues.Count != 0)
            {
                state = null;
                return false;
            }

            state = new PropertyPrefixState(_values, _names);
            return true;
        }

        internal bool TryAttachPrefixState(
            PropertyPrefixState state)
        {
            if (_baseState is not null ||
                _values.Count != 0 ||
                _hiddenBaseValues.Count != 0)
            {
                return false;
            }

            _baseState = state;
            return true;
        }

        internal PropertyDeltaEntry[] Drain()
        {
            var entries = new PropertyDeltaEntry[Count];
            int index = 0;
            foreach (KeyValuePair<PropertyId, PropertyValueRef> pair in _values)
            {
                entries[index++] =
                    new PropertyDeltaEntry(
                        pair.Key,
                        _names[pair.Key],
                        pair.Value);
            }

            if (_baseState is not null)
            {
                foreach (PropertyDeltaEntry entry in
                         _baseState.Entries)
                {
                    if (!_hiddenBaseValues.Contains(entry.Id))
                    {
                        entries[index++] = entry;
                    }
                }
            }

            _values.Clear();
            _names.Clear();
            _hiddenBaseValues.Clear();
            _baseState = null;
            return entries;
        }
    }
}
