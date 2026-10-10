// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Evaluation;

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// Diagnostic trace of each evaluation's condition and property-element stream and its final property
/// table, so the workload can be replayed against reference implementations.
/// </summary>
/// <remarks>
/// Enabled by setting <c>MSBUILDEVALUATIONTRACE</c> to an existing directory. Each thread appends to
/// <c>trace.{pid}.{thread}.tsv</c>, flushed at the end of every evaluation. Records are tab-separated,
/// with backslash, tab, CR, and LF escaped:
/// <list type="bullet">
/// <item><c>E project</c> starts an evaluation.</item>
/// <item><c>S pass</c> marks a pass: P (properties and imports), D (item definitions), I (items), U (using tasks), T (targets).</item>
/// <item><c>C result condition</c> is one condition evaluation (result 0 or 1).</item>
/// <item><c>P name unevaluated evaluated</c> is one applied property element; the value is escaped as stored.</item>
/// <item><c>F name value</c> is one entry of the final property table; <c>X</c> ends the evaluation.</item>
/// </list>
/// Evaluations do not nest on a thread; one that ends by an exception is closed by the next
/// <see cref="BeginEvaluation"/> on that thread.
/// </remarks>
internal static class EvaluationTrace
{
    internal const string EnvironmentVariable = "MSBUILDEVALUATIONTRACE";

    private static readonly string? s_directory = Environment.GetEnvironmentVariable(EnvironmentVariable);

    [ThreadStatic]
    private static StreamWriter? t_writer;

    [ThreadStatic]
    private static bool t_active;

    internal static bool Enabled { get; } = !string.IsNullOrEmpty(s_directory) && Directory.Exists(s_directory);

    /// <summary>
    /// Whether the current thread is inside a traced evaluation.
    /// </summary>
    internal static bool Active => Enabled && t_active;

    internal static void BeginEvaluation(string projectFile)
    {
        if (!Enabled)
        {
            return;
        }

        if (t_active)
        {
            t_writer!.Write("X\n");
        }

        t_active = true;
        t_writer ??= new StreamWriter(
            Path.Combine(s_directory!, $"trace.{System.Diagnostics.Process.GetCurrentProcess().Id}.{Environment.CurrentManagedThreadId}.tsv"),
            append: true,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1 << 16);
        Write('E', projectFile);
    }

    internal static void Pass(char pass)
    {
        if (Active)
        {
            t_writer!.Write('S');
            t_writer.Write('\t');
            t_writer.Write(pass);
            t_writer.Write('\n');
        }
    }

    internal static void Condition(string condition, bool result)
    {
        if (Active)
        {
            Write('C', result ? "1" : "0", condition);
        }
    }

    internal static void PropertyElement(string name, string unevaluatedValue, string evaluatedValueEscaped)
    {
        if (Active)
        {
            Write('P', name, unevaluatedValue, evaluatedValueEscaped);
        }
    }

    internal static void EndEvaluation<P>(IEnumerable<P> finalProperties)
        where P : class, IProperty
    {
        if (!Active)
        {
            return;
        }

        t_active = false;
        foreach (P property in finalProperties)
        {
            Write('F', property.Name, property.EvaluatedValueEscaped);
        }

        t_writer!.Write("X\n");
        t_writer.Flush();
    }

    private static void Write(char kind, string a, string? b = null, string? c = null)
    {
        StreamWriter writer = t_writer!;
        writer.Write(kind);
        WriteField(writer, a);
        if (b is not null)
        {
            WriteField(writer, b);
        }

        if (c is not null)
        {
            WriteField(writer, c);
        }

        writer.Write('\n');
    }

    private static void WriteField(StreamWriter writer, string value)
    {
        writer.Write('\t');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\': writer.Write("\\\\"); break;
                case '\t': writer.Write("\\t"); break;
                case '\n': writer.Write("\\n"); break;
                case '\r': writer.Write("\\r"); break;
                default: writer.Write(ch); break;
            }
        }
    }
}
