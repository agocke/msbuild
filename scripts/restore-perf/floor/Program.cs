// Reference ("floor") implementation of MSBuild's recorded condition and property-element streams.
//
// Replays an MSBUILDEVALUATIONTRACE directory evaluation by evaluation: property elements are applied
// in order and every condition is evaluated against the state at that point, then results are checked
// against the recorded ones. Static preparation (parsing each distinct condition and property value,
// resolving names to integer ids) happens once and is measured separately. Two state representations
// are compared: a dense id-indexed slot array and a case-insensitive Dictionary<string, string>.
//
// usage: Floor <trace-dir> [repetitions]

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        string dir = args[0];
        int reps = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;

        var sw = Stopwatch.StartNew();
        Workload w = Workload.Load(dir);
        Console.WriteLine($"# loaded {w.Evaluations.Count} evaluations, {w.ConditionOps} conditions, {w.PropertyOps} property elements in {sw.Elapsed.TotalSeconds:F1}s");

        using var counters = new Counters();
        w.ClassifyShapes();
        Snapshot before = counters.Read();
        w.Prepare();
        Snapshot prep = counters.Read() - before;
        w.Summarize();
        Console.WriteLine($"# static preparation: {w.Conditions.Count} distinct conditions ({w.SupportedConditions} supported), {w.Templates.Count} distinct values ({w.SupportedTemplates} supported), {w.Names.Count} names: {prep.Instructions / 1e6:F1}M {Counters.Unit}");
        Console.WriteLine($"# coverage: {w.CoveredConditionOps * 100.0 / w.ConditionOps:F1}% of condition evaluations, {w.CoveredPropertyOps * 100.0 / w.PropertyOps:F1}% of property elements");
        foreach ((string shape, long count) in w.ShapeCounts.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)))
        {
            Console.WriteLine($"#   condition shape {shape,-6} {count,9} {(w.ShapeSupported[shape] ? "replayed" : "not replayed")}");
        }

        if (Environment.GetEnvironmentVariable("FLOOR_MISMATCHES") == "1")
        {
            w.MismatchCounts = new();
            var s = new SlotState(w.Names.Count);
            w.Replay(ref s, Replay.All, null);
            foreach (var (id, n) in w.MismatchCounts.OrderByDescending(p => p.Value).Take(15))
            {
                Console.WriteLine($"# mismatch {n,7}  {w.Conditions[id]}");
            }

            w.MismatchCounts = null;
            return 0;
        }

        Run<SlotState>("slots", w, counters, reps, () => new SlotState(w.Names.Count));
        Run<DictionaryState>("dictionary", w, counters, reps, () => new DictionaryState(w.Names));
        return 0;
    }

    private static void Run<TState>(string label, Workload w, Counters counters, int reps, Func<TState> create)
        where TState : struct, IState
    {
        TState state = create();
        Snapshot Best(Replay mode, string? shape = null)
        {
            Snapshot best = default;
            for (int i = 0; i < reps + 1; i++)
            {
                Snapshot start = counters.Read();
                w.Replay(ref state, mode, shape);
                Snapshot delta = counters.Read() - start;
                if (i > 0 && (best.Instructions == 0 || delta.Instructions < best.Instructions))
                {
                    best = delta; // the first repetition warms caches and is discarded
                }
            }

            return best;
        }

        Snapshot seed = Best(Replay.Seed);
        Snapshot props = Best(Replay.Properties);
        Snapshot all = Best(Replay.All);
        var (evaluated, checkedCount, mismatches) = w.LastCheck;

        Snapshot condCost = all - props;
        Snapshot propCost = props - seed;
        Console.WriteLine();
        Console.WriteLine($"[{label}] mismatches {mismatches} of {checkedCount} checked conditions ({evaluated} evaluated)");
        string unit = Counters.Unit;
        static string Ipc(Snapshot s) => Counters.HasCycles ? $"  IPC {s.Instructions / (double)s.Cycles:F2}" : "";
        Console.WriteLine($"[{label}] seed state       {seed.Instructions / 1e9,7:F3}G {unit}  {seed.Instructions / (double)w.Evaluations.Count,9:F0} per evaluation");
        Console.WriteLine($"[{label}] property element {propCost.Instructions / 1e9,7:F3}G {unit}  {propCost.Instructions / (double)w.CoveredPropertyOps,9:F1} per element{Ipc(propCost)}");
        Console.WriteLine($"[{label}] condition        {condCost.Instructions / 1e9,7:F3}G {unit}  {condCost.Instructions / (double)w.CoveredConditionOps,9:F1} per condition{Ipc(condCost)}");
        Snapshot none = Best(Replay.All, "<none>");
        foreach (string shape in w.ShapeCounts.Keys.Where(s => w.ShapeSupported[s] && w.ShapeCounts[s] >= 100_000).OrderByDescending(s => w.ShapeCounts[s]))
        {
            Snapshot withShape = Best(Replay.All, shape);
            Snapshot cost = withShape - none;
            Console.WriteLine($"[{label}]   shape {shape,-6} {cost.Instructions / (double)w.ShapeCounts[shape],9:F1} {Counters.Unit} per condition");
        }
    }
}

