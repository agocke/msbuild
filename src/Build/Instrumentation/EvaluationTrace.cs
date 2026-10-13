// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Construction;
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
/// <item><c>I path</c> and <c>J</c> enter and leave an imported file during the properties and imports pass.</item>
/// <item><c>R name hash</c> is a property read during that pass: a stable 64-bit FNV-1a hash of the escaped
/// value in hexadecimal, or <c>-</c> when the property is undefined.</item>
/// <item><c>B location</c> and <c>K</c> enter and leave a property group or choose block in that pass.</item>
/// <item><c>O location kind type prior directory</c> and <c>Z posterior</c> enter and leave one item operation
/// application; prior and posterior hash the operation's item list. Inside it, <c>R</c> records property
/// reads and <c>Q type hash</c> records reads of other item lists.</item>
/// <item><c>F name value</c> is one entry of the final property table; <c>X</c> ends the evaluation.</item>
/// </list>
/// With <c>MSBUILDEVALUATIONTRACEITEMS=1</c>, the items pass also records, for replaying item operations:
/// <list type="bullet">
/// <item><c>D type name value</c>: one metadata value of a final item definition, before the first operation.</item>
/// <item><c>T location type include exclude update remove matchOnMetadata matchOnMetadataOptions keepMetadata removeMetadata keepDuplicates</c>
/// and one <c>H location name value condition</c> per metadata element: an operation's source, once per thread.</item>
/// <item><c>Y ordinal</c> after <c>O</c>: the operation's position in document order within the evaluation.</item>
/// <item>Before <c>Z</c>, <c>A count</c> (rows an include appended) or <c>L count</c> (the whole list after an
/// update or remove), each followed by <c>N include (name value)*</c> rows with all metadata the item reports.</item>
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

    [ThreadStatic]
    private static char t_pass;

    [ThreadStatic]
    private static int t_operationDepth;

    [ThreadStatic]
    private static HashSet<string>? t_itemSites;


    internal static bool Enabled { get; } = !string.IsNullOrEmpty(s_directory) && Directory.Exists(s_directory);

    /// <summary>
    /// Whether item operations also record their sources and result rows (<c>MSBUILDEVALUATIONTRACEITEMS=1</c>).
    /// </summary>
    internal static bool ItemDetail { get; } = Enabled && Environment.GetEnvironmentVariable(ItemDetailEnvironmentVariable) == "1";

    internal const string ItemDetailEnvironmentVariable = "MSBUILDEVALUATIONTRACEITEMS";

    /// <summary>
    /// Whether the current thread is inside a traced evaluation.
    /// </summary>
    internal static bool Active => Enabled && t_active;

    internal static bool ItemDetailActive => ItemDetail && t_active;

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
        t_pass = '\0';
        t_operationDepth = 0;
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
            t_pass = pass;
            t_writer!.Write('S');
            t_writer.Write('\t');
            t_writer.Write(pass);
            t_writer.Write('\n');
        }
    }

    internal static void ImportEnter(string path)
    {
        if (Active)
        {
            Write('I', path);
        }
    }

    internal static void ImportExit()
    {
        if (Active)
        {
            t_writer!.Write("J\n");
        }
    }

    /// <summary>
    /// Records a property read during the properties and imports pass, where import live-ins are formed.
    /// </summary>
    internal static void PropertyRead(string name, string? escapedValue)
    {
        if (Active && (t_pass == 'P' || t_operationDepth > 0))
        {
            Write('R', name, escapedValue is null ? "-" : Hash(escapedValue));
        }
    }

    internal static void BlockEnter(ElementLocation location)
    {
        if (Active && t_pass == 'P')
        {
            Write('B', $"{location.File}:{location.Line}:{location.Column}");
        }
    }

    internal static void BlockExit()
    {
        if (Active && t_pass == 'P')
        {
            t_writer!.Write("K\n");
        }
    }

    internal static bool InOperation => Active && t_operationDepth > 0;

    internal static void OperationEnter(ElementLocation location, string kind, string itemType, string priorHash, string directory)
    {
        if (Active)
        {
            t_operationDepth++;
            t_writer!.Write('O');
            WriteField(t_writer, $"{location.File}:{location.Line}:{location.Column}");
            WriteField(t_writer, kind);
            WriteField(t_writer, itemType);
            WriteField(t_writer, priorHash);
            WriteField(t_writer, directory);
            t_writer.Write('\n');
        }
    }

    internal static void OperationExit(string posteriorHash)
    {
        if (Active && t_operationDepth > 0)
        {
            t_operationDepth--;
            Write('Z', posteriorHash);
        }
    }

    internal static void ItemsRead(string itemType, string hash)
    {
        if (InOperation)
        {
            Write('Q', itemType, hash);
        }
    }

    private static string Hash(string value) => new Hasher().Add(value).ToString();

    /// <summary>
    /// Stable 64-bit FNV-1a hash for trace records, so hashes compare across processes.
    /// </summary>
    internal struct Hasher
    {
        private ulong _hash;

        public Hasher Add(string? value)
        {
            if (_hash == 0)
            {
                _hash = 14695981039346656037;
            }

            if (value is not null)
            {
                foreach (char ch in value)
                {
                    _hash = (_hash ^ ch) * 1099511628211;
                }
            }

            // Separator so ("ab", "c") and ("a", "bc") differ.
            _hash = (_hash ^ 0xFFFF) * 1099511628211;
            return this;
        }

        public override readonly string ToString() => _hash.ToString("x16");
    }

    /// <summary>
    /// Records one metadata value of a final item definition at the start of the items pass.
    /// </summary>
    internal static void ItemDefinition(string itemType, string name, string escapedValue)
    {
        if (ItemDetailActive)
        {
            Write('D', itemType, name, escapedValue);
        }
    }

    /// <summary>
    /// Records an item operation's source the first time this thread applies an operation from it, then the
    /// operation's position in document order.
    /// </summary>
    internal static void ItemOperationSource(ProjectItemElement element, string site, int ordinal)
    {
        if (!ItemDetailActive)
        {
            return;
        }

        t_itemSites ??= new HashSet<string>(StringComparer.Ordinal);
        if (t_itemSites.Add(site))
        {
            StreamWriter writer = t_writer!;
            writer.Write('T');
            WriteField(writer, site);
            WriteField(writer, element.ItemType);
            WriteField(writer, element.Include);
            WriteField(writer, element.Exclude);
            WriteField(writer, element.Update);
            WriteField(writer, element.Remove);
            WriteField(writer, element.MatchOnMetadata);
            WriteField(writer, element.MatchOnMetadataOptions);
            WriteField(writer, element.KeepMetadata);
            WriteField(writer, element.RemoveMetadata);
            WriteField(writer, element.KeepDuplicates);
            writer.Write('\n');
            foreach (ProjectMetadataElement metadata in element.Metadata)
            {
                writer.Write('H');
                WriteField(writer, site);
                WriteField(writer, metadata.Name);
                WriteField(writer, metadata.Value);
                WriteField(writer, metadata.Condition);
                writer.Write('\n');
            }
        }

        Write('Y', ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Starts the rows of an operation's result: the full list after an update or remove (<c>L count</c>),
    /// or only the rows an include appended (<c>A count</c>).
    /// </summary>
    internal static void ItemRowsStart(bool appendedOnly, int count)
    {
        if (ItemDetailActive)
        {
            Write(appendedOnly ? 'A' : 'L', count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Starts one result row, <c>N include (name value)*</c>; metadata pairs follow through <see cref="ItemRowMetadata"/>.
    /// </summary>
    internal static void ItemRowStart(string escapedInclude)
    {
        if (ItemDetailActive)
        {
            t_writer!.Write('N');
            WriteField(t_writer, escapedInclude);
        }
    }

    internal static void ItemRowMetadata(string name, string escapedValue)
    {
        if (ItemDetailActive)
        {
            WriteField(t_writer!, name);
            WriteField(t_writer!, escapedValue);
        }
    }

    internal static void ItemRowEnd()
    {
        if (ItemDetailActive)
        {
            t_writer!.Write('\n');
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
