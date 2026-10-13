// Reference ("floor") implementation of MSBuild's recorded item operations.
//
// Replays an MSBUILDEVALUATIONTRACE directory recorded with MSBUILDEVALUATIONTRACEITEMS=1, evaluation by
// evaluation. Every item operation is applied in document order to compact, configuration-owned state: per
// item type an ordered list of value rows (include, shared immutable metadata block, defining element).
// Operation sources are prepared once: item specs and metadata values become literal/property/metadata
// templates, conditions become trees, and all-literal metadata becomes one shared block. Each application
// then expands its spec, copies referenced rows, matches update and remove specs against full paths, and
// runs its metadata program once for all rows when it reads no item metadata, otherwise row by row.
//
// Inputs come from the trace: final properties (the items pass sees final values), final item definitions,
// and recorded results. A validation pass compares every operation's rows with the recorded ones; operations
// whose shape is unsupported (globs, transforms, property functions, matching options) or whose result
// disagrees are applied from the recorded rows instead, and are reported as not covered.
//
// usage: Floor items <trace-dir> [item-cost-profile.tsv] [repetitions]

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

internal static class ItemFloor
{
    public static int Run(string[] args)
    {
        string dir = args[0];
        string? profile = args.Length > 1 && args[1].Length > 0 ? args[1] : null;
        int reps = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;

        var sw = Stopwatch.StartNew();
        var w = ItemWorkload.Load(dir);
        Console.WriteLine($"# loaded {w.Evaluations.Count} evaluations, {w.OperationCount} operations, {w.Sites.Count} sites in {sw.Elapsed.TotalSeconds:F1}s");

        using var counters = new Counters();
        Snapshot before = counters.Read();
        w.Prepare();
        Snapshot prep = counters.Read() - before;
        Console.WriteLine($"# static preparation: {prep.Instructions / 1e6:F1}M {Counters.Unit} for {w.Sites.Count} sites");
        foreach ((string reason, int count) in w.UnsupportedReasons.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"#   unsupported site shape: {reason,-28} {count,6} sites");
        }

        w.BindInputs();
        var replayer = new ItemReplayer(w);
        replayer.Validate();
        Console.WriteLine($"# validation: {replayer.Checked} operations checked, {replayer.Mismatched} disagreed with the recorded rows");
        foreach ((Site site, int n) in w.Sites.Values.Where(s => s.Mismatches > 0).Select(s => (s, s.Mismatches)).OrderByDescending(p => p.Item2).Take(30))
        {
            Console.WriteLine($"#   mismatch {n,6}  {site.Kind,-16} {Short(site.Key)}  {site.FirstMismatch}");
        }

        long covered = w.Evaluations.Sum(e => e.Operations.Count(o => !o.Fallback));
        long coveredRows = w.Evaluations.Sum(e => e.Operations.Where(o => !o.Fallback).Sum(o => (long)o.Rows.Count));
        long allRows = w.Evaluations.Sum(e => e.Operations.Sum(o => (long)o.Rows.Count));
        Console.WriteLine($"# coverage: {covered * 100.0 / w.OperationCount:F1}% of operations, {coveredRows * 100.0 / Math.Max(allRows, 1):F1}% of recorded rows");

        // Whole-workload cost: best of the repetitions after a warm-up.
        Snapshot best = default;
        for (int i = 0; i < reps + 1; i++)
        {
            Snapshot start = counters.Read();
            replayer.Replay(profileSites: null);
            Snapshot delta = counters.Read() - start;
            if (i > 0 && (best.Instructions == 0 || delta.Instructions < best.Instructions))
            {
                best = delta;
            }
        }

        // Per-site cost: one replay with counters read around every operation, minus the read overhead.
        long readCost = CalibrateRead(counters);
        replayer.Replay(profileSites: counters, readCost);

        string unit = Counters.Unit;
        long floorCovered = w.Sites.Values.Sum(s => s.FloorCovered);
        long floorFallback = w.Sites.Values.Sum(s => s.FloorFallback);
        Console.WriteLine();
        Console.WriteLine($"[items] replay of all operations   {best.Instructions / 1e9,7:F3}G {unit}{(Counters.HasCycles ? $"  IPC {best.Instructions / (double)best.Cycles:F2}" : "")}");
        Console.WriteLine($"[items]   covered operations       {floorCovered / 1e9,7:F3}G  {floorCovered / (double)Math.Max(covered, 1),9:F0} per operation");
        Console.WriteLine($"[items]   recorded-row fallback    {floorFallback / 1e9,7:F3}G  {floorFallback / (double)Math.Max(w.OperationCount - covered, 1),9:F0} per operation");
        Console.WriteLine($"[items] item-pass element conditions {w.Evaluations.Sum(e => (long)e.ElementConditions)} (priced by the condition floor, not replayed)");

        if (profile is not null)
        {
            Compare(w, profile);
        }