internal enum Replay
{
    Seed,
    Properties,
    All,
}

internal interface IState
{
    void Reset();

    string? Get(int id);

    void Set(int id, string value);
}

internal struct SlotState : IState
{
    private readonly string?[] _values;
    private readonly int[] _generation;
    private int _current;

    public SlotState(int names)
    {
        _values = new string?[names];
        _generation = new int[names];
        _current = 0;
    }

    public void Reset() => _current++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly string? Get(int id) => _generation[id] == _current ? _values[id] : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Set(int id, string value)
    {
        _values[id] = value;
        _generation[id] = _current;
    }
}

internal struct DictionaryState : IState
{
    private readonly List<string> _names;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public DictionaryState(List<string> names) => _names = names;

    public readonly void Reset() => _values.Clear();

    public readonly string? Get(int id) => _values.TryGetValue(_names[id], out string? v) ? v : null;

    public readonly void Set(int id, string value) => _values[_names[id]] = value;
}

internal sealed class Evaluation
{
    public int OpStart, OpCount;
    public int[] SeedNames = [];
    public string[] SeedValues = [];
}

internal sealed class Workload
{
    // Ops: kind 0 = condition (arg = condition id, extra = recorded result), 1 = property (arg = template id, extra = name id).
    private readonly List<byte> _kind = new();
    private readonly List<int> _arg = new();
    private readonly List<int> _extra = new();
    private readonly List<string?> _recorded = new(); // recorded evaluated value of each property element
    private string?[] _recordedArray = [];
    private byte[] _kindArray = [];
    private int[] _argArray = [];
    private int[] _extraArray = [];

    public List<Evaluation> Evaluations { get; } = new();
    public List<string> Names { get; } = new();
    public Dictionary<string, int> NameIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Conditions { get; } = new();
    public List<string> Templates { get; } = new();
    private readonly Dictionary<string, int> _conditionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _templateIds = new(StringComparer.Ordinal);

    private Node?[] _parsed = [];
    private string[] _shape = [];
    private int[] _shapeId = [];
    private readonly Dictionary<string, int> _shapeIds = new(StringComparer.Ordinal);
    public int ShapeId(string shape) => _shapeIds.TryGetValue(shape, out int id) ? id : -2;
    private Template?[] _templates = [];

    public long ConditionOps, PropertyOps, CoveredConditionOps, CoveredPropertyOps;
    public int SupportedConditions, SupportedTemplates;
    public Dictionary<string, long> ShapeCounts { get; } = new();
    public Dictionary<string, bool> ShapeSupported { get; } = new();
    public (long Evaluated, long Checked, long Mismatches) LastCheck;
    public Dictionary<int, int>? MismatchCounts;

    public int NameId(string name)
    {
        if (!NameIds.TryGetValue(name, out int id))
        {
            id = Names.Count;
            Names.Add(name);
            NameIds.Add(name, id);
        }

        return id;
    }

