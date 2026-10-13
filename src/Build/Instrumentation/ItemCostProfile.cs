// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Build.Construction;

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// Diagnostic per-site cost profile of item operations in the items pass.
/// </summary>
/// <remarks>
/// Enabled by setting <c>MSBUILDITEMCOSTPROFILE</c> to an output path prefix together with the cost ledger
/// (<c>MSBUILDCOSTLEDGER</c>), whose per-thread counters it reads. Each process writes <c>{prefix}.{pid}.tsv</c>
/// at exit with one row per item element: how often its operation was constructed (condition evaluation and
/// operation construction) and applied, and the instructions of each, excluding operations nested in them
/// (an item list read by a condition or an item reference applies that list's operations).
/// </remarks>
internal static class ItemCostProfile
{
    internal const string EnvironmentVariable = "MSBUILDITEMCOSTPROFILE";

    private static readonly string? s_prefix = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Dictionary<(string File, int Line, int Column), Totals> s_totals = [];

    [ThreadStatic]
    private static List<long>? t_nested;

    internal static bool Enabled { get; } = !string.IsNullOrEmpty(s_prefix) && CostLedger.Enabled;

    static ItemCostProfile()
    {
        if (!string.IsNullOrEmpty(s_prefix))
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
        }
    }

    /// <summary>
    /// Starts measuring a construction or application; returns the current instruction count.
    /// </summary>
    internal static long Start()
    {
        (t_nested ??= []).Add(0);
        return CostLedger.ReadCurrentThread().Instructions;
    }

    /// <summary>
    /// Ends a measurement started by <see cref="Start"/> and charges it to the element's site. The kind is C for
    /// a construction whose operation was created, F for an element whose condition was false, and A for an application.
    /// </summary>
    internal static void End(ElementLocation location, string operationKind, char kind, long start)
    {
        long end = CostLedger.ReadCurrentThread().Instructions;
        List<long> nested = t_nested!;
        long exclusive = end - start - nested[^1];
        nested.RemoveAt(nested.Count - 1);

        lock (s_totals)
        {
            (string, int, int) key = (location.File, location.Line, location.Column);
            if (!s_totals.TryGetValue(key, out Totals? totals))
            {
                totals = new Totals { Kind = operationKind };
                s_totals.Add(key, totals);
            }

            switch (kind)
            {
                case 'C':
                    totals.Constructions++;
                    totals.ConstructionInstructions += exclusive;
                    break;
                case 'F':
                    totals.FalseConditions++;
                    totals.FalseConditionInstructions += exclusive;
                    break;
                default:
                    totals.Applications++;
                    totals.ApplicationInstructions += exclusive;
                    break;
            }
        }

        if (nested.Count > 0)
        {
            // The parent excludes everything this measurement spent, bookkeeping included.
            nested[^1] += CostLedger.ReadCurrentThread().Instructions - start;
        }
    }

    private static void Dump()
    {
        if (!Enabled)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append("# counters=").Append(CostLedger.Mode).AppendLine();
        builder.AppendLine("site\tkind\tconstructions\tconstruction_instructions\tfalse_conditions\tfalse_condition_instructions\tapplications\tapplication_instructions");
        lock (s_totals)
        {
            foreach (KeyValuePair<(string File, int Line, int Column), Totals> pair in s_totals)
            {
                Totals t = pair.Value;
                builder.Append(pair.Key.File).Append(':').Append(pair.Key.Line.ToString(CultureInfo.InvariantCulture))
                    .Append(':').Append(pair.Key.Column.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(t.Kind)
                    .Append('\t').Append(t.Constructions)
                    .Append('\t').Append(t.ConstructionInstructions)
                    .Append('\t').Append(t.FalseConditions)
                    .Append('\t').Append(t.FalseConditionInstructions)
                    .Append('\t').Append(t.Applications)
                    .Append('\t').Append(t.ApplicationInstructions)
                    .AppendLine();
            }
        }

        try
        {
            File.WriteAllText($"{s_prefix}.{Environment.ProcessId}.tsv", builder.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never fail the build.
        }
    }

    private sealed class Totals
    {
        internal string Kind = "";
        internal long Constructions;
        internal long ConstructionInstructions;
        internal long FalseConditions;
        internal long FalseConditionInstructions;
        internal long Applications;
        internal long ApplicationInstructions;
    }
}
#else
using Microsoft.Build.Construction;

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// The item cost profile relies on the cost ledger and is inactive on .NET Framework.
/// </summary>
internal static class ItemCostProfile
{
    internal static bool Enabled => false;

    internal static long Start() => 0;

    internal static void End(ElementLocation location, string operationKind, char kind, long start)
    {
    }
}
#endif
