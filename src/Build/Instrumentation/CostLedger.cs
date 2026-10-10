// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;
using Microsoft.Build.Evaluation;

#if NET
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// Diagnostic, per-thread cost ledger for evaluation and execution scopes.
/// </summary>
/// <remarks>
/// Enabled by setting <c>MSBUILDCOSTLEDGER</c> to an output path prefix; each process writes
/// <c>{prefix}.{pid}.tsv</c> at exit. <c>MSBUILDCOSTLEDGERLEVEL=2</c> also records fine-grained scopes.
/// On Linux, each instrumented thread opens a perf_event group (instructions, cycles, task clock), so
/// one read() returns all three. Instructions retired are the primary cost unit because, unlike CPU
/// time, they do not depend on clock speed, SMT sharing, or core count. Kernel instructions are
/// included when kernel.perf_event_paranoid permits it; the output header records the mode.
/// Scopes nest per thread. Inclusive costs are counter deltas, and exclusive costs subtract nested
/// scopes. A request that awaits on the dedicated request-builder scheduler can resume on another
/// thread, so <see cref="Detach"/> and <see cref="Attach"/> carry its open frames across that await.
/// Frames that close on another thread without being carried are counted as migrated and excluded.
/// </remarks>
internal static class CostLedger
{
    internal const string OutputEnvironmentVariable = "MSBUILDCOSTLEDGER";
    internal const string LevelEnvironmentVariable = "MSBUILDCOSTLEDGERLEVEL";

    private const int MaxDepth = 128;

    private static readonly int s_executionBase = Enum.GetValues(typeof(EvaluationPerformanceMetric)).Length;
    private static readonly string[] s_scopeNames = CreateScopeNames();
    private static readonly int s_calibrationScope = s_scopeNames.Length - 1;
    private static readonly List<ThreadLedger> s_threads = [];
    private static readonly object s_dumpLock = new();

    private static long s_ledgerCount;
    private static bool s_enabled;
    private static bool[] s_scopeEnabled = new bool[s_scopeNames.Length];
    private static string? s_outputPath;
    private static bool s_dumped;

    [ThreadStatic]
    private static ThreadLedger? t_ledger;

    static CostLedger()
    {
        string? prefix = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        if (string.IsNullOrEmpty(prefix))
        {
            return;
        }

        int level = int.TryParse(Environment.GetEnvironmentVariable(LevelEnvironmentVariable), out int parsed) ? parsed : 1;
        Configure($"{prefix}.{Environment.ProcessId}.tsv", level);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }

    internal static bool Enabled => s_enabled;

    internal static CounterMode Mode => PerfCounters.Mode;

    /// <summary>
    /// Enables the ledger with an explicit output path, for tests.
    /// </summary>
    internal static void Configure(string outputPath, int level)
    {
        bool[] scopeEnabled = new bool[s_scopeNames.Length];
        for (int i = 0; i < scopeEnabled.Length; i++)
        {
            scopeEnabled[i] = level >= 2 || IsCoarseScope(i);
        }

        s_scopeEnabled = scopeEnabled;
        s_outputPath = outputPath;
        s_dumped = false;
        s_enabled = true;
    }

    internal static void DisableForTesting() => s_enabled = false;

    internal static Frame Enter(EvaluationPerformanceMetric metric) => Enter((int)metric);

    internal static Frame Enter(BuildExecutionMetric metric) => Enter(s_executionBase + (int)metric);

    /// <summary>
    /// Closes a scope. Inlined so an inactive frame (ledger disabled) costs only a null check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Exit(in Frame frame)
    {
        if (frame.Owner is not null)
        {
            ExitActive(frame);
        }
    }

    private static void ExitActive(in Frame frame)
    {
        ThreadLedger? ledger = t_ledger;
        if (ledger is not null && ledger.Holds(frame))
        {
            ledger.Exit(frame);
        }
        else if (ledger is null || !ReferenceEquals(ledger, frame.Owner))
        {
            // Closed on a thread that does not hold it: the request resumed elsewhere without Attach.
            frame.Owner!.MarkMigrated(frame);
        }
        else
        {
            ledger.Exit(frame);
        }
    }

