// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
#if !NETFRAMEWORK
using System.Diagnostics.Metrics;
#endif

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
        CompiledItemSpecConstruction,
        CachedItemSpecConstruction,
        CompiledItemSpecExpansion,
        ScalarItemSpecExpansion,
        ScalarItemSpecConstruction,
        MetadataAnalysis,
        LazyItemIncludeApplication,
        LazyItemRemoveApplication,
        LazyItemUpdateApplication,
        LazyItemUpdateSelection,
        LazyItemMetadataDecoration,
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
        internal const string MeterName = "Microsoft.Build.Evaluation";
        internal const string DetailsMeterName =
            "Microsoft.Build.Evaluation.Details";
        internal const string DurationInstrumentName =
            "msbuild.evaluation.elapsed";
        internal const string EventInstrumentName =
            "msbuild.evaluation.events";
        internal const string ConditionShapeInstrumentName =
            "msbuild.evaluation.condition.shape";
        internal const string ConditionContextInstrumentName =
            "msbuild.evaluation.condition.context";
        internal const string LazyItemElapsedInstrumentName =
            "msbuild.evaluation.lazy_item.elapsed";
        internal const string LazyItemEventInstrumentName =
            "msbuild.evaluation.lazy_item.operations";

#if !NETFRAMEWORK
        private static readonly string[] s_metricNames =
            Enum.GetNames(typeof(EvaluationPerformanceMetric));
        private static readonly Meter s_meter = new(MeterName);
        private static readonly Meter s_detailsMeter =
            new(DetailsMeterName);
        private static readonly Counter<double> s_duration =
            s_meter.CreateCounter<double>(
                DurationInstrumentName,
                "ms",
                "Cumulative elapsed evaluation time.");
        private static readonly Counter<long> s_events =
            s_meter.CreateCounter<long>(
                EventInstrumentName,
                "{event}",
                "Evaluation event count.");
        private static readonly Counter<double> s_detailDuration =
            s_detailsMeter.CreateCounter<double>(
                DurationInstrumentName,
                "ms",
                "Cumulative elapsed evaluation time by diagnostic detail.");
        private static readonly Counter<long> s_detailEvents =
            s_detailsMeter.CreateCounter<long>(
                EventInstrumentName,
                "{event}",
                "Evaluation event count by diagnostic detail.");
        private static readonly Counter<long> s_conditionShapes =
            s_detailsMeter.CreateCounter<long>(
                ConditionShapeInstrumentName,
                "{condition}",
                "Condition evaluations by expression shape.");
        private static readonly Counter<long> s_conditionContexts =
            s_detailsMeter.CreateCounter<long>(
                ConditionContextInstrumentName,
                "{condition}",
                "Condition evaluations by source context.");
        private static readonly Counter<double> s_lazyItemDuration =
            s_detailsMeter.CreateCounter<double>(
                LazyItemElapsedInstrumentName,
                "ms",
                "Cumulative elapsed lazy item operation time.");
        private static readonly Counter<long> s_lazyItemEvents =
            s_detailsMeter.CreateCounter<long>(
                LazyItemEventInstrumentName,
                "{operation}",
                "Lazy item operation count.");
#endif

        internal static bool Enabled =>
#if NETFRAMEWORK
            false;
#else
            s_duration.Enabled ||
            s_events.Enabled ||
            s_detailDuration.Enabled ||
            s_detailEvents.Enabled ||
            s_conditionShapes.Enabled ||
            s_conditionContexts.Enabled ||
            s_lazyItemDuration.Enabled ||
            s_lazyItemEvents.Enabled;
#endif

        internal static bool ConditionContentionEnabled =>
#if NETFRAMEWORK
            false;
#else
            s_duration.Enabled || s_detailDuration.Enabled;
#endif

        internal static bool LazyItemOperationMetricsEnabled =>
#if NETFRAMEWORK
            false;
#else
            s_duration.Enabled ||
            s_events.Enabled ||
            s_lazyItemDuration.Enabled ||
            s_lazyItemEvents.Enabled;
#endif

        internal static bool LazyItemShapeEnabled =>
#if NETFRAMEWORK
            false;
#else
            s_lazyItemDuration.Enabled ||
            s_lazyItemEvents.Enabled;
#endif

        internal static Scope Measure(
            EvaluationPerformanceMetric metric) =>
            new(metric);

        internal static long StartTimestamp() =>
#if NETFRAMEWORK
            0;
#else
            s_duration.Enabled ? Stopwatch.GetTimestamp() : 0;
#endif

        internal static long StartContentionTimestamp() =>
#if NETFRAMEWORK
            0;
#else
            ConditionContentionEnabled ? Stopwatch.GetTimestamp() : 0;
