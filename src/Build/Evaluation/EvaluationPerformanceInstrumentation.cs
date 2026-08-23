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

            string outputPath = Path.Combine(
                s_outputDirectory,
                $"evaluation-profile-{processId}.tsv");
            File.WriteAllText(outputPath, report.ToString());
        }

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
}
