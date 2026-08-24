// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
#if !NETFRAMEWORK
using System.Diagnostics.Metrics;
#endif

#nullable disable

namespace Microsoft.Build.BackEnd
{
    internal enum BuildExecutionMetric
    {
        BuildSession,
        ProjectDispatch,
        ProjectBuild,
        ProjectBlocked,
        TargetBuilder,
        Target,
        TaskBuilder,
        Task,
        IntrinsicTask,
        TaskYield,
        TaskReacquire,
    }

    internal static class BuildExecutionInstrumentation
    {
        internal const string MeterName = "Microsoft.Build.Execution";
        internal const string DetailsMeterName =
            "Microsoft.Build.Execution.Details";
        internal const string ElapsedInstrumentName =
            "msbuild.execution.elapsed";
        internal const string EventInstrumentName =
            "msbuild.execution.events";

#if !NETFRAMEWORK
        private static readonly string[] s_metricNames =
            Enum.GetNames(typeof(BuildExecutionMetric));
        private static readonly Meter s_meter = new(MeterName);
        private static readonly Meter s_detailsMeter =
            new(DetailsMeterName);
        private static readonly Counter<double> s_elapsed =
            s_meter.CreateCounter<double>(
                ElapsedInstrumentName,
                "ms",
                "Cumulative elapsed build execution time.");
        private static readonly Counter<long> s_events =
            s_meter.CreateCounter<long>(
                EventInstrumentName,
                "{event}",
                "Build execution event count.");
        private static readonly Counter<double> s_detailElapsed =
            s_detailsMeter.CreateCounter<double>(
                ElapsedInstrumentName,
                "ms",
                "Cumulative elapsed build execution time by name.");
        private static readonly Counter<long> s_detailEvents =
            s_detailsMeter.CreateCounter<long>(
                EventInstrumentName,
                "{event}",
                "Build execution event count by name.");
#endif

        internal static bool DetailsEnabled =>
#if NETFRAMEWORK
            false;
#else
            s_detailElapsed.Enabled || s_detailEvents.Enabled;
#endif

        internal static Scope Measure(
            BuildExecutionMetric metric,
            string name = null,
            string parentName = null) =>
            new(metric, name, parentName);

        internal static long StartTimestamp() =>
#if NETFRAMEWORK
            0;
#else
            s_elapsed.Enabled ||
            s_events.Enabled ||
            s_detailElapsed.Enabled ||
            s_detailEvents.Enabled
                ? Stopwatch.GetTimestamp()
                : 0;
#endif

        internal static void RecordSince(
            BuildExecutionMetric metric,
            long startTimestamp,
            string name = null,
            string parentName = null)
        {
            if (startTimestamp == 0)
            {
                return;
            }

            Record(
                metric,
                Stopwatch.GetTimestamp() - startTimestamp,
                name,
                parentName,
                recordCount: true);
        }

        private static void Record(
            BuildExecutionMetric metric,
            long elapsedTicks,
            string name,
            string parentName,
            bool recordCount)
        {
#if !NETFRAMEWORK
            string metricName = s_metricNames[(int)metric];
            double elapsedMilliseconds =
                elapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (s_elapsed.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", metricName);
                s_elapsed.Add(elapsedMilliseconds, tags);
            }

            if (recordCount && s_events.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", metricName);
                s_events.Add(1, tags);
            }

            if (s_detailElapsed.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", metricName);
                if (name is not null)
                {
                    tags.Add("name", name);
                }

                if (parentName is not null)
                {
                    tags.Add("parent.name", parentName);
                }

                s_detailElapsed.Add(elapsedMilliseconds, tags);
            }

            if (recordCount && s_detailEvents.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", metricName);
                if (name is not null)
                {
                    tags.Add("name", name);
                }

                if (parentName is not null)
                {
                    tags.Add("parent.name", parentName);
                }

                s_detailEvents.Add(1, tags);
            }
#endif
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly BuildExecutionMetric _metric;
            private readonly string _name;
            private readonly string _parentName;
            private readonly long _startTimestamp;
            private readonly bool _recordCount;

            internal Scope(
                BuildExecutionMetric metric,
                string name,
                string parentName)
            {
                _metric = metric;
                _name = name;
                _parentName = parentName;
                _startTimestamp =
#if NETFRAMEWORK
                    0;
#else
                    s_elapsed.Enabled || s_detailElapsed.Enabled
                        ? Stopwatch.GetTimestamp()
                        : 0;
#endif
                _recordCount =
#if NETFRAMEWORK
                    false;
#else
                    s_events.Enabled || s_detailEvents.Enabled;
#endif
            }

            public void Dispose()
            {
                if (_startTimestamp != 0)
                {
                    Record(
                        _metric,
                        Stopwatch.GetTimestamp() - _startTimestamp,
                        _name,
                        _parentName,
                        _recordCount);
                }
                else if (_recordCount)
                {
                    Record(
                        _metric,
                        0,
                        _name,
                        _parentName,
                        recordCount: true);
                }
            }
        }
    }
}