        return 0;
    }

    private static long CalibrateRead(Counters counters)
    {
        long best = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            Snapshot a = counters.Read();
            for (int i = 0; i < 1000; i++)
            {
                _ = counters.Read();
            }

            Snapshot b = counters.Read();
            best = Math.Min(best, (b.Instructions - a.Instructions) / 1001);
        }

        return best;
    }

    /// <summary>
    /// Joins per-site floor costs with MSBuild's per-site item cost profile (MSBUILDITEMCOSTPROFILE).
    /// </summary>
    private static void Compare(ItemWorkload w, string path)
    {
        var msbuild = new Dictionary<string, (long Constructions, long Construction, long Applications, long Application)>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("site\t", StringComparison.Ordinal))
            {
                continue;
            }

            string[] f = line.Split('\t');
            msbuild[NormalizeSite(f[0])] = (long.Parse(f[2], CultureInfo.InvariantCulture), long.Parse(f[3], CultureInfo.InvariantCulture),
                long.Parse(f[6], CultureInfo.InvariantCulture), long.Parse(f[7], CultureInfo.InvariantCulture));
        }

        long msAll = 0, msCovered = 0, floorCovered = 0, opsCovered = 0;
        var rows = new List<(Site Site, double MsPerOp, double FloorPerOp, long Ms, int Ops)>();
        foreach (Site site in w.Sites.Values)
        {
            if (!msbuild.TryGetValue(NormalizeSite(site.Key), out var m) || m.Applications == 0)
            {
                continue;
            }

            double msPerOp = (m.Construction + m.Application) / (double)m.Applications;
            msAll += m.Construction + m.Application;
            if (site.CoveredApplications > 0)
            {
                msCovered += (long)(msPerOp * site.CoveredApplications);
                floorCovered += site.FloorCovered;
                opsCovered += site.CoveredApplications;
                rows.Add((site, msPerOp, site.FloorCovered / (double)site.CoveredApplications, (long)(msPerOp * site.CoveredApplications), site.CoveredApplications));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[compare] MSBuild per-site construction + application, all sites: {msAll / 1e9:F2}G");
        Console.WriteLine($"[compare] covered operations: {opsCovered}, MSBuild {msCovered / 1e9:F2}G ({msCovered * 100.0 / Math.Max(msAll, 1):F1}% of its item work), floor {floorCovered / 1e9:F3}G, ratio {msCovered / (double)Math.Max(floorCovered, 1):F1}x");
        foreach (string kind in new[] { "IncludeOperation", "UpdateOperation", "RemoveOperation" })
        {
            var k = rows.Where(r => r.Site.Kind == kind).ToList();
            long ms = k.Sum(r => r.Ms);
            long fl = k.Sum(r => r.Site.FloorCovered);
            int ops = k.Sum(r => r.Ops);
            Console.WriteLine($"[compare]   {kind,-16} {ops,8} ops  MSBuild {ms / (double)Math.Max(ops, 1),9:F0}  floor {fl / (double)Math.Max(ops, 1),7:F0} per operation  ({ms / (double)Math.Max(fl, 1):F1}x)");
        }

        Console.WriteLine();
        Console.WriteLine($"{"MSBuild G",9} {"ops",6} {"MSBuild/op",11} {"floor/op",9} {"ratio",6}  site");
        foreach (var r in rows.OrderByDescending(r => r.Ms).Take(25))
        {
            Console.WriteLine($"{r.Ms / 1e9,9:F2} {r.Ops,6} {r.MsPerOp,11:F0} {r.FloorPerOp,9:F0} {r.MsPerOp / Math.Max(r.FloorPerOp, 1),6:F1}  {r.Site.Kind[..^9],-7} {Short(r.Site.Key)}");
        }

        var uncovered = w.Sites.Values
            .Select(s => (Site: s, Ms: msbuild.TryGetValue(NormalizeSite(s.Key), out var m) && m.Applications > 0
                ? (long)((m.Construction + m.Application) / (double)m.Applications * (s.Applications - s.CoveredApplications)) : 0))
            .Where(p => p.Ms > 0).OrderByDescending(p => p.Ms).Take(10).ToList();
        Console.WriteLine();
        Console.WriteLine($"[compare] largest MSBuild work not covered: {uncovered.Sum(p => p.Ms) / 1e9:F2}G in the top 10");
        foreach (var (site, ms) in uncovered)
        {
            Console.WriteLine($"{ms / 1e9,9:F2}  {site.Kind[..^9],-7} {site.Unsupported ?? (site.Mismatches > 0 ? "mismatch" : "partly covered"),-24} {Short(site.Key)}");
        }
    }

    // SDK snapshot directories differ between the trace and profile runs.
    private static string NormalizeSite(string site)
    {
        int i = site.IndexOf("/sdks/", StringComparison.Ordinal);
        if (i < 0)
        {
            return site;
        }

        int j = site.IndexOf('/', i + 6);
        return j < 0 ? site : site[..i] + "/sdks/<sdk>" + site[j..];
    }

    internal static string Short(string site) => site.Length <= 80 ? site : "..." + site[^77..];
}

/// <summary>Case-insensitive name table (MSBuild item types, metadata, and property names).</summary>
internal sealed class NameTable
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Names = [];

    public int Intern(string name)
    {
        if (!_ids.TryGetValue(name, out int id))
        {
            id = Names.Count;
            _ids.Add(name, id);
            Names.Add(name);
        }

        return id;
    }

    public int Count => Names.Count;
}

internal sealed class RecordedRow(string include, string[] pairs)
{
    public readonly string Include = include;
    public readonly string[] Pairs = pairs;   // name, value, name, value, ...
    public MetaBlock? Block;                  // the recorded metadata as a block, built during binding
}

internal sealed class Operation
{
    public string SiteKey = "";
    public Site Site = null!;
    public int Ordinal;
    public bool Appended;
    public List<RecordedRow> Rows = [];
    public bool Fallback;
    public Row[]? Recorded;                   // rows for applying from the recording
}

internal sealed class ItemEvaluation
{
    public string Directory = "";
    public readonly List<(string Name, string Value)> Final = [];
    public readonly List<(string Type, string Name, string Value)> Definitions = [];
    public readonly Dictionary<int, Operation> ByOrdinal = [];
    public Operation[] Operations = [];
    public int ElementConditions;
    public string[] Properties = [];          // indexed by referenced-property id
    public MetaBlock?[] DefinitionBlocks = []; // indexed by item type id
}

internal sealed class ItemWorkload
{
    public readonly List<ItemEvaluation> Evaluations = [];
    public readonly Dictionary<string, Site> Sites = new(StringComparer.Ordinal);
    public readonly NameTable Types = new(), Metadata = new(), Properties = new();
    public readonly Dictionary<string, int> UnsupportedReasons = [];
    public long OperationCount;
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    private string S(string s) => s.Length == 0 ? "" : _strings.TryGetValue(s, out string? v) ? v : _strings[s] = s;

    public static ItemWorkload Load(string dir)
    {
        var w = new ItemWorkload();
        foreach (string path in System.IO.Directory.GetFiles(dir, "trace.*.tsv").Order(StringComparer.Ordinal))
        {
            w.LoadFile(path);
        }

        foreach (ItemEvaluation e in w.Evaluations)
        {
            foreach (Operation op in e.Operations)
            {
                op.Site = w.Sites[op.SiteKey];
                op.Site.Applications++;
            }

            w.OperationCount += e.Operations.Length;
        }

        return w;
    }