#endif

        internal static void RecordSince(
            EvaluationPerformanceMetric metric,
            long startTimestamp)
        {
            if (startTimestamp != 0)
            {
                RecordDuration(
                    metric,
                    Stopwatch.GetTimestamp() - startTimestamp);
                RecordEvent(metric);
            }
        }

        internal static void RecordEvent(
            EvaluationPerformanceMetric metric) =>
            RecordEvents(metric, 1);

        internal static void RecordEvents(
            EvaluationPerformanceMetric metric,
            int count)
        {
#if !NETFRAMEWORK
            if (count != 0 && s_events.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", GetMetricName(metric));
                s_events.Add(count, tags);
            }
#endif
        }

        internal static void RecordCompiledPropertyExpansion(
            string expression)
        {
#if !NETFRAMEWORK
            if (s_detailEvents.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", "compiled_property_expansion");
                tags.Add("expression", expression);
                s_detailEvents.Add(1, tags);
            }
#endif
        }

        internal static void RecordCompiledPropertyModuleShape(
            int valuePartCount,
            int functionCount,
            int functionArgumentCount)
        {
#if !NETFRAMEWORK
            if (!s_events.Enabled)
            {
                return;
            }

            AddEvent(
                s_events,
                "compiled_property_module",
                1);
            AddEvent(
                s_events,
                "compiled_property_value_part",
                valuePartCount);
            AddEvent(
                s_events,
                "compiled_property_function",
                functionCount);
            AddEvent(
                s_events,
                "compiled_property_function_argument",
                functionArgumentCount);
#endif
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
            RecordDuration(
                EvaluationPerformanceMetric.ConditionPoolContention,
                elapsedTicks);
            RecordDetailDuration(
                EvaluationPerformanceMetric.ConditionPoolContention,
                elapsedTicks,
                "condition",
                condition);
            RecordEvent(
                EvaluationPerformanceMetric.ConditionPoolContention);
        }

        internal static void RecordConditionShape(
            string shape,
            string condition)
        {
#if !NETFRAMEWORK
            if (s_conditionShapes.Enabled)
            {
                TagList tags = default;
                tags.Add("shape", shape);
                tags.Add("condition", condition);
                s_conditionShapes.Add(1, tags);
            }
#endif
        }

        internal static void RecordConditionContext(
            string context,
            string condition)
        {
#if !NETFRAMEWORK
            if (s_conditionContexts.Enabled)
            {
                TagList tags = default;
                tags.Add("context", context);
                tags.Add("condition", condition);
                s_conditionContexts.Add(1, tags);
            }
#endif
        }

        internal static LazyItemOperationShapeScope
            MeasureLazyItemOperationShape(
            EvaluationPerformanceMetric kind,
            string itemType,
            string expression) =>
            new(kind, itemType, expression);

        internal static void RecordConstantPropertyBlock(
            EvaluationModule module,
            int effectCount)
        {
#if !NETFRAMEWORK
            if (!s_events.Enabled && !s_detailEvents.Enabled)
            {
                return;
            }

            AddEvent(
                s_events,
                "compiled_property_constant_application",
                1);
            AddEvent(
                s_events,
                "compiled_property_applied_effect",
                effectCount);
            if (s_detailEvents.Enabled)
            {
                string modulePath = GetModulePath(module);
                AddEvent(
                    s_detailEvents,
                    "compiled_property_constant_application",
                    1,
                    "module",
                    modulePath);
                AddEvent(
                    s_detailEvents,
                    "compiled_property_applied_effect",
                    effectCount,
                    "module",
                    modulePath);
            }
#endif
        }

        internal static void RecordPropertyBlockFallback(
            EvaluationModule module,
            CompiledPropertyBlockFallback fallback)
        {
#if !NETFRAMEWORK
            if (s_events.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", "compiled_property_fallback");
                tags.Add("reason", fallback.ToString());
                s_events.Add(1, tags);
            }

            if (s_detailEvents.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", "compiled_property_fallback");
                tags.Add("module", GetModulePath(module));
                tags.Add("reason", fallback.ToString());
                s_detailEvents.Add(1, tags);
            }
#endif
        }

        internal static void RecordPropertyBlockSpecialization(
            EvaluationModule module,
            bool cacheHit,
            long startTimestamp,
            int effectCount,
            bool applied)
        {
#if !NETFRAMEWORK
            EvaluationPerformanceMetric metric = cacheHit
                ? EvaluationPerformanceMetric
                    .CompiledPropertyBlockCacheHit
                : EvaluationPerformanceMetric
                    .CompiledPropertyBlockCacheMiss;
            RecordEvent(metric);
            if (startTimestamp != 0)
            {
                long elapsedTicks =
                    Stopwatch.GetTimestamp() - startTimestamp;
                RecordDuration(
                    metric,
                    elapsedTicks);
                if (s_detailDuration.Enabled)
                {
                    RecordDetailDuration(
                        metric,
                        elapsedTicks,
                        "module",
                        GetModulePath(module));
                }
            }

            if (applied && s_events.Enabled)
            {
                AddEvent(
                    s_events,
                    "compiled_property_specialization_application",
                    1);
                AddEvent(
                    s_events,
                    "compiled_property_applied_effect",
                    effectCount);
            }

            if (applied && s_detailEvents.Enabled)
            {
                string modulePath = GetModulePath(module);
                AddEvent(
                    s_detailEvents,
                    "compiled_property_specialization_application",
                    1,
                    "module",
                    modulePath);
                AddEvent(
                    s_detailEvents,
                    "compiled_property_applied_effect",
                    effectCount,
                    "module",
                    modulePath);
            }
#endif
        }