    private static int Intern(Dictionary<string, int> ids, List<string> list, string value)
    {
        if (!ids.TryGetValue(value, out int id))
        {
            id = list.Count;
            list.Add(value);
            ids.Add(value, id);
        }

        return id;
    }

    public static Workload Load(string dir)
    {
        var w = new Workload();
        foreach (string file in Directory.GetFiles(dir, "trace.*.tsv").OrderBy(f => f, StringComparer.Ordinal))
        {
            Evaluation? current = null;
            HashSet<int> written = new();
            List<(int Name, string Value)> final = new();
            foreach (string line in File.ReadLines(file))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                string[] f = line.Split('\t');
                switch (line[0])
                {
                    case 'E':
                        current = new Evaluation { OpStart = w._kind.Count };
                        written.Clear();
                        final.Clear();
                        break;
                    case 'C' when current is not null:
                        w._kind.Add(0);
                        w._arg.Add(Intern(w._conditionIds, w.Conditions, Unescape(f[2])));
                        w._extra.Add(f[1] == "1" ? 1 : 0);
                        w._recorded.Add(null);
                        w.ConditionOps++;
                        break;
                    case 'P' when current is not null:
                        int nameId = w.NameId(Unescape(f[1]));
                        written.Add(nameId);
                        w._kind.Add(1);
                        w._arg.Add(Intern(w._templateIds, w.Templates, Unescape(f[2])));
                        w._extra.Add(nameId);
                        w._recorded.Add(Unescape(f.Length > 3 ? f[3] : ""));
                        w.PropertyOps++;
                        break;
                    case 'F' when current is not null:
                        final.Add((w.NameId(Unescape(f[1])), Unescape(f.Length > 2 ? f[2] : "")));
                        break;
                    case 'X' when current is not null:
                        current.OpCount = w._kind.Count - current.OpStart;
                        // Initial state: final values of names no property element in this evaluation wrote
                        // (environment, global, toolset, and reserved properties).
                        var seeds = final.Where(p => !written.Contains(p.Name)).ToArray();
                        current.SeedNames = seeds.Select(p => p.Name).ToArray();
                        current.SeedValues = seeds.Select(p => p.Value).ToArray();
                        w.Evaluations.Add(current);
                        current = null;
                        break;
                }
            }
        }

