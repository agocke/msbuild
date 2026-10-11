// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// Diagnostic per-file cost profile of the properties and imports pass.
/// </summary>
/// <remarks>
/// Enabled by setting <c>MSBUILDIMPORTCOSTPROFILE</c> to an output path prefix together with the cost
/// ledger (<c>MSBUILDCOSTLEDGER</c>), whose per-thread counters it reads. Each process writes
/// <c>{prefix}.{pid}.tsv</c> at exit with one row per project or import file: executions, inclusive
/// and exclusive counters for the file's subtree in the properties pass, and the cost of resolving and
/// loading the file's own imports (included in its exclusive cost).
/// </remarks>
internal static class ImportCostProfile
{
    internal const string EnvironmentVariable = "MSBUILDIMPORTCOSTPROFILE";

    internal const string ProjectKey = "<project>";

    private static readonly string? s_prefix = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private static readonly Dictionary<string, Totals> s_totals = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static List<Frame>? t_stack;

    internal static bool Enabled { get; } = !string.IsNullOrEmpty(s_prefix) && CostLedger.Enabled;

    static ImportCostProfile()
    {
        if (!string.IsNullOrEmpty(s_prefix))
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
        }
    }

    internal static void Enter(string path)
    {
        if (!Enabled)
        {
            return;
        }

        t_stack ??= [];
        if (path == ProjectKey)
        {
            // A failed evaluation can leave frames behind; each project starts a fresh stack.
            t_stack.Clear();
        }

        t_stack.Add(new Frame(path, CostLedger.ReadCurrentThread()));
    }

    internal static void Exit()
    {
        List<Frame>? stack = t_stack;
        if (!Enabled || stack is null || stack.Count == 0)
        {
            return;
        }

        Frame frame = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        CostLedger.Snapshot inclusive = CostLedger.ReadCurrentThread() - frame.Start;
        if (stack.Count > 0)
        {
            Frame parent = stack[^1];
            parent.Children += inclusive;
            stack[^1] = parent;
        }

        lock (s_totals)
        {
            if (!s_totals.TryGetValue(frame.Path, out Totals? totals))
            {
                totals = new Totals();
                s_totals.Add(frame.Path, totals);
            }

            totals.Count++;
            totals.Inclusive += inclusive;
            totals.Exclusive += inclusive - frame.Children;
            totals.Resolution += frame.Resolution;
        }
    }

    /// <summary>
    /// Starts timing the resolution and loading of an import element in the current file.
    /// </summary>
    internal static void BeginResolution()
    {
        List<Frame>? stack = t_stack;
        if (!Enabled || stack is null || stack.Count == 0)
        {
            return;
        }

        Frame frame = stack[^1];
        frame.ResolutionStart = CostLedger.ReadCurrentThread();
        stack[^1] = frame;
    }

    internal static void EndResolution()
    {
        List<Frame>? stack = t_stack;
        if (!Enabled || stack is null || stack.Count == 0)
        {
            return;
        }

        Frame frame = stack[^1];
        frame.Resolution += CostLedger.ReadCurrentThread() - frame.ResolutionStart;
        stack[^1] = frame;
    }

    private static void Dump()
    {
        if (!Enabled)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append("# counters=").Append(CostLedger.Mode).AppendLine();
        builder.AppendLine("path\tcount\tincl_instructions\texcl_instructions\tresolution_instructions\tincl_task_ms\texcl_task_ms\tresolution_task_ms");
        lock (s_totals)
        {
            foreach (KeyValuePair<string, Totals> pair in s_totals)
            {
                Totals t = pair.Value;
                builder.Append(pair.Key)
                    .Append('\t').Append(t.Count)
                    .Append('\t').Append(t.Inclusive.Instructions)
                    .Append('\t').Append(t.Exclusive.Instructions)
                    .Append('\t').Append(t.Resolution.Instructions)
                    .Append('\t').Append(Milliseconds(t.Inclusive.TaskNanoseconds))
                    .Append('\t').Append(Milliseconds(t.Exclusive.TaskNanoseconds))
                    .Append('\t').Append(Milliseconds(t.Resolution.TaskNanoseconds))
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

    private static string Milliseconds(long nanoseconds) => (nanoseconds / 1e6).ToString("F3", CultureInfo.InvariantCulture);

    private struct Frame(string path, CostLedger.Snapshot start)
    {
        internal readonly string Path = path;
        internal readonly CostLedger.Snapshot Start = start;
        internal CostLedger.Snapshot Children;
        internal CostLedger.Snapshot Resolution;
        internal CostLedger.Snapshot ResolutionStart;
    }

    private sealed class Totals
    {
        internal long Count;
        internal CostLedger.Snapshot Inclusive;
        internal CostLedger.Snapshot Exclusive;
        internal CostLedger.Snapshot Resolution;
    }
}
#else
namespace Microsoft.Build.Instrumentation;

/// <summary>
/// The import cost profile relies on the cost ledger and is inactive on .NET Framework.
/// </summary>
internal static class ImportCostProfile
{
    internal const string ProjectKey = "<project>";

    internal static bool Enabled => false;

    internal static void Enter(string path)
    {
    }

    internal static void Exit()
    {
    }

    internal static void BeginResolution()
    {
    }

    internal static void EndResolution()
    {
    }
}
#endif
