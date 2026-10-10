// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;

namespace Microsoft.Build.Evaluation;

/// <summary>
/// Process-wide cache of property-group effects in the properties and imports pass, keyed by the group's
/// XML element, the global-property status of its properties, and the values of the properties it reads
/// before writing them (its live-ins).
/// </summary>
/// <remarks>
/// <para>
/// Evaluating an eligible group is deterministic in those inputs: it uses only <c>$(Name)</c> references
/// and simple conditions (no property functions, item or metadata references, or <c>Exists</c>), and it sees
/// no backslashes, which would make the Unix path heuristic consult the filesystem. Two evaluations with
/// equal live-in values therefore perform the same reads in the same order and the same writes. The first
/// evaluation of each distinct vector records its writes; later evaluations replay them through the same
/// <c>SetProperty</c> calls, which preserves property objects, reassignment logging, and write notifications.
/// </para>
/// <para>
/// Experimental and off by default (<c>MSBUILDATTACHPROPERTYGROUPS=1</c>). The evaluator falls back to
/// ordinary evaluation whenever read-side observers are active. Entries live as long as their XML elements
/// and are discarded when the containing project's version changes.
/// </para>
/// </remarks>
internal static class PropertyGroupEffectCache
{
    internal const string StatisticsEnvironmentVariable = "MSBUILDATTACHPROPERTYGROUPSSTATS";

    /// <summary>
    /// When set to 1, every hit is also evaluated normally and compared with the recorded effect.
    /// </summary>
    internal static readonly bool Verify = Environment.GetEnvironmentVariable("MSBUILDATTACHPROPERTYGROUPSVERIFY") == "1";

    private const int MaxNameSets = 8;
    private const int MaxVariantsPerNameSet = 1024;
    private const int MaxElements = 64;

    private static readonly ConditionalWeakTable<ProjectPropertyGroupElement, Entry> s_entries = new();

    private static long s_hits;
    private static long s_misses;
    private static long s_uncacheable;
    private static long s_ineligible;
    private static long s_attachedWrites;
    private static long s_recordedVariants;
    private static long s_verified;
    private static long s_verifyMismatches;