        w._kindArray = w._kind.ToArray();
        w._argArray = w._arg.ToArray();
        w._extraArray = w._extra.ToArray();
        w._recordedArray = w._recorded.ToArray();
        return w;
    }

    public void ClassifyShapes()
    {
        _shape = new string[Conditions.Count];
        for (int i = 0; i < Conditions.Count; i++)
        {
            _shape[i] = Conditions[i].Length == 0 ? "empty" : Shape(Conditions[i]);
        }

        _shapeId = new int[Conditions.Count];
        for (int i = 0; i < Conditions.Count; i++)
        {
            if (!_shapeIds.TryGetValue(_shape[i], out int id))
            {
                id = _shapeIds.Count;
                _shapeIds.Add(_shape[i], id);
            }

            _shapeId[i] = id;
        }
    }

    public void Prepare()
    {
        _parsed = new Node?[Conditions.Count];
        for (int i = 0; i < Conditions.Count; i++)
        {
            // An empty condition is true, as in MSBuild.
            _parsed[i] = Conditions[i].Length == 0 ? TrueNode.Instance : new ConditionParser(Conditions[i], this).Parse();
            if (_parsed[i] is not null)
            {
                SupportedConditions++;
            }
        }

        _templates = new Template?[Templates.Count];
        for (int i = 0; i < Templates.Count; i++)
        {
            _templates[i] = Template.Parse(Templates[i], this);
            if (_templates[i] is not null)
            {
                SupportedTemplates++;
            }
        }

    }

    public void Summarize()
    {
        for (int i = 0; i < _kindArray.Length; i++)
        {
            if (_kindArray[i] == 0)
            {
                string shape = _shape[_argArray[i]];
                ShapeCounts[shape] = ShapeCounts.GetValueOrDefault(shape) + 1;
                bool supported = _parsed[_argArray[i]] is not null;
                ShapeSupported[shape] = ShapeSupported.GetValueOrDefault(shape) || supported;
                if (supported)
                {
                    CoveredConditionOps++;
                }
            }
            else if (_templates[_argArray[i]] is not null)
            {
                CoveredPropertyOps++;
            }
        }
    }

    public void Replay<TState>(ref TState state, Replay mode, string? onlyShape)
        where TState : struct, IState
    {
        int onlyShapeId = onlyShape is null ? -1 : ShapeId(onlyShape);
        long evaluated = 0, checkedCount = 0, mismatches = 0;
        var context = new EvalContext<TState>();
        foreach (Evaluation e in Evaluations)
        {
            state.Reset();
            int[] seedNames = e.SeedNames;
            string[] seedValues = e.SeedValues;
            for (int i = 0; i < seedNames.Length; i++)
            {
                state.Set(seedNames[i], seedValues[i]);
            }

            if (mode == global::Replay.Seed)
            {
                continue;
            }

            int end = e.OpStart + e.OpCount;
            for (int op = e.OpStart; op < end; op++)
            {
                int arg = _argArray[op];
                if (_kindArray[op] == 1)
                {
                    Template? t = _templates[arg];
                    // Elements outside the replayed subset (property functions, items) are applied with their
                    // recorded value so later conditions see the same state.
                    state.Set(_extraArray[op], t is not null ? t.Evaluate(ref state) : _recordedArray[op]!);
                }
                else if (mode == global::Replay.All)
                {
                    Node? n = _parsed[arg];
                    if (n is null || (onlyShapeId != -1 && _shapeId[arg] != onlyShapeId))
                    {
                        continue;
                    }

                    bool result = n.Evaluate(ref state, ref context);
                    evaluated++;
                    if (!n.UsesMetadata)
                    {
                        checkedCount++;
                        if (result != (_extraArray[op] == 1))
                        {
                            mismatches++;
                            if (MismatchCounts is not null)
                            {
                                MismatchCounts[arg] = MismatchCounts.GetValueOrDefault(arg) + 1;
                            }
                        }
                    }
                }
            }
        }

        if (mode == global::Replay.All && onlyShape is null)
        {
            LastCheck = (evaluated, checkedCount, mismatches);
        }
    }

    // Matches the shape buckets used to characterize the trace.
    private static string Shape(string s)
    {
        var k = new StringBuilder();
        if (s.Contains("$(", StringComparison.Ordinal)) k.Append('P');
        if (s.Contains("@(", StringComparison.Ordinal)) k.Append('I');
        if (s.Contains("%(", StringComparison.Ordinal)) k.Append('M');
        if (s.Contains("::", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(s, @"\$\([A-Za-z_][\w]*\.")) k.Append('F');
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?i)\bexists\s*\(|hastrailingslash\s*\(")) k.Append('X');
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"(?i)\b(and|or)\b")) k.Append('B');
        return k.Length == 0 ? "lit" : k.ToString();
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
        {
            return s;
        }

        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                b.Append(s[i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => s[i] });
            }
            else
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }
}

/// <summary>
/// A sequence of literal and property-reference segments, as in <c>$(A)\bin\$(Configuration)</c>.
/// </summary>
internal sealed class Template
{
    private readonly string[] _literals; // literal text before each reference, plus the trailing literal
    private readonly int[] _refs;        // property name ids
    private readonly string? _constant;

    private Template(string[] literals, int[] refs)
    {
        _literals = literals;
        _refs = refs;
        _constant = refs.Length == 0 ? literals[0] : null;
    }

    /// <summary>Parses literal and <c>$(Name)</c> segments; anything else (functions, items) is unsupported.</summary>
    public static Template? Parse(string value, Workload w)
    {
        var literals = new List<string>();
        var refs = new List<int>();
        var literal = new StringBuilder();
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '@' && i + 1 < value.Length && value[i + 1] == '(')
            {
                return null;
            }

            if (value[i] == '$' && i + 1 < value.Length && value[i + 1] == '(')
            {
                int nameEnd = ConditionParser.ScanName(value, i + 2);
                if (nameEnd < 0 || nameEnd >= value.Length || value[nameEnd] != ')')
                {
                    return null; // property function or malformed reference
                }

                literals.Add(literal.ToString());
                literal.Clear();
                refs.Add(w.NameId(value.Substring(i + 2, nameEnd - (i + 2))));
                i = nameEnd + 1;
                continue;
            }