    private void LoadFile(string path)
    {
        ItemEvaluation? e = null;
        char pass = '\0';
        var stack = new Stack<Operation>();
        Site? metadataTarget = null;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            char kind = line[0];
            switch (kind)
            {
                case 'E':
                    e = new ItemEvaluation();
                    pass = '\0';
                    stack.Clear();
                    break;
                case 'S':
                    pass = line[2];
                    break;
                case 'C':
                    if (e is not null && pass == 'I' && stack.Count == 0)
                    {
                        e.ElementConditions++;
                    }

                    break;
                case 'F':
                    if (e is not null)
                    {
                        string[] f = Fields(line);
                        e.Final.Add((f[1], S(f[2])));
                    }

                    break;
                case 'D':
                    if (e is not null)
                    {
                        string[] f = Fields(line);
                        e.Definitions.Add((S(f[1]), S(f[2]), S(f[3])));
                    }

                    break;
                case 'T':
                {
                    string[] f = Fields(line);
                    metadataTarget = null;
                    if (!Sites.ContainsKey(f[1]))
                    {
                        var site = new Site
                        {
                            Key = f[1], TypeName = f[2], IncludeText = f[3], ExcludeText = f[4], UpdateText = f[5], RemoveText = f[6],
                            MatchOnMetadata = f[7], MatchOnMetadataOptions = f[8], KeepMetadata = f[9], RemoveMetadata = f[10], KeepDuplicates = f[11],
                        };
                        Sites.Add(site.Key, site);
                        metadataTarget = site;
                    }

                    break;
                }

                case 'H':
                    if (metadataTarget is not null)
                    {
                        string[] f = Fields(line);
                        metadataTarget.RawMetadata.Add((f[2], f[3], f[4]));
                    }

                    break;
                case 'O':
                    if (e is not null)
                    {
                        string[] f = Fields(line);
                        var op = new Operation { SiteKey = S(f[1]) };
                        _ = f[2];
                        if (e.Directory.Length == 0)
                        {
                            e.Directory = f[5];
                        }

                        stack.Push(op);
                    }

                    break;
                case 'Y':
                    if (stack.Count > 0)
                    {
                        stack.Peek().Ordinal = int.Parse(line.AsSpan(2), CultureInfo.InvariantCulture);
                    }

                    break;
                case 'A':
                case 'L':
                    if (stack.Count > 0)
                    {
                        Operation op = stack.Peek();
                        op.Appended = kind == 'A';
                        op.Rows = new List<RecordedRow>(int.Parse(line.AsSpan(2), CultureInfo.InvariantCulture));
                    }

                    break;
                case 'N':
                    if (stack.Count > 0)
                    {
                        string[] f = Fields(line);
                        var pairs = new string[f.Length - 2];
                        for (int i = 2; i < f.Length; i++)
                        {
                            pairs[i - 2] = S(f[i]);
                        }

                        stack.Peek().Rows.Add(new RecordedRow(S(f[1]), pairs));
                    }

                    break;
                case 'Z':
                    if (e is not null && stack.Count > 0)
                    {
                        Operation op = stack.Pop();
                        e.ByOrdinal[op.Ordinal] = op;   // a reapplied operation keeps its last result
                    }

                    break;
                case 'X':
                    if (e is not null)
                    {
                        e.Operations = e.ByOrdinal.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
                        e.ByOrdinal.Clear();
                        Evaluations.Add(e);
                        e = null;
                    }

                    break;
            }
        }
    }

    private static string[] Fields(string line)
    {
        string[] f = line.Split('\t');
        for (int i = 1; i < f.Length; i++)
        {
            if (f[i].Contains('\\'))
            {
                f[i] = Unescape(f[i]);
            }
        }

        return f;
    }

    private static string Unescape(string s)
    {
        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                b.Append(s[i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => s[i] });
            }
            else
            {
                b.Append(s[i]);
            }
        }

        return b.ToString();
    }

    /// <summary>Static preparation: parse every site's spec, exclude, and metadata program once.</summary>
    public void Prepare()
    {
        foreach (Site site in Sites.Values)
        {
            site.Prepare(this);
            if (site.Unsupported is not null)
            {
                UnsupportedReasons[site.Unsupported] = UnsupportedReasons.GetValueOrDefault(site.Unsupported) + 1;
            }
        }
    }

    /// <summary>Binds each evaluation's inputs (final properties and item definitions) to prepared ids.</summary>
    public void BindInputs()
    {
        var propertyIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Properties.Count; i++)
        {
            propertyIds[Properties.Names[i]] = i;
        }

        foreach (ItemEvaluation e in Evaluations)
        {
            e.Properties = new string[Properties.Count];
            Array.Fill(e.Properties, "");
            foreach ((string name, string value) in e.Final)
            {
                if (propertyIds.TryGetValue(name, out int id))
                {
                    e.Properties[id] = value;
                }
            }

            e.DefinitionBlocks = new MetaBlock?[Types.Count];
            foreach (var group in e.Definitions.GroupBy(d => Types.Intern(d.Type)))
            {
                var frame = new Frame();
                foreach (var d in group)
                {
                    frame.Set(Metadata.Intern(d.Name), d.Value);
                }

                if (group.Key < e.DefinitionBlocks.Length)
                {
                    e.DefinitionBlocks[group.Key] = frame.ToBlock();
                }
            }

            foreach (Operation op in e.Operations)
            {
                foreach (RecordedRow row in op.Rows)
                {
                    var frame = new Frame();
                    for (int i = 0; i + 1 < row.Pairs.Length; i += 2)
                    {
                        frame.Set(Metadata.Intern(row.Pairs[i]), row.Pairs[i + 1]);
                    }

                    row.Block = frame.Count == 0 ? null : frame.ToBlock();
                }
            }

            e.Final.Clear();
            e.Definitions.Clear();
        }
    }
}

/// <summary>An immutable metadata block: names sorted by id, shared between rows and copies.</summary>
internal sealed class MetaBlock(int[] names, string[] values)
{
    public readonly int[] Names = names;
    public readonly string[] Values = values;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Get(int name)
    {
        int[] names = Names;
        if (names.Length <= 8)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == name)
                {
                    return Values[i];
                }
            }

            return null;
        }

        int index = Array.BinarySearch(names, name);
        return index >= 0 ? Values[index] : null;
    }

    /// <summary>Union of two blocks; the overlay wins.</summary>
    public static MetaBlock Merge(MetaBlock? under, MetaBlock over)
    {
        if (under is null || under.Names.Length == 0)
        {
            return over;
        }

        int[] a = under.Names, b = over.Names;
        var names = new int[a.Length + b.Length];
        var values = new string[a.Length + b.Length];
        int i = 0, j = 0, k = 0;
        while (i < a.Length || j < b.Length)
        {
            if (j == b.Length || (i < a.Length && a[i] < b[j]))
            {
                names[k] = a[i];
                values[k++] = under.Values[i++];
            }
            else
            {
                if (i < a.Length && a[i] == b[j])
                {
                    i++;
                }

                names[k] = b[j];
                values[k++] = over.Values[j++];
            }
        }

        if (k < names.Length)
        {
            Array.Resize(ref names, k);
            Array.Resize(ref values, k);
        }

        return new MetaBlock(names, values);
    }
}

/// <summary>A working metadata frame for one operation and row; later writes replace earlier ones.</summary>
internal sealed class Frame
{
    private int[] _names = new int[8];
    private string[] _values = new string[8];
    public int Count;

    public void Clear() => Count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? Get(int name)
    {
        for (int i = 0; i < Count; i++)
        {
            if (_names[i] == name)
            {
                return _values[i];
            }
        }

        return null;
    }

    public void Set(int name, string value)
    {
        for (int i = 0; i < Count; i++)
        {
            if (_names[i] == name)
            {
                _values[i] = value;
                return;
            }
        }

        if (Count == _names.Length)
        {
            Array.Resize(ref _names, Count * 2);
            Array.Resize(ref _values, Count * 2);
        }

        _names[Count] = name;
        _values[Count++] = value;
    }

    public MetaBlock ToBlock()
    {
        var names = _names.AsSpan(0, Count).ToArray();
        var values = _values.AsSpan(0, Count).ToArray();
        Array.Sort(names, values);
        return new MetaBlock(names, values);
    }
}

/// <summary>One item occurrence: escaped include, direct metadata, and the element that defined it.</summary>
internal struct Row(string include, MetaBlock? meta, Site origin)
{
    public string Include = include;
    public MetaBlock? Meta = meta;
    public Site Origin = origin;
    public string? FullPath;
}

internal enum RefKind : byte
{
    Property,
    Metadata,
    BuiltIn,
    FileProperty,   // MSBuildThisFile*: scoped to the file that contains the element
}

