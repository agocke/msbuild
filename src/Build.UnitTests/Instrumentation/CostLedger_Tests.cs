// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System;
using System.IO;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Instrumentation;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.Instrumentation;

[Collection(nameof(CostLedger_Tests))]
public sealed class CostLedger_Tests : IDisposable
{
    private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"costledger-{Guid.NewGuid():N}.tsv");

    public CostLedger_Tests() => CostLedger.Configure(_outputPath, level: 2);

    public void Dispose()
    {
        CostLedger.DisableForTesting();
        File.Delete(_outputPath);
    }

    [Fact]
    public void NestedScopesSubtractChildrenFromExclusiveCost()
    {
        (CostLedger.Entry outer, CostLedger.Entry inner) = RunOnNewThread(() =>
        {
            CostLedger.Frame outerFrame = CostLedger.Enter(BuildExecutionMetric.FastTaskSite);
            for (int i = 0; i < 3; i++)
            {
                CostLedger.Frame innerFrame = CostLedger.Enter(BuildExecutionMetric.FastTaskAction);
                Spin();
                CostLedger.Exit(innerFrame);
            }

            CostLedger.Exit(outerFrame);
            return (
                CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.FastTaskSite),
                CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.FastTaskAction));
        });

        outer.Count.ShouldBe(1);
        inner.Count.ShouldBe(3);
        (outer.Inclusive.WallTicks - outer.Exclusive.WallTicks).ShouldBe(inner.Inclusive.WallTicks);
        (outer.Inclusive.Instructions - outer.Exclusive.Instructions).ShouldBe(inner.Inclusive.Instructions);
        inner.Inclusive.AllocatedBytes.ShouldBeGreaterThan(0);
        if (CostLedger.Mode is CostLedger.CounterMode.UserInstructions or CostLedger.CounterMode.UserAndKernelInstructions)
        {
            inner.Inclusive.Instructions.ShouldBeGreaterThan(0);
            inner.Inclusive.Cycles.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void DetachedFramesCloseOnAnotherThreadAfterAttach()
    {
        CostLedger.Frame frame = default;
        CostLedger.DetachedFrames? detached = RunOnNewThread(() =>
        {
            frame = CostLedger.Enter(BuildExecutionMetric.ProjectBlocked);
            Spin();
            return CostLedger.Detach();
        });

        detached.ShouldNotBeNull();
        CostLedger.Entry entry = RunOnNewThread(() =>
        {
            CostLedger.Attach(detached);
            Spin();
            CostLedger.Exit(frame);
            return CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.ProjectBlocked);
        });

        entry.Count.ShouldBe(1);
        entry.Migrated.ShouldBe(0);
        entry.Abandoned.ShouldBe(0);
        entry.Inclusive.AllocatedBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FramesClosedOnAnotherThreadWithoutAttachAreCountedAsMigrated()
    {
        CostLedger.Frame frame = default;
        long migratedBefore = 0;
        long migratedAfter = RunOnNewThread(() =>
        {
            frame = CostLedger.Enter(BuildExecutionMetric.TaskYield);
            migratedBefore = CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.TaskYield).Migrated;
            RunOnNewThread(() =>
            {
                CostLedger.Exit(frame);
                return 0;
            });

            // The next scope on the owning thread trims the migrated frame instead of nesting under it.
            CostLedger.Frame next = CostLedger.Enter(BuildExecutionMetric.TaskReacquire);
            CostLedger.Exit(next);
            CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.TaskReacquire).Count.ShouldBe(1);
            return CostLedger.GetCurrentThreadEntryForTesting(BuildExecutionMetric.TaskYield).Migrated;
        });

        (migratedAfter - migratedBefore).ShouldBe(1);
    }

    [Fact]
    public void DumpWritesHeaderAndScopeRows()
    {
        RunOnNewThread(() =>
        {
            CostLedger.Frame frame = CostLedger.Enter(BuildExecutionMetric.FastTaskCreate);
            CostLedger.Exit(frame);
            return 0;
        });

        CostLedger.Dump();
        string[] lines = File.ReadAllLines(_outputPath);
        lines[0].ShouldStartWith("# process=");
        lines.ShouldContain(line => line.StartsWith("# probe_cost_per_scope ", StringComparison.Ordinal));
        lines.ShouldContain(line => line.StartsWith("scope\tcount\t", StringComparison.Ordinal));
        lines.ShouldContain(line => line.StartsWith("execution/FastTaskCreate\t", StringComparison.Ordinal));
    }

    private static T RunOnNewThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Ledger test thread failed.", failure);
        }

        return result;
    }

    private static void Spin()
    {
        // Allocate and compute so every counter advances.
        int[] values = new int[256];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i * i;
        }

        GC.KeepAlive(values);
    }
}
#endif
