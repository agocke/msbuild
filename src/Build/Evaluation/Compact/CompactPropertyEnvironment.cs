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

        internal CompactPropertyEnvironment(PropertyIdentityTable identities)
        {
            _identities = identities;
        }

        internal int Count => _values.Count;

        internal string GetName(PropertyId id) => _names[id];

        internal void Apply(PropertyDelta delta)
        {
            foreach (PropertyDeltaEntry entry in delta.Entries)
            {
                _values[entry.Id] = entry.Value;
                _names[entry.Id] = entry.Name;
            }
        }

        internal void Set(
            PropertyId id,
            string name,
            PropertyValueRef value)
        {
            _values[id] = value;
            _names[id] = name;
        }

        internal bool TryGet(PropertyId id, out PropertyValueRef value) =>
            _values.TryGetValue(id, out value);

        internal bool TryGet(string name, out PropertyValueRef value)
        {
            if (_identities.TryGetId(name, out PropertyId id))
            {
                return _values.TryGetValue(id, out value);
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
                return _values.TryGetValue(id, out value);
            }

            value = default;
            return false;
        }

        internal bool Remove(PropertyId id)
        {
            _names.Remove(id);
            return _values.Remove(id);
        }

        internal bool Remove(string name)
        {
            return _identities.TryGetId(name, out PropertyId id) &&
                   Remove(id);
        }

        internal PropertyDeltaEntry[] Drain()
        {
            var entries = new PropertyDeltaEntry[_values.Count];
            int index = 0;
            foreach (KeyValuePair<PropertyId, PropertyValueRef> pair in _values)
            {
                entries[index++] =
                    new PropertyDeltaEntry(
                        pair.Key,
                        _names[pair.Key],
                        pair.Value);
            }

            _values.Clear();
            _names.Clear();
            return entries;
        }
    }
}