internal enum FileProperty : byte
{
    File,
    Directory,
    DirectoryNoRoot,
    Extension,
    FullPath,
    Name,
}

internal enum BuiltIn : byte
{
    Identity,
    Filename,
    Extension,
    FullPath,
    RootDir,
    Directory,
    RelativeDir,
    RecursiveDir,
    DefiningProjectFullPath,
    DefiningProjectDirectory,
    DefiningProjectName,
    DefiningProjectExtension,
}

internal readonly record struct TemplateRef(RefKind Kind, int Id, int Qualifier);

/// <summary>Literal text interleaved with $(property), %(metadata), and built-in metadata references.</summary>
internal sealed class ItemTemplate(string[] literals, TemplateRef[] refs)
{
    public readonly string[] Literals = literals;
    public readonly TemplateRef[] Refs = refs;
    public bool UsesMetadata => Refs.Any(r => r.Kind is RefKind.Metadata or RefKind.BuiltIn);
    public bool IsLiteral => Refs.Length == 0;

    private static readonly Dictionary<string, BuiltIn> s_builtIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Identity"] = BuiltIn.Identity, ["Filename"] = BuiltIn.Filename, ["Extension"] = BuiltIn.Extension,
        ["FullPath"] = BuiltIn.FullPath, ["RootDir"] = BuiltIn.RootDir, ["Directory"] = BuiltIn.Directory,
        ["RelativeDir"] = BuiltIn.RelativeDir, ["RecursiveDir"] = BuiltIn.RecursiveDir,
        ["DefiningProjectFullPath"] = BuiltIn.DefiningProjectFullPath, ["DefiningProjectDirectory"] = BuiltIn.DefiningProjectDirectory,
        ["DefiningProjectName"] = BuiltIn.DefiningProjectName, ["DefiningProjectExtension"] = BuiltIn.DefiningProjectExtension,
    };

    private static readonly Dictionary<string, FileProperty> s_fileProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSBuildThisFile"] = FileProperty.File, ["MSBuildThisFileDirectory"] = FileProperty.Directory,
        ["MSBuildThisFileDirectoryNoRoot"] = FileProperty.DirectoryNoRoot, ["MSBuildThisFileExtension"] = FileProperty.Extension,
        ["MSBuildThisFileFullPath"] = FileProperty.FullPath, ["MSBuildThisFileName"] = FileProperty.Name,
    };

    private static readonly HashSet<string> s_unsupportedBuiltIns = new(StringComparer.OrdinalIgnoreCase) { "ModifiedTime", "CreatedTime", "AccessedTime" };

    /// <summary>Parses a template; returns null with a reason for syntax the floor does not implement.</summary>
    public static ItemTemplate? Parse(string text, ItemWorkload w, bool allowMetadata, out string? reason)
    {
        reason = null;
        var literals = new List<string>();
        var refs = new List<TemplateRef>();
        var current = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '(')
            {
                reason = "item list in value";
                return null;
            }

            if ((c == '$' || c == '%') && i + 1 < text.Length && text[i + 1] == '(')
            {
                int close = text.IndexOf(')', i + 2);
                if (close < 0)
                {
                    reason = "unterminated reference";
                    return null;
                }

                string inner = text[(i + 2)..close];
                if (c == '$')
                {
                    if (inner.Length == 0 || !IsName(inner))
                    {
                        reason = "property function";
                        return null;
                    }

                    literals.Add(current.ToString());
                    current.Clear();
                    refs.Add(s_fileProperties.TryGetValue(inner, out FileProperty fp)
                        ? new TemplateRef(RefKind.FileProperty, (int)fp, -1)
                        : new TemplateRef(RefKind.Property, w.Properties.Intern(inner), -1));
                }
                else
                {
                    if (!allowMetadata)
                    {
                        reason = "metadata in item spec";
                        return null;
                    }

                    int dot = inner.IndexOf('.');
                    string name = dot < 0 ? inner : inner[(dot + 1)..];
                    string? qualifier = dot < 0 ? null : inner[..dot];
                    if (!IsName(name) || (qualifier is not null && !IsName(qualifier)))
                    {
                        reason = "metadata expression";
                        return null;
                    }

                    if (s_unsupportedBuiltIns.Contains(name))
                    {
                        reason = "file time metadata";
                        return null;
                    }

                    literals.Add(current.ToString());
                    current.Clear();
                    int q = qualifier is null ? -1 : w.Types.Intern(qualifier);
                    refs.Add(s_builtIns.TryGetValue(name, out BuiltIn b)
                        ? new TemplateRef(RefKind.BuiltIn, (int)b, q)
                        : new TemplateRef(RefKind.Metadata, w.Metadata.Intern(name), q));
                }

                i = close + 1;
                continue;
            }

            current.Append(c);
            i++;
        }

        literals.Add(current.ToString());
        return new ItemTemplate([.. literals], [.. refs]);
    }

    private static bool IsName(string s)
    {
        if (s.Length == 0 || !(char.IsAsciiLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

internal abstract class CondNode
{
    public abstract bool Evaluate(ItemReplayer r, ref Row row);
}

internal sealed class AndCond(CondNode a, CondNode b) : CondNode
{
    public override bool Evaluate(ItemReplayer r, ref Row row) => a.Evaluate(r, ref row) && b.Evaluate(r, ref row);
}

internal sealed class OrCond(CondNode a, CondNode b) : CondNode
{
    public override bool Evaluate(ItemReplayer r, ref Row row) => a.Evaluate(r, ref row) || b.Evaluate(r, ref row);
}

internal sealed class NotCond(CondNode a) : CondNode
{
    public override bool Evaluate(ItemReplayer r, ref Row row) => !a.Evaluate(r, ref row);
}

internal sealed class TruthCond(ItemTemplate t) : CondNode
{
    public override bool Evaluate(ItemReplayer r, ref Row row) => Comparison.TryBool(r.Expand(t, ref row), out bool b) && b;
}

internal sealed class CompareCond(ItemTemplate left, ItemTemplate right, string op) : CondNode
{
    public override bool Evaluate(ItemReplayer r, ref Row row) => Comparison.Compare(r.Expand(left, ref row), r.Expand(right, ref row), op);
}

internal static class Comparison
{
    public static bool Compare(string left, string right, string op)
    {
        if (left.Contains('%'))
        {
            left = Unescape(left);
        }

        if (right.Contains('%'))
        {
            right = Unescape(right);
        }

        if (TryNumber(left, out double l) && TryNumber(right, out double r))
        {
            return op switch { "==" => l == r, "!=" => l != r, "<" => l < r, ">" => l > r, "<=" => l <= r, _ => l >= r };
        }

        if (op is "==" or "!=")
        {
            bool equal = TryBool(left, out bool lb) && TryBool(right, out bool rb) ? lb == rb : left.Equals(right, StringComparison.OrdinalIgnoreCase);
            return op == "==" ? equal : !equal;
        }

        if (Version.TryParse(left, out Version? lv) && Version.TryParse(right, out Version? rv))
        {
            int c = lv.CompareTo(rv);
            return op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 };
        }

        return false;
    }

    public static string Unescape(string s)
    {
        if (!s.Contains('%'))
        {
            return s;
        }

        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
            {
                b.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                b.Append(s[i]);
            }
        }

        return b.ToString();
    }

    private static bool TryNumber(string s, out double value)
    {
        value = 0;
        if (s.Length == 0 || !(char.IsAsciiDigit(s[0]) || s[0] is '-' or '+' or '.'))
        {
            return false;
        }

        if (s.Length > 2 && s[0] == '0' && (s[1] | 0x20) == 'x')
        {
            bool ok = long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex);
            value = hex;
            return ok;
        }

        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryBool(string s, out bool value)
    {
        value = false;
        if (s.Length is < 2 or > 6)
        {
            return false;
        }

        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("!false", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("!off", StringComparison.OrdinalIgnoreCase) || s.Equals("!no", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        return s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("no", StringComparison.OrdinalIgnoreCase) || s.Equals("!true", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("!on", StringComparison.OrdinalIgnoreCase) || s.Equals("!yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Condition parser for comparisons, and/or/not, and parentheses over templates; no functions or item lists.</summary>
internal sealed class CondParser(string text, ItemWorkload w)
{
    private int _pos;
    public string? Reason;

    public CondNode? Parse()
    {
        CondNode? n = ParseOr();
        Skip();
        if (n is null || _pos != text.Length)
        {
            Reason ??= "condition syntax";
            return null;
        }

        return n;
    }

    private void Skip()
    {
        while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
        {
            _pos++;
        }
    }

    private bool Keyword(string k)
    {
        Skip();
        if (_pos + k.Length <= text.Length && string.Compare(text, _pos, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            (_pos + k.Length == text.Length || !char.IsAsciiLetterOrDigit(text[_pos + k.Length])))
        {
            _pos += k.Length;
            return true;
        }

        return false;
    }

    private CondNode? ParseOr()
    {
        CondNode? left = ParseAnd();
        while (left is not null && Keyword("or"))
        {
            CondNode? right = ParseAnd();
            left = right is null ? null : new OrCond(left, right);
        }

        return left;
    }

    private CondNode? ParseAnd()
    {
        CondNode? left = ParseNot();
        while (left is not null && Keyword("and"))
        {
            CondNode? right = ParseNot();
            left = right is null ? null : new AndCond(left, right);
        }

        return left;
    }

    private CondNode? ParseNot()
    {
        Skip();
        if (_pos < text.Length && text[_pos] == '!' && (_pos + 1 >= text.Length || text[_pos + 1] != '='))
        {
            _pos++;
            CondNode? inner = ParseNot();
            return inner is null ? null : new NotCond(inner);
        }

        return ParsePrimary();
    }

    private CondNode? ParsePrimary()
    {
        Skip();
        if (_pos < text.Length && text[_pos] == '(')
        {
            _pos++;
            CondNode? inner = ParseOr();
            Skip();
            if (inner is null || _pos >= text.Length || text[_pos] != ')')
            {
                return null;
            }

            _pos++;
            return inner;
        }

        ItemTemplate? left = ParseOperand();
        if (left is null)
        {
            return null;
        }

        Skip();
        foreach (string op in new[] { "==", "!=", "<=", ">=", "<", ">" })
        {
            if (string.CompareOrdinal(text, _pos, op, 0, op.Length) == 0)
            {
                _pos += op.Length;
                ItemTemplate? right = ParseOperand();
                return right is null ? null : new CompareCond(left, right, op);
            }
        }

        return new TruthCond(left);
    }

    private ItemTemplate? ParseOperand()
    {
        Skip();
        if (_pos >= text.Length)
        {
            return null;
        }

        string raw;
        if (text[_pos] == '\'')
        {
            int close = text.IndexOf('\'', _pos + 1);
            if (close < 0)
            {
                return null;
            }

            raw = text[(_pos + 1)..close];
            _pos = close + 1;
        }
        else if ((text[_pos] == '$' || text[_pos] == '%') && _pos + 1 < text.Length && text[_pos + 1] == '(')
        {
            int close = text.IndexOf(')', _pos);
            if (close < 0)
            {
                return null;
            }

            raw = text[_pos..(close + 1)];
            _pos = close + 1;
        }
        else
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '.' or '_' or '-'))
            {
                _pos++;
            }

            if (_pos == start || (_pos < text.Length && text[_pos] == '('))
            {
                Reason = "condition function";
                return null;
            }

            raw = text[start.._pos];
        }

        ItemTemplate? t = ItemTemplate.Parse(raw, w, allowMetadata: true, out string? reason);
        Reason ??= reason;
        return t;
    }
}

internal enum FragKind : byte
{
    Value,
    ItemRef,
    Glob,
    Unsupported,
}

/// <summary>A spec fragment; Adjust marks a value with backslashes that MSBuild's Unix path heuristic may rewrite.</summary>
internal readonly record struct Frag(FragKind Kind, string Value, int Type, bool Adjust = false);

/// <summary>An item spec: fragments prepared once when it has no property references, otherwise a template split per application.</summary>
internal sealed class Spec
{
    public Frag[]? Static;
    public ItemTemplate? Dynamic;

    public static Spec? Parse(string text, ItemWorkload w, out string? reason)
    {
        reason = null;
        if (!text.Contains("$("))
        {
            Frag[] frags = Split(text, w);
            if (frags.Any(f => f.Kind == FragKind.Unsupported))
            {
                reason = "item transform or separator";
                return null;
            }

            if (frags.Any(f => f.Kind == FragKind.Glob))
            {
                reason = "wildcard";
                return null;
            }

            return new Spec { Static = frags };
        }

        // Item expressions are recognized before property expansion; keep them out of the template.
        if (text.Contains("@("))
        {
            reason = "item list with properties";
            return null;
        }

        ItemTemplate? t = ItemTemplate.Parse(text, w, allowMetadata: false, out reason);
        return t is null ? null : new Spec { Dynamic = t };
    }

    /// <summary>Splits an expanded spec on semicolons, keeping item expressions whole.</summary>
    public static Frag[] Split(string text, ItemWorkload? w)
    {
        var result = new List<Frag>();
        int start = 0, depth = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                char c = text[i];
                if (c == '(' && i > 0 && text[i - 1] == '@')
                {
                    depth++;
                }
                else if (c == ')' && depth > 0)
                {
                    depth--;
                }

                if (c != ';' || depth > 0)
                {
                    continue;
                }
            }

            ReadOnlySpan<char> piece = text.AsSpan(start, i - start).Trim();
            start = i + 1;
            if (piece.IsEmpty)
            {
                continue;
            }

            if (piece.StartsWith("@("))
            {
                ReadOnlySpan<char> inner = piece[2..^1].Trim();
                bool plain = piece[^1] == ')' && inner.Length > 0 && !inner.ContainsAny("-,'%$@()");
                result.Add(plain && w is not null
                    ? new Frag(FragKind.ItemRef, "", w.Types.Intern(inner.ToString()))
                    : new Frag(FragKind.Unsupported, piece.ToString(), -1));
            }
            else if (piece.ContainsAny('*', '?'))
            {
                result.Add(new Frag(FragKind.Glob, piece.ToString(), -1));
            }
            else
            {
                string value = piece.ToString();
                result.Add(new Frag(FragKind.Value, value, -1, value.Contains('\\')));
            }
        }

        return [.. result];
    }
}

internal sealed class MetaElement(int name, ItemTemplate value, CondNode? condition)
{
    public readonly int Name = name;
    public readonly ItemTemplate Value = value;
    public readonly CondNode? Condition = condition;
}

internal sealed class Site
{
    public string Key = "", TypeName = "", IncludeText = "", ExcludeText = "", UpdateText = "", RemoveText = "";
    public string MatchOnMetadata = "", MatchOnMetadataOptions = "", KeepMetadata = "", RemoveMetadata = "", KeepDuplicates = "";
    public readonly List<(string Name, string Value, string Condition)> RawMetadata = [];

    public string Kind = "";
    public int Type;
    public Spec? Spec;
    public Spec? Exclude;
    public MetaElement[] Program = [];
    public bool ItemDependent;
    public MetaBlock? Constant;                  // the whole program when it is literal and unconditional
    public bool MatchesWholeList;                // Update or Remove of @(Self)
    public string? Unsupported;
    public string File = "", DefiningDirectory = "", DefiningName = "", DefiningExtension = "";

    // Statistics.
    public int Applications, CoveredApplications, Mismatches;
    public string? FirstMismatch;
    public long FloorCovered, FloorFallback;

    public void Prepare(ItemWorkload w)
    {
        Kind = IncludeText.Length > 0 ? "IncludeOperation" : UpdateText.Length > 0 ? "UpdateOperation" : "RemoveOperation";
        Type = w.Types.Intern(TypeName);
        int colon = Key.LastIndexOf(':', Key.LastIndexOf(':') - 1);
        File = Key[..colon];
        int slash = File.LastIndexOf('/');
        DefiningDirectory = File[..(slash + 1)];
        string fileName = File[(slash + 1)..];
        int dot = fileName.LastIndexOf('.');
        DefiningName = dot < 0 ? fileName : fileName[..dot];
        DefiningExtension = dot < 0 ? "" : fileName[dot..];

        if (MatchOnMetadata.Length > 0 || KeepMetadata.Length > 0 || RemoveMetadata.Length > 0 || KeepDuplicates.Length > 0)
        {
            Unsupported = "matching or copy options";
            return;
        }

        string specText = Kind switch { "IncludeOperation" => IncludeText, "UpdateOperation" => UpdateText, _ => RemoveText };
        Spec = Spec.Parse(specText, w, out string? reason);
        if (Spec is null)
        {
            Unsupported = reason;
            return;
        }

        if (ExcludeText.Length > 0)
        {
            Exclude = Spec.Parse(ExcludeText, w, out reason);
            if (Exclude is null || (Exclude.Static?.Any(f => f.Kind is not (FragKind.Value or FragKind.ItemRef)) ?? false))
            {
                Unsupported = "exclude " + (reason ?? "item list");
                return;
            }
        }

        if (Kind != "IncludeOperation" && Spec.Static is { Length: 1 } s && s[0].Kind == FragKind.ItemRef && s[0].Type == Type)
        {
            MatchesWholeList = true;
        }

        var program = new List<MetaElement>();
        bool constant = true;
        foreach ((string name, string value, string condition) in RawMetadata)
        {
            ItemTemplate? t = ItemTemplate.Parse(value, w, allowMetadata: true, out reason);
            if (t is null)
            {
                Unsupported = "metadata " + reason;
                return;
            }

            CondNode? c = null;
            if (condition.Length > 0)
            {
                var parser = new CondParser(condition, w);
                c = parser.Parse();
                if (c is null)
                {
                    Unsupported = "metadata " + parser.Reason;
                    return;
                }

                constant = false;
            }

            constant &= t.IsLiteral && !t.Literals[0].Contains('\\');
            ItemDependent |= t.UsesMetadata || (c is not null && UsesMetadata(condition));
            program.Add(new MetaElement(w.Metadata.Intern(name), t, c));
        }

        Program = [.. program];
        if (constant && Program.Length > 0)
        {
            var frame = new Frame();
            foreach (MetaElement e in Program)
            {
                frame.Set(e.Name, e.Value.Literals[0]);
            }

            Constant = frame.ToBlock();
        }
    }

    private static bool UsesMetadata(string condition) => condition.Contains("%(");
}

/// <summary>Applies prepared operations to compact per-configuration state.</summary>
internal sealed class ItemReplayer(ItemWorkload w)
{
    private ItemEvaluation _e = null!;
    private List<Row>?[] _lists = [];
    private Site _site = null!;
    private readonly Frame _frame = new();
    private readonly List<Row> _scratch = [];
    private readonly HashSet<string> _match = new(StringComparer.Ordinal);
    private readonly StringBuilder _builder = new();
    public long Checked, Mismatched;

    public void Validate()
    {
        foreach (ItemEvaluation e in w.Evaluations)
        {
            Begin(e);
            foreach (Operation op in e.Operations)
            {
                Site site = op.Site;
                bool supported = site.Unsupported is null && TryApply(op);
                if (!supported)
                {
                    op.Fallback = true;
                    ApplyRecorded(op);
                    continue;
                }

                Checked++;
                List<Row> list = List(site.Type);
                int start = op.Appended ? _appendStart : 0;
                string? difference = Compare(list, start, op);
                if (difference is not null)
                {
                    Mismatched++;
                    site.Mismatches++;
                    site.FirstMismatch ??= difference;
                    op.Fallback = true;
                    if (op.Appended)
                    {
                        // Undo the floor's own rows: it may have appended a different number.
                        list.RemoveRange(_appendStart, list.Count - _appendStart);
                    }

                    ApplyRecorded(op);
                }
                else
                {
                    site.CoveredApplications++;
                }
            }
        }

    }

    /// <summary>Replays every evaluation; with counters, charges each operation's cost to its site.</summary>
    public void Replay(Counters? profileSites, long readCost = 0)
    {
        foreach (ItemEvaluation e in w.Evaluations)
        {
            Begin(e);
            foreach (Operation op in e.Operations)
            {
                Snapshot start = profileSites?.Read() ?? default;
                if (op.Fallback)
                {
                    ApplyRecorded(op);
                }
                else
                {
                    TryApply(op);
                }

                if (profileSites is not null)
                {
                    long cost = profileSites.Read().Instructions - start.Instructions - readCost;
                    if (op.Fallback)
                    {
                        op.Site.FloorFallback += cost;
                    }
                    else
                    {
                        op.Site.FloorCovered += cost;
                    }
                }
            }
        }
    }

    private void Begin(ItemEvaluation e)
    {
        _e = e;
        _lists = new List<Row>?[w.Types.Count];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<Row> List(int type) => _lists[type] ??= [];

    private int _appendStart;

    /// <summary>Applies a supported operation; returns false when this application needs the recording.</summary>
    private bool TryApply(Operation op)
    {
        Site site = _site = op.Site;
        List<Row> list = List(site.Type);
        _appendStart = list.Count;
        switch (site.Kind)
        {
            case "IncludeOperation":
                return Include(site, list);
            case "UpdateOperation":
                return Update(site, list);
            default:
                return Remove(site, list);
        }
    }

    private bool Include(Site site, List<Row> list)
    {
        List<Row> rows = _scratch;
        rows.Clear();
        Frag[] frags = site.Spec!.Static ?? Spec.Split(Expand(site.Spec.Dynamic!), w);
        foreach (Frag f in frags)
        {
            switch (f.Kind)
            {
                case FragKind.Value:
                    rows.Add(new Row(f.Adjust ? AdjustPath(f.Value) : f.Value, null, site));
                    break;
                case FragKind.ItemRef:
                    if (f.Type >= _lists.Length)
                    {
                        break;
                    }

                    List<Row>? source = _lists[f.Type];
                    if (source is null)
                    {
                        break;
                    }

                    MetaBlock? definitions = _e.DefinitionBlocks[f.Type];
                    int count = source.Count;   // a self-reference copies the list as it was
                    for (int i = 0; i < count; i++)
                    {
                        Row r = source[i];
                        MetaBlock? meta = definitions is null ? r.Meta : r.Meta is null ? definitions : MetaBlock.Merge(definitions, r.Meta);
                        rows.Add(new Row(r.Include, meta, site));
                    }

                    break;
                default:
                    return false;   // a property expanded into a wildcard or an item expression
            }
        }

        if (site.Exclude is not null && rows.Count > 0)
        {
            _match.Clear();
            foreach (Frag f in site.Exclude.Static ?? Spec.Split(Expand(site.Exclude.Dynamic!), w))
            {
                if (f.Kind == FragKind.ItemRef)
                {
                    if (f.Type < _lists.Length && _lists[f.Type] is { } excluded)
                    {
                        Span<Row> excludedRows = CollectionsMarshal.AsSpan(excluded);
                        for (int i = 0; i < excludedRows.Length; i++)
                        {
                            _match.Add(FullPath(ref excludedRows[i]));
                        }
                    }

                    continue;
                }

                if (f.Kind != FragKind.Value)
                {
                    return false;
                }

                _match.Add(FullPath(f.Adjust ? AdjustPath(f.Value) : f.Value));
            }

            for (int i = rows.Count - 1; i >= 0; i--)
            {
                Row r = rows[i];
                if (_match.Contains(FullPath(ref r)))
                {
                    rows.RemoveAt(i);
                }
            }
        }

        ApplyProgram(site, rows, 0, rows.Count, all: true);
        list.AddRange(rows);
        return true;
    }

    private bool Update(Site site, List<Row> list)
    {
        if (site.MatchesWholeList)
        {
            ApplyProgram(site, list, 0, list.Count, all: true);
            return true;
        }

        if (!BuildMatch(site))
        {
            return false;
        }

        Span<Row> rows = CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < rows.Length; i++)
        {
            if (_match.Contains(FullPath(ref rows[i])))
            {
                ApplyProgram(site, list, i, i + 1, all: false);
                rows = CollectionsMarshal.AsSpan(list);
            }
        }

        return true;
    }

    private bool Remove(Site site, List<Row> list)
    {
        if (site.MatchesWholeList)
        {
            list.Clear();
            return true;
        }

        if (!BuildMatch(site))
        {
            return false;
        }

        list.RemoveAll(r => _match.Contains(FullPath(ref r)));
        return true;
    }

    /// <summary>Collects the full paths an update or remove spec matches.</summary>
    private bool BuildMatch(Site site)
    {
        _match.Clear();
        foreach (Frag f in site.Spec!.Static ?? Spec.Split(Expand(site.Spec.Dynamic!), w))
        {
            switch (f.Kind)
            {
                case FragKind.Value:
                    _match.Add(FullPath(f.Adjust ? AdjustPath(f.Value) : f.Value));
                    break;
                case FragKind.ItemRef:
                    if (f.Type < _lists.Length && _lists[f.Type] is { } source)
                    {
                        Span<Row> rows = CollectionsMarshal.AsSpan(source);
                        for (int i = 0; i < rows.Length; i++)
                        {
                            _match.Add(FullPath(ref rows[i]));
                        }
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Runs the metadata program on rows [start, end): once for all rows when it reads no item metadata.</summary>
    private void ApplyProgram(Site site, List<Row> list, int start, int end, bool all)
    {
        if (site.Program.Length == 0 || start == end)
        {
            return;
        }

        Span<Row> rows = CollectionsMarshal.AsSpan(list);
        if (!site.ItemDependent)
        {
            MetaBlock? overlay = site.Constant;
            if (overlay is null)
            {
                Row none = default;
                _frame.Clear();
                foreach (MetaElement e in site.Program)
                {
                    if (e.Condition is null || e.Condition.Evaluate(this, ref none))
                    {
                        _frame.Set(e.Name, AdjustPath(Expand(e.Value, ref none)));
                    }
                }

                if (_frame.Count == 0)
                {
                    return;
                }

                overlay = _frame.ToBlock();
            }

            for (int i = start; i < end; i++)
            {
                ref Row r = ref rows[i];
                r.Meta = r.Meta is null ? overlay : MetaBlock.Merge(r.Meta, overlay);
            }

            return;
        }

        for (int i = start; i < end; i++)
        {
            ref Row r = ref rows[i];
            _frame.Clear();
            foreach (MetaElement e in site.Program)
            {
                if (e.Condition is null || e.Condition.Evaluate(this, ref r))
                {
                    _frame.Set(e.Name, AdjustPath(Expand(e.Value, ref r)));
                }
            }

            if (_frame.Count > 0)
            {
                MetaBlock block = _frame.ToBlock();
                r.Meta = r.Meta is null ? block : MetaBlock.Merge(r.Meta, block);
            }
        }
    }

    private string Expand(ItemTemplate t)
    {
        Row none = default;
        return Expand(t, ref none);
    }

    public string Expand(ItemTemplate t, ref Row row)
    {
        TemplateRef[] refs = t.Refs;
        if (refs.Length == 0)
        {
            return t.Literals[0];
        }

        if (refs.Length == 1 && t.Literals[0].Length == 0 && t.Literals[1].Length == 0)
        {
            return Resolve(refs[0], ref row);
        }

        StringBuilder b = _builder;
        b.Clear();
        for (int i = 0; i < refs.Length; i++)
        {
            b.Append(t.Literals[i]);
            b.Append(Resolve(refs[i], ref row));
        }

        b.Append(t.Literals[^1]);
        return b.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string Resolve(TemplateRef r, ref Row row)
    {
        switch (r.Kind)
        {
            case RefKind.Property:
                return _e.Properties[r.Id];
            case RefKind.FileProperty:
                return (FileProperty)r.Id switch
                {
                    FileProperty.File => _site.DefiningName + _site.DefiningExtension,
                    FileProperty.Directory => _site.DefiningDirectory,
                    FileProperty.DirectoryNoRoot => _site.DefiningDirectory[1..],
                    FileProperty.Extension => _site.DefiningExtension,
                    FileProperty.FullPath => _site.File,
                    _ => _site.DefiningName,
                };
            case RefKind.Metadata:
                if (row.Include is null)
                {
                    return "";
                }

                return _frame.Get(r.Id) ?? row.Meta?.Get(r.Id) ?? (_site.Type < _e.DefinitionBlocks.Length ? _e.DefinitionBlocks[_site.Type]?.Get(r.Id) : null) ?? "";
            default:
                return row.Include is null ? "" : BuiltInValue((BuiltIn)r.Id, ref row);
        }
    }

    private string BuiltInValue(BuiltIn b, ref Row row)
    {
        string include = row.Include;
        switch (b)
        {
            case BuiltIn.Identity:
                return include;
            case BuiltIn.Filename:
            {
                ReadOnlySpan<char> name = FileName(include);
                int dot = name.LastIndexOf('.');
                return (dot < 0 ? name : name[..dot]).ToString();
            }

            case BuiltIn.Extension:
            {
                ReadOnlySpan<char> name = FileName(include);
                int dot = name.LastIndexOf('.');
                return dot < 0 ? "" : name[dot..].ToString();
            }

            case BuiltIn.FullPath:
                return FullPath(ref row);
            case BuiltIn.RootDir:
                return "/";
            case BuiltIn.Directory:
            {
                string full = FullPath(ref row);
                int slash = full.LastIndexOf('/');
                return slash <= 0 ? "" : full[1..(slash + 1)];
            }

            case BuiltIn.RelativeDir:
            {
                int slash = include.AsSpan().LastIndexOfAny('/', '\\');
                return slash < 0 ? "" : include[..(slash + 1)];
            }

            case BuiltIn.RecursiveDir:
                return "";
            case BuiltIn.DefiningProjectFullPath:
                return row.Origin.File;
            case BuiltIn.DefiningProjectDirectory:
                return row.Origin.DefiningDirectory;
            case BuiltIn.DefiningProjectName:
                return row.Origin.DefiningName;
            default:
                return row.Origin.DefiningExtension;
        }
    }

    /// <summary>
    /// MSBuild's Unix path heuristic (FileUtilities.MaybeAdjustFilePath): a value with backslashes becomes a
    /// slash-collapsed path when its first segment exists relative to the working directory. The probe is a real
    /// filesystem call, as in MSBuild; its outcome depends on the process working directory.
    /// </summary>
    private static string AdjustPath(string value)
    {
        if (value.Length == 0 || !value.Contains('\\') || value.StartsWith("$(", StringComparison.Ordinal) ||
            value.StartsWith("@(", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return value;
        }

        string adjusted = CollapseSlashes(value);
        if (!adjusted.Contains('/'))
        {
            return value;
        }

        ReadOnlySpan<char> path = adjusted.AsSpan().Trim('"').Trim('\'');
        int directoryLength = path.Length > 1 ? path[1..].IndexOf('/') + 1 : 0;
        bool exists = directoryLength > 0
            ? System.IO.Directory.Exists(path[..directoryLength].ToString())
            : path.Length > 0 && path[0] == '/' && Path.Exists(path.ToString());
        return exists ? adjusted : value;
    }

    internal static string CollapseSlashes(string value)
    {
        var b = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            bool slash = c is '/' or '\\';
            if (slash && b.Length > 0 && b[^1] == '/')
            {
                continue;
            }

            b.Append(slash ? '/' : c);
        }

        return b.ToString();
    }

    /// <summary>Whether two values differ only by the Unix path heuristic's rewriting.</summary>
    private static bool SlashEquivalent(string? a, string? b) =>
        a is not null && b is not null && (a.Contains('\\') || b.Contains('\\')) && CollapseSlashes(a) == CollapseSlashes(b);

    private static ReadOnlySpan<char> FileName(string include)
    {
        int slash = include.AsSpan().LastIndexOfAny('/', '\\');
        return include.AsSpan(slash + 1);
    }

    private string FullPath(ref Row row) => row.FullPath ??= FullPath(row.Include);

    /// <summary>The comparison form of an include: unescaped, rooted at the project directory, and normalized.</summary>
    private string FullPath(string include)
    {
        string path = Comparison.Unescape(include);
        if (path.Contains('\\'))
        {
            path = path.Replace('\\', '/');
        }

        if (path.Length == 0 || path[0] != '/')
        {
            path = _e.Directory + "/" + path;
        }

        if (path.Contains("//") || path.Contains("/./") || path.Contains("/../") || path.EndsWith("/.", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
        {
            var parts = new List<string>();
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(part);
            }

            path = "/" + string.Join('/', parts) + (path.EndsWith('/') ? "/" : "");
        }

        return path;
    }

    private void ApplyRecorded(Operation op)
    {
        Site site = op.Site;
        op.Recorded ??= op.Rows.Select(r => new Row(r.Include, r.Block, site)).ToArray();
        List<Row> list = List(site.Type);
        if (!op.Appended)
        {
            list.Clear();
        }

        list.AddRange(op.Recorded);
    }

    /// <summary>Compares the floor's rows with the recorded ones; returns a description of the first difference.</summary>
    private string? Compare(List<Row> list, int start, Operation op)
    {
        if (list.Count - start != op.Rows.Count)
        {
            return $"{list.Count - start} rows, recorded {op.Rows.Count}";
        }

        MetaBlock? definitions = op.Site.Type < _e.DefinitionBlocks.Length ? _e.DefinitionBlocks[op.Site.Type] : null;
        for (int i = 0; i < op.Rows.Count; i++)
        {
            Row row = list[start + i];
            RecordedRow recorded = op.Rows[i];
            if (!string.Equals(row.Include, recorded.Include, StringComparison.Ordinal) && !SlashEquivalent(row.Include, recorded.Include))
            {
                return $"include '{row.Include}', recorded '{recorded.Include}'";
            }

            MetaBlock? flat = definitions is null ? row.Meta : row.Meta is null ? definitions : MetaBlock.Merge(definitions, row.Meta);
            int floorCount = flat?.Names.Length ?? 0;
            int recordedCount = recorded.Block?.Names.Length ?? 0;
            for (int j = 0; j < recordedCount; j++)
            {
                int name = recorded.Block!.Names[j];
                string? value = flat?.Get(name);
                if (!string.Equals(value, recorded.Block.Values[j], StringComparison.Ordinal) && !SlashEquivalent(value, recorded.Block.Values[j]))
                {
                    return $"%({w.Metadata.Names[name]}) '{value}', recorded '{recorded.Block.Values[j]}'";
                }
            }

            if (floorCount != recordedCount)
            {
                string extra = flat!.Names.Select(n => w.Metadata.Names[n]).Except(recorded.Block?.Names.Select(n => w.Metadata.Names[n]) ?? []).FirstOrDefault() ?? "?";
                return $"{floorCount} metadata, recorded {recordedCount} (extra %({extra}))";
            }
        }

        return null;
    }
}
