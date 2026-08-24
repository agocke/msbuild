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
        CompiledConditionEvaluation,
        ScalarConditionEvaluation,
        ItemSpecConstruction,
        MetadataAnalysis,
        LazyItemIncludeApplication,
        LazyItemRemoveApplication,
        LazyItemUpdateApplication,
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
        CompiledPropertyFunction,
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
        private static readonly ConcurrentDictionary<
            string,
            CompiledPropertyExpansionAccumulator>
            s_compiledPropertyExpansions =
                new ConcurrentDictionary<
                    string,
                    CompiledPropertyExpansionAccumulator>(
                        StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<
            (string Shape, string Condition),
            long> s_conditionShapes = new();
        private static readonly ConcurrentDictionary<
            string,
            long> s_conditionContexts =
                new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<
            (string Context, string Condition),
            long> s_conditionContextShapes = new();
        private static readonly ConcurrentDictionary<
            (EvaluationPerformanceMetric Kind, string ItemType, string Expression),
            LazyItemOperationShapeAccumulator>
            s_lazyItemOperationShapes = new();
        private static readonly object s_reportLock = new object();
        private static long s_completedEvaluations;
        private static long s_compiledPropertyModuleCount;
        private static long s_compiledPropertyValuePartCount;
        private static long s_compiledPropertyFunctionCount;
        private static long s_compiledPropertyFunctionArgumentCount;
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

        internal static void RecordCompiledPropertyExpansion(
            string expression)
        {
            if (!Enabled)
            {
                return;
            }

            CompiledPropertyExpansionAccumulator accumulator =
                s_compiledPropertyExpansions.GetOrAdd(
                    expression,
                    static _ =>
                        new CompiledPropertyExpansionAccumulator());
            Interlocked.Increment(ref accumulator.Count);
        }

        internal static void RecordCompiledPropertyModuleShape(
            int valuePartCount,
            int functionCount,
            int functionArgumentCount)
        {
            if (!Enabled)
            {
                return;
            }

            Interlocked.Increment(ref s_compiledPropertyModuleCount);
            Interlocked.Add(
                ref s_compiledPropertyValuePartCount,
                valuePartCount);
            Interlocked.Add(
                ref s_compiledPropertyFunctionCount,
                functionCount);
            Interlocked.Add(
                ref s_compiledPropertyFunctionArgumentCount,
                functionArgumentCount);
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

        internal static void RecordConditionShape(
            string shape,
            string condition)
        {
            if (Enabled)
            {
                s_conditionShapes.AddOrUpdate(
                    (shape, condition),
                    1,
                    static (_, count) => count + 1);
            }
        }

        internal static void RecordConditionContext(
            string context,
            string condition)
        {
            if (Enabled)
            {
                s_conditionContexts.AddOrUpdate(
                    context,
                    1,
                    static (_, count) => count + 1);
                s_conditionContextShapes.AddOrUpdate(
                    (context, condition),
                    1,
                    static (_, count) => count + 1);
            }
        }

        internal static LazyItemOperationShapeScope
            MeasureLazyItemOperationShape(
            EvaluationPerformanceMetric kind,
            string itemType,
            string expression) =>
            new LazyItemOperationShapeScope(
                kind,
                itemType,
                expression);

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
#if NETCOREAPP
            GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
            report.Append("managed_heap_bytes\t");
            report.AppendLine(
                GC.GetTotalMemory(false)
                    .ToString(CultureInfo.InvariantCulture));
            report.Append("gc_heap_size_bytes\t");
            report.AppendLine(
                memoryInfo.HeapSizeBytes
                    .ToString(CultureInfo.InvariantCulture));
            report.Append("gc_fragmented_bytes\t");
            report.AppendLine(
                memoryInfo.FragmentedBytes
                    .ToString(CultureInfo.InvariantCulture));
            report.Append("gc_total_committed_bytes\t");
            report.AppendLine(
                memoryInfo.TotalCommittedBytes
                    .ToString(CultureInfo.InvariantCulture));
#endif
            report.Append("compiled_property_module_count\t");
            report.AppendLine(
                Read(ref s_compiledPropertyModuleCount));
            report.Append("compiled_property_value_part_count\t");
            report.AppendLine(
                Read(ref s_compiledPropertyValuePartCount));
            report.Append("compiled_property_function_record_count\t");
            report.AppendLine(
                Read(ref s_compiledPropertyFunctionCount));
            report.Append("compiled_property_function_argument_count\t");
            report.AppendLine(
                Read(ref s_compiledPropertyFunctionArgumentCount));
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
                         .ToArray()
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

            report.AppendLine("condition_shape\tcondition\tcount");
            foreach (KeyValuePair<
                         (string Shape, string Condition),
                         long> entry
                     in s_conditionShapes
                         .ToArray()
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key.Shape)
                         .ThenBy(pair => pair.Key.Condition)
                         .Take(300))
            {
                report.Append(Escape(entry.Key.Shape));
                report.Append('\t');
                report.Append(Escape(entry.Key.Condition));
                report.Append('\t');
                report.AppendLine(
                    entry.Value.ToString(CultureInfo.InvariantCulture));
            }

            report.AppendLine("condition_context\tcount");
            foreach (KeyValuePair<string, long> entry
                     in s_conditionContexts
                         .ToArray()
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key))
            {
                report.Append(Escape(entry.Key));
                report.Append('\t');
                report.AppendLine(
                    entry.Value.ToString(CultureInfo.InvariantCulture));
            }

            report.AppendLine("condition_context_shape\tcondition\tcount");
            foreach (KeyValuePair<(string Context, string Condition), long> entry
                     in s_conditionContextShapes
                         .ToArray()
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key.Context)
                         .ThenBy(pair => pair.Key.Condition)
                         .Take(300))
            {
                report.Append(Escape(entry.Key.Context));
                report.Append('\t');
                report.Append(Escape(entry.Key.Condition));
                report.Append('\t');
                report.AppendLine(
                    entry.Value.ToString(CultureInfo.InvariantCulture));
            }

            report.AppendLine(
                "lazy_item_operation\titem_type\texpression\tcount\t" +
                "elapsed_ticks\telapsed_ms");
            foreach (KeyValuePair<
                         (EvaluationPerformanceMetric Kind, string ItemType, string Expression),
                         LazyItemOperationShapeAccumulator> entry
                     in s_lazyItemOperationShapes
                         .ToArray()
                         .OrderByDescending(
                             pair => Interlocked.Read(
                                 ref pair.Value.ElapsedTicks))
                         .ThenBy(pair => pair.Key.Kind)
                         .ThenBy(pair => pair.Key.ItemType)
                         .ThenBy(pair => pair.Key.Expression)
                         .Take(500))
            {
                long count =
                    Interlocked.Read(ref entry.Value.Count);
                long elapsedTicks =
                    Interlocked.Read(ref entry.Value.ElapsedTicks);
                report.Append(entry.Key.Kind);
                report.Append('\t');
                report.Append(Escape(entry.Key.ItemType));
                report.Append('\t');
                report.Append(Escape(entry.Key.Expression));
                report.Append('\t');
                report.Append(
                    count.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.Append(
                    elapsedTicks.ToString(CultureInfo.InvariantCulture));
                report.Append('\t');
                report.AppendLine(
                    ToMilliseconds(elapsedTicks)
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
                         .ToArray()
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

            report.AppendLine("compiled_property_expansion\tcount");
            foreach (KeyValuePair<
                         string,
                         CompiledPropertyExpansionAccumulator> entry
                     in s_compiledPropertyExpansions
                         .ToArray()
                         .OrderByDescending(
                             pair =>
                                 Interlocked.Read(ref pair.Value.Count))
                         .ThenBy(pair => pair.Key))
            {
                report.Append(Escape(entry.Key));
                report.Append('\t');
                report.AppendLine(Read(ref entry.Value.Count));
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

        private sealed class CompiledPropertyExpansionAccumulator
        {
            internal long Count;
        }

        private sealed class LazyItemOperationShapeAccumulator
        {
            internal long Count;
            internal long ElapsedTicks;
        }

        internal readonly struct LazyItemOperationShapeScope :
            IDisposable
        {
            private readonly (
                EvaluationPerformanceMetric Kind,
                string ItemType,
                string Expression) _key;
            private readonly long _startTimestamp;

            internal LazyItemOperationShapeScope(
                EvaluationPerformanceMetric kind,
                string itemType,
                string expression)
            {
                _key = (kind, itemType, expression);
                _startTimestamp =
                    Enabled ? Stopwatch.GetTimestamp() : 0;
            }

            public void Dispose()
            {
                if (_startTimestamp == 0)
                {
                    return;
                }

                long elapsedTicks =
                    Stopwatch.GetTimestamp() - _startTimestamp;
                LazyItemOperationShapeAccumulator accumulator =
                    s_lazyItemOperationShapes.GetOrAdd(
                        _key,
                        static _ =>
                            new LazyItemOperationShapeAccumulator());
                Interlocked.Increment(ref accumulator.Count);
                Interlocked.Add(
                    ref accumulator.ElapsedTicks,
                    elapsedTicks);
            }
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