#if !NETFRAMEWORK
        private static string GetMetricName(
            EvaluationPerformanceMetric metric) =>
            s_metricNames[(int)metric];

        private static string GetModulePath(EvaluationModule module)
        {
            string path = module.Source.FullPath;
            return string.IsNullOrEmpty(path)
                ? module.Source.Location?.File ?? "<in-memory>"
                : path;
        }

        private static void AddEvent(
            Counter<long> counter,
            string metric,
            long count)
        {
            if (count == 0 || !counter.Enabled)
            {
                return;
            }

            TagList tags = default;
            tags.Add("metric", metric);
            counter.Add(count, tags);
        }

        private static void AddEvent(
            Counter<long> counter,
            string metric,
            long count,
            string tagName,
            string tagValue)
        {
            if (count == 0 || !counter.Enabled)
            {
                return;
            }

            TagList tags = default;
            tags.Add("metric", metric);
            tags.Add(tagName, tagValue);
            counter.Add(count, tags);
        }
#endif

        private static void RecordDuration(
            EvaluationPerformanceMetric metric,
            long elapsedTicks)
        {
#if !NETFRAMEWORK
            if (s_duration.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", GetMetricName(metric));
                s_duration.Add(ToMilliseconds(elapsedTicks), tags);
            }
#endif
        }

        private static void RecordDetailDuration(
            EvaluationPerformanceMetric metric,
            long elapsedTicks,
            string tagName,
            string tagValue)
        {
#if !NETFRAMEWORK
            if (s_detailDuration.Enabled)
            {
                TagList tags = default;
                tags.Add("metric", GetMetricName(metric));
                tags.Add(tagName, tagValue);
                s_detailDuration.Add(
                    ToMilliseconds(elapsedTicks),
                    tags);
            }
#endif
        }

        internal readonly struct LazyItemOperationShapeScope :
            IDisposable
        {
#if !NETFRAMEWORK
            private readonly EvaluationPerformanceMetric _kind;
            private readonly string _itemType;
            private readonly string _expression;
            private readonly long _startTimestamp;
            private readonly bool _recordCount;
#endif

            internal LazyItemOperationShapeScope(
                EvaluationPerformanceMetric kind,
                string itemType,
                string expression)
            {
#if !NETFRAMEWORK
                _kind = kind;
                _itemType = itemType;
                _expression = expression;
                _startTimestamp = s_lazyItemDuration.Enabled
                    ? Stopwatch.GetTimestamp()
                    : 0;
                _recordCount = s_lazyItemEvents.Enabled;
#endif
            }

            public void Dispose()
            {
#if !NETFRAMEWORK
                if (_startTimestamp == 0 && !_recordCount)
                {
                    return;
                }

                TagList tags = default;
                tags.Add("operation", GetMetricName(_kind));
                tags.Add("item.type", _itemType);
                tags.Add("expression", _expression);
                if (_startTimestamp != 0)
                {
                    s_lazyItemDuration.Add(
                        ToMilliseconds(
                            Stopwatch.GetTimestamp() -
                            _startTimestamp),
                        tags);
                }

                if (_recordCount)
                {
                    s_lazyItemEvents.Add(1, tags);
                }
#endif
            }
        }

#if !NETFRAMEWORK
        private static double ToMilliseconds(long elapsedTicks) =>
            elapsedTicks * 1000.0 / Stopwatch.Frequency;
#endif

        internal readonly struct Scope : IDisposable
        {
            private readonly EvaluationPerformanceMetric _metric;
            private readonly long _startTimestamp;
            private readonly bool _recordCount;

            internal Scope(EvaluationPerformanceMetric metric)
            {
                _metric = metric;
                _startTimestamp =
#if NETFRAMEWORK
                    0;
#else
                    s_duration.Enabled
                        ? Stopwatch.GetTimestamp()
                        : 0;
#endif
                _recordCount =
#if NETFRAMEWORK
                    false;
#else
                    s_events.Enabled;
#endif
            }

            public void Dispose()
            {
                if (_startTimestamp != 0)
                {
                    RecordDuration(
                        _metric,
                        Stopwatch.GetTimestamp() -
                        _startTimestamp);
                }

                if (_recordCount)
                {
                    RecordEvent(_metric);
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