            literal.Append(value[i]);
            i++;
        }

        literals.Add(literal.ToString());
        return new Template(literals.ToArray(), refs.ToArray());
    }

    public string Evaluate<TState>(ref TState state)
        where TState : struct, IState
    {
        if (_constant is not null)
        {
            return _constant;
        }

        if (_refs.Length == 1 && _literals[0].Length == 0 && _literals[1].Length == 0)
        {
            return state.Get(_refs[0]) ?? string.Empty;
        }

        int length = 0;
        for (int i = 0; i < _refs.Length; i++)
        {
            length += _literals[i].Length + (state.Get(_refs[i])?.Length ?? 0);
        }

        length += _literals[^1].Length;
        return string.Create(length, (this, state), static (span, s) =>
        {
            (Template t, TState st) = s;
            int pos = 0;
            for (int i = 0; i < t._refs.Length; i++)
            {
                t._literals[i].AsSpan().CopyTo(span[pos..]);
                pos += t._literals[i].Length;
                string? v = st.Get(t._refs[i]);
                if (v is not null)
                {
                    v.AsSpan().CopyTo(span[pos..]);
                    pos += v.Length;
                }
            }

            t._literals[^1].AsSpan().CopyTo(span[pos..]);
        });
    }
}

internal struct EvalContext<TState>
    where TState : struct, IState
{
    // Scratch space for operands that concatenate several segments; no per-condition allocation.
    public char[] Buffer;
}

internal abstract class Node
{
    public bool UsesMetadata;

    public abstract bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context)
        where TState : struct, IState;
}

internal sealed class TrueNode : Node
{
    public static readonly TrueNode Instance = new();

    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context) => true;
}

internal sealed class AndNode(Node left, Node right) : Node
{
    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context) =>
        left.Evaluate(ref state, ref context) && right.Evaluate(ref state, ref context);
}

internal sealed class OrNode(Node left, Node right) : Node
{
    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context) =>
        left.Evaluate(ref state, ref context) || right.Evaluate(ref state, ref context);
}

internal sealed class NotNode(Node operand) : Node
{
    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context) =>
        !operand.Evaluate(ref state, ref context);
}

/// <summary>A string operand: literal text interleaved with property or metadata references.</summary>
internal sealed class Operand
{
    public string[] Literals = [];
    public int[] Refs = [];          // >= 0: property name id; < 0: ~metadata index
    public bool HasMetadata;

    // Synthetic per-item metadata (the trace does not record item metadata); values have realistic lengths.
    private static readonly string[] s_metadata = ["libs", "Microsoft.Extensions.Logging", "src/libraries/Common/src", ".cs", "true", ""];