    /// <summary>
    /// Removes every open frame from the current thread before an await that may resume elsewhere.
    /// </summary>
    internal static DetachedFrames? Detach() => s_enabled ? t_ledger?.Detach() : null;

    /// <summary>
    /// Restores frames removed by <see cref="Detach"/> onto the current thread.
    /// </summary>
    internal static void Attach(DetachedFrames? frames)
    {
        if (frames is not null)
        {
            GetLedger().Attach(frames);
        }
    }

    internal static void Dump()
    {
        lock (s_dumpLock)
        {
            if (!s_enabled || s_dumped || s_outputPath is null)
            {
                return;
            }

            s_dumped = true;
            try
            {
                File.WriteAllText(s_outputPath, Format());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never fail the build.
            }
        }
    }

    internal static string Format()
    {
        Calibration calibration = Calibrate();
        Entry[] totals = new Entry[s_scopeNames.Length];
        List<ThreadLedger> threads;
        lock (s_threads)
        {
            threads = [.. s_threads];
        }

        var builder = new StringBuilder();
        builder.Append("# process=").Append(Environment.ProcessId)
            .Append(" counters=").Append(PerfCounters.Mode)
            .Append(" threads=").Append(threads.Count)
            .Append(" processors=").Append(Environment.ProcessorCount).AppendLine();
        builder.Append("# command_line=").AppendLine(Environment.CommandLine);
        builder.Append("# probe_cost_per_scope parent_instructions=").Append(calibration.ParentInstructions.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" parent_cycles=").Append(calibration.ParentCycles.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" self_instructions=").Append(calibration.SelfInstructions.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" self_cycles=").Append(calibration.SelfCycles.ToString("F0", CultureInfo.InvariantCulture))
            .AppendLine();
        builder.AppendLine("# thread\tname\ttotal_instructions\troot_instructions\tunscoped_instructions\ttotal_task_ms\troot_task_ms\tmultiplexed_reads\tdepth_overflow");

        foreach (ThreadLedger thread in threads)
        {
            Snapshot total = thread.ReadTotal();
            Snapshot root = thread.RootTotal;
            builder.Append("# ").Append(thread.ManagedThreadId)
                .Append('\t').Append(thread.Name)
                .Append('\t').Append(total.Instructions)
                .Append('\t').Append(root.Instructions)
                .Append('\t').Append(total.Instructions - root.Instructions)
                .Append('\t').Append(FormatMilliseconds(total.TaskNanoseconds))
                .Append('\t').Append(FormatMilliseconds(root.TaskNanoseconds))
                .Append('\t').Append(thread.MultiplexedReads)
                .Append('\t').Append(thread.Overflow)
                .AppendLine();

            for (int i = 0; i < totals.Length; i++)
            {
                totals[i].Add(thread.Entries[i]);
            }
        }

        builder.AppendLine(
            "scope\tcount\tmigrated\tabandoned\t" +
            "incl_instructions\texcl_instructions\tincl_cycles\texcl_cycles\t" +
            "incl_task_ms\texcl_task_ms\tincl_wall_ms\texcl_wall_ms\tincl_alloc_bytes\texcl_alloc_bytes");
        for (int i = 0; i < totals.Length; i++)
        {
            ref Entry entry = ref totals[i];
            if (entry.Count == 0 && entry.Migrated == 0 && entry.Abandoned == 0)
            {
                continue;
            }

            builder.Append(s_scopeNames[i])
                .Append('\t').Append(entry.Count)
                .Append('\t').Append(entry.Migrated)
                .Append('\t').Append(entry.Abandoned)
                .Append('\t').Append(entry.Inclusive.Instructions)
                .Append('\t').Append(entry.Exclusive.Instructions)
                .Append('\t').Append(entry.Inclusive.Cycles)
                .Append('\t').Append(entry.Exclusive.Cycles)
                .Append('\t').Append(FormatMilliseconds(entry.Inclusive.TaskNanoseconds))
                .Append('\t').Append(FormatMilliseconds(entry.Exclusive.TaskNanoseconds))
                .Append('\t').Append(FormatMilliseconds(TicksToNanoseconds(entry.Inclusive.WallTicks)))
                .Append('\t').Append(FormatMilliseconds(TicksToNanoseconds(entry.Exclusive.WallTicks)))
                .Append('\t').Append(entry.Inclusive.AllocatedBytes)
                .Append('\t').Append(entry.Exclusive.AllocatedBytes)
                .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the current thread's entry for a scope, for tests.
    /// </summary>
    internal static Entry GetCurrentThreadEntryForTesting(BuildExecutionMetric metric) =>
        GetLedger().Entries[s_executionBase + (int)metric];

    private static Frame Enter(int scope)
    {
        if (!s_enabled || !s_scopeEnabled[scope])
        {
            return default;
        }

        return GetLedger().Enter(scope);
    }

    private static ThreadLedger GetLedger()
    {
        ThreadLedger? ledger = t_ledger;
        if (ledger is null)
        {
            ledger = new ThreadLedger(s_scopeNames.Length);
            lock (s_threads)
            {
                s_threads.Add(ledger);
            }

            t_ledger = ledger;
        }

        return ledger;
    }

    private static Calibration Calibrate()
    {
        const int Iterations = 4096;

        // Measure on a private ledger so calibration frames do not appear in the dump thread's totals.
        var ledger = new ThreadLedger(s_scopeNames.Length);
        Frame outer = ledger.Enter(s_calibrationScope);
        for (int i = 0; i < Iterations; i++)
        {
            Frame inner = ledger.Enter(s_calibrationScope);
            ledger.Exit(inner);
        }

        ledger.Exit(outer);
        ledger.Dispose();

        // The outer frame's exclusive cost is the probe work charged to an enclosing scope per nested pair;
        // the inner frames' inclusive cost is what an empty scope reports for itself.
        Entry entry = ledger.Entries[s_calibrationScope];
        Snapshot innerInclusive = entry.Inclusive - ledger.RootTotal;
        Snapshot parentCharge = entry.Exclusive - innerInclusive;
        return new Calibration(
            (double)parentCharge.Instructions / Iterations,
            (double)parentCharge.Cycles / Iterations,
            (double)innerInclusive.Instructions / Iterations,
            (double)innerInclusive.Cycles / Iterations);
    }

    private static bool IsCoarseScope(int scope)
    {
        if (scope < s_executionBase)
        {
            return (EvaluationPerformanceMetric)scope switch
            {
                EvaluationPerformanceMetric.TotalEvaluation or
                EvaluationPerformanceMetric.InitialProperties or
                EvaluationPerformanceMetric.PropertiesAndImports or
                EvaluationPerformanceMetric.ItemDefinitions or
                EvaluationPerformanceMetric.ItemOperationConstruction or
                EvaluationPerformanceMetric.LazyItemApplication or
                EvaluationPerformanceMetric.UsingTasks or
                EvaluationPerformanceMetric.Targets or
                EvaluationPerformanceMetric.ModuleLowering => true,
                _ => false,
            };
        }

        if (scope == s_calibrationScope)
        {
            return true;
        }

        return (BuildExecutionMetric)(scope - s_executionBase) switch
        {
            BuildExecutionMetric.BuildSession or
            BuildExecutionMetric.ProjectDispatch or
            BuildExecutionMetric.ProjectBuild or
            BuildExecutionMetric.ProjectBlocked or
            BuildExecutionMetric.TargetBuilder or
            BuildExecutionMetric.Target or
            BuildExecutionMetric.TaskBuilder or
            BuildExecutionMetric.Task or
            BuildExecutionMetric.TaskResolve or
            BuildExecutionMetric.TaskInitialize or
            BuildExecutionMetric.TaskParameters or
            BuildExecutionMetric.TaskBody or
            BuildExecutionMetric.TaskOutputs or
            BuildExecutionMetric.IntrinsicTask or
            BuildExecutionMetric.TaskYield or
            BuildExecutionMetric.TaskReacquire => true,
            _ => false,
        };
    }

    private static string[] CreateScopeNames()
    {
        string[] evaluation = Enum.GetNames(typeof(EvaluationPerformanceMetric));
        string[] execution = Enum.GetNames(typeof(BuildExecutionMetric));
        string[] names = new string[evaluation.Length + execution.Length + 1];
        for (int i = 0; i < evaluation.Length; i++)
        {
            names[i] = "evaluation/" + evaluation[i];
        }

        for (int i = 0; i < execution.Length; i++)
        {
            names[evaluation.Length + i] = "execution/" + execution[i];
        }

        names[^1] = "ledger/Calibration";
        return names;
    }

    private static long TicksToNanoseconds(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));

    private static string FormatMilliseconds(long nanoseconds) =>
        (nanoseconds / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture);

    internal enum CounterMode
    {
        Unavailable,
        TaskClockOnly,
        UserInstructions,
        UserAndKernelInstructions,
    }

    /// <summary>
    /// An open scope. The default value is an inactive scope.
    /// </summary>
    internal readonly struct Frame
    {
        internal Frame(ThreadLedger owner, int depth, int scope, long generation)
        {
            Owner = owner;
            Depth = depth;
            Scope = scope;
            Generation = generation;
        }

        internal ThreadLedger? Owner { get; }

        internal int Depth { get; }

        internal int Scope { get; }

        internal long Generation { get; }
    }

    internal struct Snapshot
    {
        internal long Instructions;
        internal long Cycles;
        internal long TaskNanoseconds;
        internal long WallTicks;
        internal long AllocatedBytes;

        public static Snapshot operator -(Snapshot left, Snapshot right) => new()
        {
            Instructions = left.Instructions - right.Instructions,
            Cycles = left.Cycles - right.Cycles,
            TaskNanoseconds = left.TaskNanoseconds - right.TaskNanoseconds,
            WallTicks = left.WallTicks - right.WallTicks,
            AllocatedBytes = left.AllocatedBytes - right.AllocatedBytes,
        };

        public static Snapshot operator +(Snapshot left, Snapshot right) => new()
        {
            Instructions = left.Instructions + right.Instructions,
            Cycles = left.Cycles + right.Cycles,
            TaskNanoseconds = left.TaskNanoseconds + right.TaskNanoseconds,
            WallTicks = left.WallTicks + right.WallTicks,
            AllocatedBytes = left.AllocatedBytes + right.AllocatedBytes,
        };
    }

    internal struct Entry
    {
        internal long Count;
        internal long Migrated;
        internal long Abandoned;
        internal Snapshot Inclusive;
        internal Snapshot Exclusive;

        internal void Add(in Entry other)
        {
            Count += other.Count;
            Migrated += other.Migrated;
            Abandoned += other.Abandoned;
            Inclusive += other.Inclusive;
            Exclusive += other.Exclusive;
        }
    }

    /// <summary>
    /// Frames removed from a thread by <see cref="Detach"/>, with the cost they accrued so far.
    /// </summary>
    internal sealed class DetachedFrames
    {
        internal DetachedFrames(int count)
        {
            Scopes = new int[count];
            Generations = new long[count];
            Carried = new Snapshot[count];
            Children = new Snapshot[count];
        }

        internal int[] Scopes { get; }

        internal long[] Generations { get; }

        internal Snapshot[] Carried { get; }

        internal Snapshot[] Children { get; }
    }

    private readonly record struct Calibration(
        double ParentInstructions,
        double ParentCycles,
        double SelfInstructions,
        double SelfCycles);

    internal sealed class ThreadLedger : IDisposable
    {
        private readonly PerfCounters _counters = PerfCounters.OpenForCurrentThread();
        private readonly int[] _scopes = new int[MaxDepth];
        private readonly long[] _generations = new long[MaxDepth];
        private readonly Snapshot[] _starts = new Snapshot[MaxDepth];
        private readonly Snapshot[] _carried = new Snapshot[MaxDepth];
        private readonly Snapshot[] _children = new Snapshot[MaxDepth];
        private readonly bool[] _migrated = new bool[MaxDepth];
        private readonly long _generationBase;
        private int _depth;
        private int _overflow;
        private long _nextGeneration;

        internal ThreadLedger(int scopeCount)
        {
            // Unique per ledger so frames carried to another thread keep a process-wide identity.
            _generationBase = Interlocked.Increment(ref s_ledgerCount) << 40;
            Entries = new Entry[scopeCount];
            ManagedThreadId = Environment.CurrentManagedThreadId;
            Name = Thread.CurrentThread.Name ?? string.Empty;
        }

        internal Entry[] Entries { get; }

        internal Snapshot RootTotal { get; private set; }

        internal int ManagedThreadId { get; }

        internal string Name { get; }

        internal long MultiplexedReads => _counters.MultiplexedReads;

        internal int Overflow => _overflow;

        internal Snapshot ReadTotal() => _counters.Read();

        internal Frame Enter(int scope)
        {
            TrimMigrated();
            if (_depth == MaxDepth)
            {
                _overflow++;
                return default;
            }

            int depth = _depth++;
            long generation = _generationBase | ++_nextGeneration;
            _scopes[depth] = scope;
            _generations[depth] = generation;
            _carried[depth] = default;
            _children[depth] = default;
            _migrated[depth] = false;
            _starts[depth] = Read();
            return new Frame(this, depth, scope, generation);
        }

        internal bool Holds(in Frame frame) =>
            frame.Depth < _depth && _generations[frame.Depth] == frame.Generation;

        internal void Exit(in Frame frame)
        {
            Snapshot now = Read();
            if (!Holds(frame))
            {
                // The frame was already removed (abandoned below it, or detached and never attached).
                Entries[frame.Scope].Abandoned++;
                return;
            }

            // Frames left open above this one never closed on this thread.
            while (_depth - 1 > frame.Depth)
            {
                _depth--;
                if (!_migrated[_depth])
                {
                    Entries[_scopes[_depth]].Abandoned++;
                }
            }

            _depth--;
            Snapshot inclusive = _carried[_depth] + (now - _starts[_depth]);
            ref Entry entry = ref Entries[frame.Scope];
            entry.Count++;
            entry.Inclusive += inclusive;
            entry.Exclusive += inclusive - _children[_depth];
            if (_depth > 0)
            {
                _children[_depth - 1] += inclusive;
            }
            else
            {
                RootTotal += inclusive;
            }
        }

        internal void MarkMigrated(in Frame frame)
        {
            // Called from another thread; the owner trims the frame on its next Enter.
            if (frame.Depth < MaxDepth && Volatile.Read(ref _generations[frame.Depth]) == frame.Generation)
            {
                Volatile.Write(ref _migrated[frame.Depth], true);
            }

            Interlocked.Increment(ref Entries[frame.Scope].Migrated);
        }

        internal DetachedFrames? Detach()
        {
            TrimMigrated();
            if (_depth == 0)
            {
                return null;
            }

            Snapshot now = Read();
            var frames = new DetachedFrames(_depth);
            for (int i = 0; i < _depth; i++)
            {
                frames.Scopes[i] = _scopes[i];
                frames.Generations[i] = _generations[i];
                frames.Carried[i] = _carried[i] + (now - _starts[i]);
                frames.Children[i] = _children[i];
            }

            // Frame handles stay valid: Attach restores the same generations at the same depths.
            _depth = 0;
            return frames;
        }

        internal void Attach(DetachedFrames frames)
        {
            TrimMigrated();
            if (_depth != 0 || frames.Scopes.Length > MaxDepth)
            {
                // Attaching on top of unrelated frames would corrupt nesting; drop the carried frames.
                foreach (int scope in frames.Scopes)
                {
                    Entries[scope].Abandoned++;
                }

                return;
            }

            Snapshot now = Read();
            for (int i = 0; i < frames.Scopes.Length; i++)
            {
                _scopes[i] = frames.Scopes[i];
                _generations[i] = frames.Generations[i];
                _carried[i] = frames.Carried[i];
                _children[i] = frames.Children[i];
                _migrated[i] = false;
                _starts[i] = now;
            }

            _depth = frames.Scopes.Length;
        }

        public void Dispose() => _counters.Dispose();

        private void TrimMigrated()
        {
            while (_depth > 0 && Volatile.Read(ref _migrated[_depth - 1]))
            {
                _depth--;
            }
        }

        private Snapshot Read()
        {
            Snapshot snapshot = _counters.Read();
            snapshot.WallTicks = Stopwatch.GetTimestamp();
            snapshot.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            return snapshot;
        }
    }

    /// <summary>
    /// A perf_event group for the current thread: instructions (leader), cycles, and task clock.
    /// </summary>
    private sealed unsafe class PerfCounters : IDisposable
    {
        private const uint PerfTypeHardware = 0;
        private const uint PerfTypeSoftware = 1;
        private const ulong PerfCountHardwareCpuCycles = 0;
        private const ulong PerfCountHardwareInstructions = 1;
        private const ulong PerfCountSoftwareTaskClock = 1;
        private const ulong FormatTotalTimeEnabled = 1;
        private const ulong FormatTotalTimeRunning = 2;
        private const ulong FormatGroup = 8;
        private const ulong FlagExcludeKernel = 1 << 5;
        private const ulong FlagExcludeHypervisor = 1 << 6;
        private const ulong PerfFlagFdCloexec = 8;
        private const int EACCES = 13;

        private static CounterMode s_mode = CounterMode.Unavailable;
        private static bool s_modeKnown;
        private static readonly object s_modeLock = new();

        private readonly int _leader = -1;
        private readonly int _cycles = -1;
        private readonly int _taskClock = -1;
        private readonly bool _hardware;
        private long _multiplexedReads;

        private PerfCounters(int leader, int cycles, int taskClock, bool hardware)
        {
            _leader = leader;
            _cycles = cycles;
            _taskClock = taskClock;
            _hardware = hardware;
        }

        internal static CounterMode Mode => s_mode;

        internal long MultiplexedReads => Volatile.Read(ref _multiplexedReads);

        internal static PerfCounters OpenForCurrentThread()
        {
            if (!OperatingSystem.IsLinux() || GetSyscallNumber() < 0)
            {
                return new PerfCounters(-1, -1, -1, hardware: false);
            }

            bool excludeKernel;
            lock (s_modeLock)
            {
                excludeKernel = s_modeKnown && s_mode != CounterMode.UserAndKernelInstructions;
            }

            int leader = Open(PerfTypeHardware, PerfCountHardwareInstructions, -1, excludeKernel);
            if (leader < 0 && !excludeKernel && Marshal.GetLastPInvokeError() == EACCES)
            {
                excludeKernel = true;
                leader = Open(PerfTypeHardware, PerfCountHardwareInstructions, -1, excludeKernel);
            }

            if (leader >= 0)
            {
                int cycles = Open(PerfTypeHardware, PerfCountHardwareCpuCycles, leader, excludeKernel);
                int taskClock = Open(PerfTypeSoftware, PerfCountSoftwareTaskClock, leader, excludeKernel);
                if (cycles >= 0 && taskClock >= 0)
                {
                    SetMode(excludeKernel ? CounterMode.UserInstructions : CounterMode.UserAndKernelInstructions);
                    return new PerfCounters(leader, cycles, taskClock, hardware: true);
                }

                Close(cycles);
                Close(taskClock);
                Close(leader);
            }

            // No usable hardware counters (for example, in a VM): fall back to the task clock alone.
            int clock = Open(PerfTypeSoftware, PerfCountSoftwareTaskClock, -1, excludeKernel: true);
            SetMode(clock >= 0 ? CounterMode.TaskClockOnly : CounterMode.Unavailable);
            return new PerfCounters(clock, -1, -1, hardware: false);
        }

        internal Snapshot Read()
        {
            Snapshot snapshot = default;
            if (_leader < 0)
            {
                return snapshot;
            }

            // Group layout: nr, time_enabled, time_running, values[nr].
            ulong* buffer = stackalloc ulong[6];
            nint expected = _hardware ? 6 * sizeof(ulong) : 4 * sizeof(ulong);
            if (read(_leader, buffer, expected) != expected)
            {
                return snapshot;
            }

            if (buffer[2] < buffer[1])
            {
                // The group was multiplexed with other perf users; counts cover only part of the time.
                Interlocked.Increment(ref _multiplexedReads);
            }

            if (_hardware)
            {
                snapshot.Instructions = (long)buffer[3];
                snapshot.Cycles = (long)buffer[4];
                snapshot.TaskNanoseconds = (long)buffer[5];
            }
            else
            {
                snapshot.TaskNanoseconds = (long)buffer[3];
            }

            return snapshot;
        }

        public void Dispose()
        {
            Close(_taskClock);
            Close(_cycles);
            Close(_leader);
        }

        private static void SetMode(CounterMode mode)
        {
            lock (s_modeLock)
            {
                if (!s_modeKnown || mode < s_mode)
                {
                    s_mode = mode;
                }

                s_modeKnown = true;
            }
        }

        private static int Open(uint type, ulong config, int groupFd, bool excludeKernel)
        {
            PerfEventAttr attr = default;
            attr.Type = type;
            attr.Size = (uint)sizeof(PerfEventAttr);
            attr.Config = config;
            attr.ReadFormat = FormatGroup | FormatTotalTimeEnabled | FormatTotalTimeRunning;
            attr.Flags = FlagExcludeHypervisor | (excludeKernel ? FlagExcludeKernel : 0);
            return (int)syscall(GetSyscallNumber(), &attr, pid: 0, cpu: -1, groupFd, PerfFlagFdCloexec);
        }

        private static void Close(int fd)
        {
            if (fd >= 0)
            {
                close(fd);
            }
        }

        private static long GetSyscallNumber() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 298,
            Architecture.Arm64 => 241,
            _ => -1,
        };

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, PerfEventAttr* attr, int pid, int cpu, int groupFd, ulong flags);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, void* buffer, nint count);

        [DllImport("libc")]
        private static extern int close(int fd);

        // perf_event_attr through sig_data (PERF_ATTR_SIZE_VER7, 128 bytes).
        [StructLayout(LayoutKind.Sequential)]
        private struct PerfEventAttr
        {
            public uint Type;
            public uint Size;
            public ulong Config;
            public ulong SamplePeriod;
            public ulong SampleType;
            public ulong ReadFormat;
            public ulong Flags;
            public uint WakeupEvents;
            public uint BreakpointType;
            public ulong Config1;
            public ulong Config2;
            public ulong BranchSampleType;
            public ulong SampleRegsUser;
            public uint SampleStackUser;
            public int ClockId;
            public ulong SampleRegsIntr;
            public uint AuxWatermark;
            public ushort SampleMaxStack;
            public ushort Reserved2;
            public uint AuxSampleSize;
            public uint Reserved3;
            public ulong SigData;
        }
    }
}

#else

namespace Microsoft.Build.Instrumentation;

/// <summary>
/// The cost ledger relies on Linux perf_event counters and is inactive on .NET Framework.
/// </summary>
internal static class CostLedger
{
    internal static bool Enabled => false;

    internal static Frame Enter(EvaluationPerformanceMetric metric) => default;

    internal static Frame Enter(BuildExecutionMetric metric) => default;

    internal static void Exit(in Frame frame)
    {
    }

    internal static DetachedFrames? Detach() => null;

    internal static void Attach(DetachedFrames? frames)
    {
    }

    internal readonly struct Frame
    {
    }

    internal sealed class DetachedFrames
    {
    }
}

#endif
