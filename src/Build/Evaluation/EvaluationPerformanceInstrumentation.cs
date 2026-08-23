// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    internal enum EvaluationPerformanceMetric
    {
        TotalEvaluation,
        InitialProperties,
        PropertiesAndImports,
        ItemDefinitions,
        ItemOperationConstruction,
        LazyItemApplication,
        LazyItemOperationApplication,
        UsingTasks,
        Targets,
        ModuleLowering,
        ConditionEvaluation,
        ConditionPoolWait,
        ConditionPoolContention,
        ConditionParsing,
        ConditionExpressionEvaluation,
        ItemSpecConstruction,
        MetadataAnalysis,
        UsingTaskRegistration,
        PropertyReplayCacheHit,
        PropertyReplayCacheMiss,
        PropertyReplayCacheContention,
        ConditionReplayCacheHit,
        ConditionReplayCacheMiss,
        ConditionReplayCacheContention,
        CompiledPropertyBatch,
        CompiledPropertyEffect,
        CompiledPropertyFold,
        CompiledPropertyExpansion,
        CompiledPropertyDeadStore,
        CompiledPropertyBlockCacheHit,
        CompiledPropertyBlockCacheMiss,
    }

    internal static class EvaluationPerformanceInstrumentation
    {
        private const string OutputDirectoryEnvironmentVariable =
            "MSBUILDEVALUATIONPROFILEDIRECTORY";
        private static readonly int s_metricCount =
            Enum.GetValues(typeof(EvaluationPerformanceMetric)).Length;
        private static readonly ThreadLocal<MetricAccumulator>
            s_threadMetrics =
                new ThreadLocal<MetricAccumulator>(
                    () => new MetricAccumulator(s_metricCount),
                    trackAllValues: true);
        private static readonly ConcurrentDictionary<
            string,
            ConditionContentionAccumulator> s_conditionContentions =
                new ConcurrentDictionary<
                    string,
                    ConditionContentionAccumulator>(
                        StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<
            string,
            CompiledPropertyModuleAccumulator>
            s_compiledPropertyModules =
                new ConcurrentDictionary<
                    string,
                    CompiledPropertyModuleAccumulator>(
                        StringComparer.OrdinalIgnoreCase);
        private static readonly object s_reportLock = new object();
        private static long s_completedEvaluations;
        private static readonly string s_outputDirectory =
            Environment.GetEnvironmentVariable(
                OutputDirectoryEnvironmentVariable);

        static EvaluationPerformanceInstrumentation()
        {
            if (Enabled)
            {
                AppDomain.CurrentDomain.ProcessExit +=
                    (_, _) => WriteReport();
            }
        }

        internal static bool Enabled =>
            !string.IsNullOrEmpty(s_outputDirectory);

        internal static Scope Measure(
            EvaluationPerformanceMetric metric) =>
            new Scope(metric);

        internal static long StartTimestamp() =>
            Enabled ? Stopwatch.GetTimestamp() : 0;

        internal static void RecordSince(
            EvaluationPerformanceMetric metric,
            long startTimestamp)
        {
            if (startTimestamp != 0)
            {
                Record(
                    metric,
                    Stopwatch.GetTimestamp() - startTimestamp);
            }
        }

        internal static void RecordEvent(
            EvaluationPerformanceMetric metric) =>
            RecordEvents(metric, 1);

        internal static void RecordEvents(
            EvaluationPerformanceMetric metric,
            int count)
        {
            if (Enabled && count != 0)
            {
                s_threadMetrics.Value.Counts[(int)metric] += count;
            }
        }

        internal static void RecordConditionContention(
            string condition,
            long startTimestamp)
        {
            if (startTimestamp == 0)
            {
                return;
            }

            long elapsedTicks =
                Stopwatch.GetTimestamp() - startTimestamp;
            Record(
                EvaluationPerformanceMetric.ConditionPoolContention,
                elapsedTicks);
            ConditionContentionAccumulator accumulator =
                s_conditionContentions.GetOrAdd(
                    condition,
                    static _ => new ConditionContentionAccumulator());
            Interlocked.Increment(ref accumulator.Count);
            Interlocked.Add(
                ref accumulator.ElapsedTicks,
                elapsedTicks);
        }

        internal static void RecordConstantPropertyBlock(
            EvaluationModule module,
            int effectCount)
        {
            if (!Enabled)
            {
                return;
            }

            CompiledPropertyModuleAccumulator accumulator =
                GetCompiledPropertyModuleAccumulator(module);
            Interlocked.Increment(
                ref accumulator.ConstantApplications);
            Interlocked.Add(
                ref accumulator.AppliedEffects,
                effectCount);
        }

        internal static void RecordPropertyBlockFallback(
            EvaluationModule module,
            CompiledPropertyBlockFallback fallback)
        {
            if (!Enabled)
            {
                return;
            }

            CompiledPropertyModuleAccumulator accumulator =
                GetCompiledPropertyModuleAccumulator(module);
            switch (fallback)
            {
                case CompiledPropertyBlockFallback.GlobalProperty:
                    Interlocked.Increment(
                        ref accumulator.GlobalPropertyFallbacks);
                    break;
                case CompiledPropertyBlockFallback.UndefinedInput:
                    Interlocked.Increment(
                        ref accumulator.UndefinedInputFallbacks);
                    break;
                case CompiledPropertyBlockFallback.Destination:
                    Interlocked.Increment(
                        ref accumulator.DestinationFallbacks);
                    break;
            }
        }

        internal static void RecordPropertyBlockSpecialization(
            EvaluationModule module,
            bool cacheHit,
            long startTimestamp,
            int effectCount,
            bool applied)
        {
            if (!Enabled)
            {
                return;
            }

            CompiledPropertyModuleAccumulator accumulator =
                GetCompiledPropertyModuleAccumulator(module);
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            if (cacheHit)
            {
                Interlocked.Increment(
                    ref accumulator.SpecializationHits);
                Interlocked.Add(
                    ref accumulator.SpecializationHitTicks,
                    elapsedTicks);
            }
            else
            {
                Interlocked.Increment(
                    ref accumulator.SpecializationMisses);
                Interlocked.Add(
                    ref accumulator.SpecializationMissTicks,
                    elapsedTicks);
            }

            if (applied)
            {
                Interlocked.Increment(
                    ref accumulator.SpecializationApplications);
                Interlocked.Add(
                    ref accumulator.AppliedEffects,
                    effectCount);
            }
        }

        internal static void WriteReportSnapshot()
        {
            if (Enabled)
            {
                WriteReport();
            }
        }

        internal static void RecordEvaluationCompleted()
        {
            if (!Enabled)
            {
                return;
            }

            long completed =
                Interlocked.Increment(ref s_completedEvaluations);
            if (completed == 1 || (completed & 255) == 0)
            {
                WriteReport();
            }
        }

        private static CompiledPropertyModuleAccumulator
            GetCompiledPropertyModuleAccumulator(EvaluationModule module)
        {
            string path = module.Source.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                path = module.Source.Location?.File ?? "<in-memory>";
            }

            return s_compiledPropertyModules.GetOrAdd(
                path,
                static _ => new CompiledPropertyModuleAccumulator());
        }

        private static void Record(
            EvaluationPerformanceMetric metric,
            long elapsedTicks)
        {
            int index = (int)metric;
            MetricAccumulator metrics = s_threadMetrics.Value;
            metrics.Counts[index]++;
            metrics.ElapsedTicks[index] += elapsedTicks;
        }

        private static void WriteReport()
        {
            lock (s_reportLock)
            {
                WriteReportCore();
            }
        }

        private static void WriteReportCore()
        {
            Directory.CreateDirectory(s_outputDirectory);
            int processId;
            string processName;
            using (Process process = Process.GetCurrentProcess())
            {
                processId = process.Id;
                processName = process.ProcessName;
            }

            var report = new StringBuilder();
            report.Append("process_id\t");
            report.AppendLine(processId.ToString(CultureInfo.InvariantCulture));
            report.Append("process_name\t");
            report.AppendLine(Escape(processName));
            report.Append("compiled_modules\t");
            report.AppendLine(
                Traits.Instance.EnableCompiledModuleEvaluation
                    ? "true"
                    : "false");
            report.Append("compiled_module_replay\t");
            report.AppendLine(
                Traits.Instance.EnableCompiledModuleReplay
                    ? "true"
                    : "false");
            report.Append("compiled_module_effect_batching\t");
            report.AppendLine(
                Traits.Instance.EnableCompiledModuleEffectBatching
                    ? "true"
                    : "false");
            report.Append("command_line\t");
            report.AppendLine(Escape(Environment.CommandLine));
            report.Append("timestamp_frequency\t");
            report.AppendLine(
                Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("metric\tcount\telapsed_ticks\telapsed_ms");

            var counts = new long[s_metricCount];
            var elapsedTicksByMetric = new long[s_metricCount];
            foreach (MetricAccumulator threadMetrics
                     in s_threadMetrics.Values)
            {
                for (int i = 0; i < s_metricCount; i++)
                {
                    counts[i] += threadMetrics.Counts[i];
                    elapsedTicksByMetric[i] +=
                        threadMetrics.ElapsedTicks[i];
                }
            }

            foreach (EvaluationPerformanceMetric metric
                     in Enum.GetValues(
                         typeof(EvaluationPerformanceMetric)))
            {
                long count = counts[(int)metric];
                long elapsedTicks =
                    elapsedTicksByMetric[(int)metric];
                double elapsedMilliseconds =
                    elapsedTicks * 1000.0 / Stopwatch.Frequency;
                report.Append(metric);
                report.Append('\t');
                report.Append(
                    count.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.Append(
                    elapsedTicks.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.AppendLine(
                    elapsedMilliseconds.ToString(
                        "F3",
                        CultureInfo.InvariantCulture));
            }

            report.AppendLine(
                "condition_contention\tcount\telapsed_ticks\telapsed_ms");
            foreach (KeyValuePair<
                         string,
                         ConditionContentionAccumulator> entry
                     in s_conditionContentions
                         .OrderByDescending(
                             pair => Interlocked.Read(
                                 ref pair.Value.ElapsedTicks))
                         .Take(20))
            {
                long count = Interlocked.Read(ref entry.Value.Count);
                long elapsedTicks =
                    Interlocked.Read(ref entry.Value.ElapsedTicks);
                report.Append(Escape(entry.Key));
                report.Append('\t');
                report.Append(
                    count.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.Append(
                    elapsedTicks.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.AppendLine(
                    (elapsedTicks * 1000.0 / Stopwatch.Frequency)
                        .ToString("F3", CultureInfo.InvariantCulture));
            }

            report.AppendLine(
                "compiled_property_module\tconstant_applications\t" +
                "specialization_hits\tspecialization_misses\t" +
                "specialization_applications\tglobal_fallbacks\t" +
                "undefined_fallbacks\tdestination_fallbacks\t" +
                "applied_effects\thit_ms\tmiss_ms");
            foreach (KeyValuePair<
                         string,
                         CompiledPropertyModuleAccumulator> entry
                     in s_compiledPropertyModules
                         .OrderByDescending(
                             pair =>
                                 Interlocked.Read(
                                     ref pair.Value.AppliedEffects))
                         .ThenBy(pair => pair.Key))
            {
                CompiledPropertyModuleAccumulator accumulator =
                    entry.Value;
                report.Append(Escape(entry.Key));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.ConstantApplications));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.SpecializationHits));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.SpecializationMisses));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.SpecializationApplications));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.GlobalPropertyFallbacks));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.UndefinedInputFallbacks));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.DestinationFallbacks));
                report.Append('\t');
                report.Append(
                    Read(ref accumulator.AppliedEffects));
                report.Append('\t');
                report.Append(
                    ToMilliseconds(
                            Interlocked.Read(
                                ref accumulator.SpecializationHitTicks))
                        .ToString("F3", CultureInfo.InvariantCulture));
                report.Append('\t');
                report.AppendLine(
                    ToMilliseconds(
                            Interlocked.Read(
                                ref accumulator.SpecializationMissTicks))
                        .ToString("F3", CultureInfo.InvariantCulture));
            }

            string outputPath = Path.Combine(
                s_outputDirectory,
                $"evaluation-profile-{processId}.tsv");
            File.WriteAllText(outputPath, report.ToString());
        }

        private static string Read(ref long value) =>
            Interlocked.Read(ref value)
                .ToString(CultureInfo.InvariantCulture);

        private static double ToMilliseconds(long elapsedTicks) =>
            elapsedTicks * 1000.0 / Stopwatch.Frequency;

        private static string Escape(string value) =>
            value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ??
            string.Empty;

        private sealed class MetricAccumulator
        {
            internal MetricAccumulator(int metricCount)
            {
                Counts = new long[metricCount];
                ElapsedTicks = new long[metricCount];
            }

            internal long[] Counts { get; }

            internal long[] ElapsedTicks { get; }
        }

        private sealed class ConditionContentionAccumulator
        {
            internal long Count;
            internal long ElapsedTicks;
        }

        private sealed class CompiledPropertyModuleAccumulator
        {
            internal long ConstantApplications;
            internal long SpecializationHits;
            internal long SpecializationMisses;
            internal long SpecializationApplications;
            internal long GlobalPropertyFallbacks;
            internal long UndefinedInputFallbacks;
            internal long DestinationFallbacks;
            internal long AppliedEffects;
            internal long SpecializationHitTicks;
            internal long SpecializationMissTicks;
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly EvaluationPerformanceMetric _metric;
            private readonly long _startTimestamp;

            internal Scope(EvaluationPerformanceMetric metric)
            {
                _metric = metric;
                _startTimestamp =
                    Enabled ? Stopwatch.GetTimestamp() : 0;
            }

            public void Dispose()
            {
                if (_startTimestamp != 0)
                {
                    Record(
                        _metric,
                        Stopwatch.GetTimestamp() - _startTimestamp);
                }
            }
        }
    }

    internal enum CompiledPropertyBlockFallback
    {
        GlobalProperty,
        UndefinedInput,
        Destination,
    }
}