    public ReadOnlySpan<char> Value<TState>(ref TState state, ref EvalContext<TState> context)
        where TState : struct, IState
    {
        if (Refs.Length == 0)
        {
            return Literals[0];
        }

        if (Refs.Length == 1 && Literals[0].Length == 0 && Literals[1].Length == 0)
        {
            return Get(ref state, Refs[0]);
        }

        char[] buffer = context.Buffer ??= new char[1 << 16];
        int pos = 0;
        for (int i = 0; i < Refs.Length; i++)
        {
            Literals[i].AsSpan().CopyTo(buffer.AsSpan(pos));
            pos += Literals[i].Length;
            ReadOnlySpan<char> v = Get(ref state, Refs[i]);
            v.CopyTo(buffer.AsSpan(pos));
            pos += v.Length;
        }

        Literals[^1].AsSpan().CopyTo(buffer.AsSpan(pos));
        pos += Literals[^1].Length;
        return buffer.AsSpan(0, pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> Get<TState>(ref TState state, int r)
        where TState : struct, IState
        => r >= 0 ? state.Get(r) : s_metadata[~r % s_metadata.Length];
}

internal sealed class ComparisonNode : Node
{
    public Operand Left = null!, Right = null!;
    public string Op = "";

    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context)
    {
        // The left value may live in the shared buffer; materialize it only when the right side also needs it.
        ReadOnlySpan<char> left = Left.Value(ref state, ref context);
        string? leftCopy = null;
        if (Right.Refs.Length > 1)
        {
            leftCopy = left.ToString();
        }

        ReadOnlySpan<char> right = Right.Value(ref state, ref context);
        if (leftCopy is not null)
        {
            left = leftCopy;
        }

        // Property values are stored escaped; MSBuild compares unescaped text.
        if (left.Contains('%'))
        {
            left = Unescape(left);
        }

        if (right.Contains('%'))
        {
            right = Unescape(right);
        }

        // MSBuild compares numerically when both sides are numbers, as booleans when both are booleans,
        // and otherwise as case-insensitive strings.
        bool equalityOp = Op is "==" or "!=";
        if (TryNumber(left, out double l) && TryNumber(right, out double r))
        {
            return Op switch
            {
                "==" => l == r,
                "!=" => l != r,
                "<" => l < r,
                ">" => l > r,
                "<=" => l <= r,
                _ => l >= r,
            };
        }

        if (equalityOp)
        {
            bool equal = TryBool(left, out bool lb) && TryBool(right, out bool rb)
                ? lb == rb
                : left.Equals(right, StringComparison.OrdinalIgnoreCase);
            return Op == "==" ? equal : !equal;
        }

        if (Version.TryParse(left, out Version? lv) && Version.TryParse(right, out Version? rv))
        {
            int c = lv.CompareTo(rv);
            return Op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 };
        }

        return false;
    }

    private static string Unescape(ReadOnlySpan<char> s)
    {
        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && char.IsAsciiHexDigit(s[i + 1]) && char.IsAsciiHexDigit(s[i + 2]))
            {
                b.Append((char)Convert.ToInt32(s.Slice(i + 1, 2).ToString(), 16));
                i += 2;
            }
            else
            {
                b.Append(s[i]);
            }
        }

        return b.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryNumber(ReadOnlySpan<char> s, out double value)
    {
        value = 0;
        if (s.IsEmpty)
        {
            return false;
        }

        char c = s[0];
        if (!(char.IsAsciiDigit(c) || c is '-' or '+' or '.'))
        {
            return false;
        }

        if (s.Length > 2 && s[0] == '0' && (s[1] | 0x20) == 'x')
        {
            bool ok = long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex);
            value = hex;
            return ok;
        }

        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryBool(ReadOnlySpan<char> s, out bool value)
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

/// <summary>A bare operand in boolean position, such as <c>'$(Flag)'</c> or <c>true</c>.</summary>
internal sealed class TruthNode(Operand operand) : Node
{
    public override bool Evaluate<TState>(ref TState state, ref EvalContext<TState> context) =>
        ComparisonNode.TryBool(operand.Value(ref state, ref context), out bool b) && b;
}

/// <summary>
/// Parser for the MSBuild condition subset without functions, item lists, or Exists: comparisons,
/// and/or/not, parentheses, quoted strings with property and metadata references, and bare words.
/// Returns null for anything outside that subset.
/// </summary>
internal sealed class ConditionParser(string text, Workload w)
{
    private int _pos;
    private bool _usesMetadata;
    private static readonly Dictionary<string, int> s_metadataIds = new(StringComparer.OrdinalIgnoreCase);