    static PropertyGroupEffectCache()
    {
        string? path = Environment.GetEnvironmentVariable(StatisticsEnvironmentVariable);
        if (!string.IsNullOrEmpty(path))
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    File.WriteAllText($"{path}.{System.Diagnostics.Process.GetCurrentProcess().Id}.txt", FormatStatistics());
                }
                catch (IOException)
                {
                    // Diagnostics must never fail the build.
                }
            };
        }
    }

    internal static Entry GetEntry(ProjectPropertyGroupElement group) =>
        s_entries.GetValue(group, static g => new Entry(g));

    internal static void CountIneligible() => Interlocked.Increment(ref s_ineligible);

    internal static void CountUncacheable() => Interlocked.Increment(ref s_uncacheable);

    internal static void CountHit(int writes)
    {
        Interlocked.Increment(ref s_hits);
        Interlocked.Add(ref s_attachedWrites, writes);
    }

    internal static void CountMiss() => Interlocked.Increment(ref s_misses);

    internal static string FormatStatistics() =>
        $"hits={Interlocked.Read(ref s_hits)} misses={Interlocked.Read(ref s_misses)} " +
        $"uncacheable={Interlocked.Read(ref s_uncacheable)} ineligible={Interlocked.Read(ref s_ineligible)} " +
        $"attached_writes={Interlocked.Read(ref s_attachedWrites)} recorded_variants={Interlocked.Read(ref s_recordedVariants)} " +
        $"verified={Interlocked.Read(ref s_verified)} verify_mismatches={Interlocked.Read(ref s_verifyMismatches)}\n";

    /// <summary>
    /// Compares a recorded effect with the outcomes of an ordinary evaluation of the same group.
    /// </summary>
    internal static void CheckVerification(Operation[] recorded, Recorder actual)
    {
        Interlocked.Increment(ref s_verified);
        bool same = recorded.Length == actual.Operations.Count;
        for (int i = 0; same && i < recorded.Length; i++)
        {
            same = ReferenceEquals(recorded[i].Element, actual.Operations[i].Element) &&
                string.Equals(recorded[i].Value, actual.Operations[i].Value, StringComparison.Ordinal);
        }

        if (!same)
        {
            Interlocked.Increment(ref s_verifyMismatches);
        }
    }

    /// <summary>
    /// Whether a property value or condition uses only <c>$(Name)</c> references and no constructs whose
    /// result depends on anything but property values.
    /// </summary>
    internal static bool IsDeterministicInProperties(string text, bool isCondition)
    {
        if (text.IndexOf('\\') >= 0)
        {
            return false;
        }

        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if ((c == '@' || c == '%') && i + 1 < text.Length && text[i + 1] == '(')
            {
                return false;
            }

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                int end = ScanName(text, i + 2);
                if (end < 0 || end >= text.Length || text[end] != ')')
                {
                    return false; // property function, registry reference, or malformed reference
                }

                i = end;
                continue;
            }

            // Outside quotes, an identifier followed by '(' is a condition function such as Exists.
            if (isCondition && !inQuotes && IsNameStart(c) && (i == 0 || !IsNameChar(text[i - 1])))
            {
                int end = ScanName(text, i);
                int next = end;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && text[next] == '(' && !IsKeyword(text, i, end - i))
                {
                    return false;
                }

                i = end - 1;
            }
        }

        return true;
    }

    private static bool IsKeyword(string text, int start, int length) =>
        (length == 3 && string.Compare(text, start, "and", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) ||
        (length == 2 && string.Compare(text, start, "or", 0, 2, StringComparison.OrdinalIgnoreCase) == 0);

    private static int ScanName(string s, int start)
    {
        int i = start;
        if (i >= s.Length || !IsNameStart(s[i]))
        {
            return -1;
        }

        while (i < s.Length && IsNameChar(s[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsNameStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    private static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9') || c == '-';

    /// <summary>
    /// One recorded property-element outcome: a write of <see cref="Value"/>, or, when it is null, a skip
    /// because the property is a non-overridable global property.
    /// </summary>
    internal readonly record struct Operation(ProjectPropertyElement Element, string? Value);

    /// <summary>
    /// A recorded effect. A null <see cref="Operations"/> marks a vector whose evaluation is not cacheable.
    /// </summary>
    internal sealed record Variant(Operation[]? Operations);

    internal enum LookupResult
    {
        Miss,
        Hit,
        Uncacheable,
    }

    /// <summary>
    /// Collects a group evaluation's live-in reads and outcomes.
    /// </summary>
    internal sealed class Recorder
    {
        private readonly HashSet<string> _seen = new(MSBuildNameIgnoreCaseComparer.Default);
        private readonly HashSet<string> _written = new(MSBuildNameIgnoreCaseComparer.Default);

        internal List<string> Names { get; } = new();

        internal List<string?> Values { get; } = new();

        internal List<Operation> Operations { get; } = new();

        internal bool Uncacheable { get; private set; }

        internal void OnRead(string name, string? escapedValue)
        {
            if (_written.Contains(name) || !_seen.Add(name))
            {
                return;
            }

            Names.Add(name);
            Values.Add(escapedValue);
            if (escapedValue is not null && escapedValue.IndexOf('\\') >= 0)
            {
                Uncacheable = true;
            }
        }

        internal void OnWrite(ProjectPropertyElement element, string escapedValue)
        {
            _written.Add(element.Name);
            Operations.Add(new Operation(element, escapedValue));
        }

        internal void OnSkippedGlobal(ProjectPropertyElement element) => Operations.Add(new Operation(element, null));
    }

    internal sealed class Entry
    {
        private readonly ProjectPropertyGroupElement _group;
        private readonly object _lock = new();
        private int _version;
        private ProjectPropertyElement[]? _elements;
        private NameSet[] _sets = [];

        internal Entry(ProjectPropertyGroupElement group)
        {
            _group = group;
            Reset();
        }

        /// <summary>
        /// The group's property elements if the group is eligible for attachment, otherwise null.
        /// </summary>
        internal ProjectPropertyElement[]? Elements
        {
            get
            {
                if (_group.ContainingProject?.Version != Volatile.Read(ref _version))
                {
                    lock (_lock)
                    {
                        Reset();
                    }
                }

                return _elements;
            }
        }

        internal LookupResult Find(Func<string, string?> readEscapedValue, ulong globalMask, out Variant? variant)
        {
            foreach (NameSet set in Volatile.Read(ref _sets))
            {
                string?[] values = new string?[set.Names.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = readEscapedValue(set.Names[i]);
                }

                if (set.Variants.TryGetValue(new LiveInKey(values, globalMask), out variant))
                {
                    return variant.Operations is null ? LookupResult.Uncacheable : LookupResult.Hit;
                }
            }

            variant = null;
            return LookupResult.Miss;
        }

        internal void Add(Recorder recorder, ulong globalMask)
        {
            string[] names = recorder.Names.ToArray();
            var key = new LiveInKey(recorder.Values.ToArray(), globalMask);
            var variant = new Variant(recorder.Uncacheable ? null : recorder.Operations.ToArray());
            lock (_lock)
            {
                NameSet? set = null;
                foreach (NameSet candidate in _sets)
                {
                    if (candidate.Matches(names))
                    {
                        set = candidate;
                        break;
                    }
                }

                if (set is null)
                {
                    if (_sets.Length >= MaxNameSets)
                    {
                        return;
                    }

                    set = new NameSet(names);
                    _sets = [.. _sets, set];
                }

                if (set.Variants.Count < MaxVariantsPerNameSet && set.Variants.TryAdd(key, variant))
                {
                    Interlocked.Increment(ref s_recordedVariants);
                }
            }
        }

        private void Reset()
        {
            _version = _group.ContainingProject?.Version ?? 0;
            _sets = [];
            _elements = null;
            if (!IsDeterministicInProperties(_group.Condition, isCondition: true))
            {
                return;
            }

            var elements = new List<ProjectPropertyElement>();
            foreach (ProjectPropertyElement element in _group.Properties)
            {
                if (elements.Count == MaxElements ||
                    !IsDeterministicInProperties(element.Condition, isCondition: true) ||
                    !IsDeterministicInProperties(element.Value, isCondition: false))
                {
                    return;
                }

                elements.Add(element);
            }

            _elements = elements.ToArray();
        }
    }

    private sealed class NameSet(string[] names)
    {
        internal string[] Names { get; } = names;

        internal ConcurrentDictionary<LiveInKey, Variant> Variants { get; } = new();

        internal bool Matches(string[] names)
        {
            if (names.Length != Names.Length)
            {
                return false;
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (!MSBuildNameIgnoreCaseComparer.Default.Equals(names[i], Names[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Live-in values (escaped; null when undefined) plus the global-property mask, compared ordinally.
    /// </summary>
    private readonly struct LiveInKey : IEquatable<LiveInKey>
    {
        private readonly string?[] _values;
        private readonly ulong _globalMask;
        private readonly int _hash;

        internal LiveInKey(string?[] values, ulong globalMask)
        {
            _values = values;
            _globalMask = globalMask;
            int hash = globalMask.GetHashCode();
            foreach (string? value in values)
            {
                hash = unchecked((hash * 31) + (value is null ? -1 : StringComparer.Ordinal.GetHashCode(value)));
            }

            _hash = hash;
        }

        public bool Equals(LiveInKey other)
        {
            if (_hash != other._hash || _globalMask != other._globalMask || _values.Length != other._values.Length)
            {
                return false;
            }

            for (int i = 0; i < _values.Length; i++)
            {
                if (!string.Equals(_values[i], other._values[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is LiveInKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }
}