    public Node? Parse()
    {
        try
        {
            Node? n = ParseOr();
            SkipSpace();
            if (n is null || _pos != text.Length)
            {
                return null;
            }

            n.UsesMetadata = _usesMetadata;
            return n;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static int ScanName(string s, int start)
    {
        int i = start;
        if (i >= s.Length || !(char.IsAsciiLetter(s[i]) || s[i] == '_'))
        {
            return -1;
        }

        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-'))
        {
            i++;
        }

        return i;
    }

    private void SkipSpace()
    {
        while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
        {
            _pos++;
        }
    }

    private bool Keyword(string k)
    {
        SkipSpace();
        if (_pos + k.Length <= text.Length && string.Compare(text, _pos, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            (_pos + k.Length == text.Length || !char.IsAsciiLetterOrDigit(text[_pos + k.Length])))
        {
            _pos += k.Length;
            return true;
        }

        return false;
    }

    private Node? ParseOr()
    {
        Node? left = ParseAnd();
        while (left is not null && Keyword("or"))
        {
            Node? right = ParseAnd();
            if (right is null) return null;
            left = new OrNode(left, right);
        }

        return left;
    }

    private Node? ParseAnd()
    {
        Node? left = ParseUnary();
        while (left is not null && Keyword("and"))
        {
            Node? right = ParseUnary();
            if (right is null) return null;
            left = new AndNode(left, right);
        }

        return left;
    }

    private Node? ParseUnary()
    {
        SkipSpace();
        if (_pos < text.Length && text[_pos] == '!' && !(_pos + 1 < text.Length && text[_pos + 1] == '='))
        {
            _pos++;
            Node? inner = ParseUnary();
            return inner is null ? null : new NotNode(inner);
        }

        if (_pos < text.Length && text[_pos] == '(')
        {
            _pos++;
            Node? inner = ParseOr();
            SkipSpace();
            if (inner is null || _pos >= text.Length || text[_pos] != ')') return null;
            _pos++;
            return inner;
        }

        Operand? left = ParseOperand();
        if (left is null) return null;
        SkipSpace();
        string? op = null;
        foreach (string candidate in new[] { "==", "!=", "<=", ">=", "<", ">" })
        {
            if (string.CompareOrdinal(text, _pos, candidate, 0, candidate.Length) == 0)
            {
                op = candidate;
                break;
            }
        }

        if (op is null)
        {
            return new TruthNode(left);
        }

        _pos += op.Length;
        Operand? right = ParseOperand();
        return right is null ? null : new ComparisonNode { Left = left, Right = right, Op = op };
    }

    private Operand? ParseOperand()
    {
        SkipSpace();
        if (_pos >= text.Length) return null;
        string body;
        if (text[_pos] == '\'')
        {
            int close = text.IndexOf('\'', _pos + 1);
            if (close < 0) return null;
            body = text.Substring(_pos + 1, close - _pos - 1);
            _pos = close + 1;
        }
        else if (text[_pos] == '$' && _pos + 1 < text.Length && text[_pos + 1] == '(')
        {
            int close = text.IndexOf(')', _pos);
            if (close < 0) return null;
            body = text.Substring(_pos, close - _pos + 1);
            _pos = close + 1;
        }
        else
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '.' or '_' or '-'))
            {
                _pos++;
            }

            if (_pos == start || (_pos < text.Length && text[_pos] == '(')) return null; // function call such as Exists(...)
            body = text.Substring(start, _pos - start);
        }

        return ParseSegments(body);
    }

    private Operand? ParseSegments(string body)
    {
        var literals = new List<string>();
        var refs = new List<int>();
        var literal = new StringBuilder();
        bool hasMetadata = false;
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if ((c == '$' || c == '%') && i + 1 < body.Length && body[i + 1] == '(')
            {
                int nameEnd = ScanName(body, i + 2);
                int id;
                if (c == '%' && nameEnd > 0 && nameEnd < body.Length && body[nameEnd] == '.')
                {
                    nameEnd = ScanName(body, nameEnd + 1); // %(ItemType.Name)
                }

                if (nameEnd < 0 || nameEnd >= body.Length || body[nameEnd] != ')')
                {
                    return null; // property function or unsupported reference
                }

                string name = body.Substring(i + 2, nameEnd - (i + 2));
                if (c == '$')
                {
                    id = w.NameId(name);
                }
                else
                {
                    if (!s_metadataIds.TryGetValue(name, out int m))
                    {
                        m = s_metadataIds.Count;
                        s_metadataIds.Add(name, m);
                    }

                    id = ~m;
                    hasMetadata = true;
                }

                literals.Add(literal.ToString());
                literal.Clear();
                refs.Add(id);
                i = nameEnd + 1;
                continue;
            }

            if (c == '@' && i + 1 < body.Length && body[i + 1] == '(')
            {
                return null;
            }

            literal.Append(c);
            i++;
        }

        literals.Add(literal.ToString());
        _usesMetadata |= hasMetadata;
        return new Operand { Literals = literals.ToArray(), Refs = refs.ToArray(), HasMetadata = hasMetadata };
    }
}

internal struct Snapshot
{
    public long Instructions, Cycles;

    public static Snapshot operator -(Snapshot a, Snapshot b) => new() { Instructions = a.Instructions - b.Instructions, Cycles = a.Cycles - b.Cycles };
}

/// <summary>
/// Per-thread cost counters via perf_event_open: instructions and cycles (user and kernel) when the CPU
/// exposes hardware counters, otherwise the kernel's task clock (CPU nanoseconds). In task-clock mode
/// <see cref="Snapshot.Instructions"/> holds CPU nanoseconds and cycles are zero. FLOOR_COUNTER=task-clock
/// forces the software counter.
/// </summary>
internal sealed unsafe class Counters : IDisposable
{
    private const uint TypeHardware = 0, TypeSoftware = 1;
    private readonly int _leader;
    private readonly bool _hardware;

    public static string Unit { get; private set; } = "instructions";

    public static bool HasCycles { get; private set; } = true;

    public Counters()
    {
        bool excludeKernel = false;
        bool forceSoftware = Environment.GetEnvironmentVariable("FLOOR_COUNTER") == "task-clock";
        _leader = forceSoftware ? -1 : Open(TypeHardware, 1, -1, excludeKernel);  // PERF_COUNT_HW_INSTRUCTIONS
        if (_leader < 0 && !forceSoftware && Marshal.GetLastPInvokeError() == 13)
        {
            // kernel.perf_event_paranoid > 1 forbids counting kernel work; fall back to user mode.
            excludeKernel = true;
            _leader = Open(TypeHardware, 1, -1, excludeKernel);
        }

        if (_leader >= 0 && Open(TypeHardware, 0, _leader, excludeKernel) >= 0) // PERF_COUNT_HW_CPU_CYCLES
        {
            _hardware = true;
            Console.WriteLine(excludeKernel
                ? "# counters: instructions, user mode only (set kernel.perf_event_paranoid=1 to include kernel work)"
                : "# counters: instructions, user and kernel");
            return;
        }

        if (_leader >= 0)
        {
            close(_leader);
        }

        // No hardware counters (for example, a VM without a virtual PMU): measure CPU time instead.
        _leader = Open(TypeSoftware, 1, -1, excludeKernel: true); // PERF_COUNT_SW_TASK_CLOCK
        if (_leader < 0) throw new InvalidOperationException($"perf_event_open failed: {Marshal.GetLastPInvokeError()}");
        Unit = "CPU ns";
        HasCycles = false;
        Console.WriteLine("# counters: no hardware counters; reporting thread CPU time in nanoseconds (noisier than instructions)");
    }

    public Snapshot Read()
    {
        ulong* buffer = stackalloc ulong[3];
        nint size = (_hardware ? 3 : 2) * sizeof(ulong);
        if (read(_leader, buffer, size) != size) throw new InvalidOperationException("read failed");
        return new Snapshot { Instructions = (long)buffer[1], Cycles = _hardware ? (long)buffer[2] : 0 };
    }

    public void Dispose() => close(_leader);

    private static int Open(uint type, ulong config, int group, bool excludeKernel)
    {
        ulong* attr = stackalloc ulong[16];
        new Span<ulong>(attr, 16).Clear();
        ((uint*)attr)[0] = type;
        ((uint*)attr)[1] = 128;        // size
        attr[1] = config;
        attr[4] = 8;                   // read_format = PERF_FORMAT_GROUP
        attr[5] = (1 << 6) | (excludeKernel ? 1UL << 5 : 0); // exclude_hv, exclude_kernel
        return (int)syscall(298, attr, 0, -1, group, 8);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, void* attr, int pid, int cpu, int group, ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, void* buffer, nint count);

    [DllImport("libc")]
    private static extern int close(int fd);
}
