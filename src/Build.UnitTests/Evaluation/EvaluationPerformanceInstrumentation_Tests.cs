// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !NETFRAMEWORK

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Build.Evaluation;
using Shouldly;
using Xunit;

namespace Microsoft.Build.Engine.UnitTests.Evaluation
{
    public class EvaluationPerformanceInstrumentation_Tests
    {
        [Fact]
        public void ReportsEvaluationMetrics()
        {
            _ = EvaluationPerformanceInstrumentation.Enabled;
            var measurements = new List<Measurement>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name is
                    EvaluationPerformanceInstrumentation.MeterName or
                    EvaluationPerformanceInstrumentation.DetailsMeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) =>
                    measurements.Add(
                        new Measurement(
                            instrument.Meter.Name,
                            instrument.Name,
                            value,
                            CopyTags(tags))));
            listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) =>
                    measurements.Add(
                        new Measurement(
                            instrument.Meter.Name,
                            instrument.Name,
                            value,
                            CopyTags(tags))));
            listener.Start();

            using (EvaluationPerformanceInstrumentation.Measure(
                       EvaluationPerformanceMetric.TotalEvaluation))
            {
            }

            EvaluationPerformanceInstrumentation.RecordEvents(
                EvaluationPerformanceMetric.CompiledPropertyEffect,
                3);
            EvaluationPerformanceInstrumentation.RecordConditionShape(
                "Compiled",
                "'$(Configuration)' == 'Debug'");
            EvaluationPerformanceInstrumentation.RecordConditionContext(
                "ProjectPropertyElement",
                "'$(Configuration)' == 'Debug'");
            using (EvaluationPerformanceInstrumentation
                       .MeasureLazyItemOperationShape(
                           EvaluationPerformanceMetric
                               .LazyItemUpdateApplication,
                           "Compile",
                           "@(Compile)"))
            {
            }

            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .DurationInstrumentName &&
                    measurement.MeterName ==
                    EvaluationPerformanceInstrumentation.MeterName &&
                    measurement.Tags["metric"] as string ==
                    nameof(
                        EvaluationPerformanceMetric
                            .TotalEvaluation));
            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .EventInstrumentName &&
                    measurement.Value == 3 &&
                    measurement.Tags["metric"] as string ==
                    nameof(
                        EvaluationPerformanceMetric
                            .CompiledPropertyEffect));
            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .ConditionShapeInstrumentName &&
                    measurement.MeterName ==
                    EvaluationPerformanceInstrumentation
                        .DetailsMeterName &&
                    measurement.Tags["shape"] as string ==
                    "Compiled");
            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .ConditionContextInstrumentName &&
                    measurement.MeterName ==
                    EvaluationPerformanceInstrumentation
                        .DetailsMeterName &&
                    measurement.Tags["context"] as string ==
                    "ProjectPropertyElement");
            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .LazyItemElapsedInstrumentName &&
                    measurement.MeterName ==
                    EvaluationPerformanceInstrumentation
                        .DetailsMeterName &&
                    measurement.Tags["operation"] as string ==
                    nameof(
                        EvaluationPerformanceMetric
                            .LazyItemUpdateApplication) &&
                    measurement.Tags["item.type"] as string ==
                    "Compile" &&
                    measurement.Tags["expression"] as string ==
                    "@(Compile)");
            measurements.ShouldContain(
                measurement =>
                    measurement.InstrumentName ==
                    EvaluationPerformanceInstrumentation
                        .LazyItemEventInstrumentName &&
                    measurement.MeterName ==
                    EvaluationPerformanceInstrumentation
                        .DetailsMeterName &&
                    measurement.Value == 1 &&
                    measurement.Tags["item.type"] as string ==
                    "Compile");
        }

        [Fact]
        public void AggregateMeterDoesNotEnableDetailedMetrics()
        {
            var measurements = new List<Measurement>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                    EvaluationPerformanceInstrumentation.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) =>
                    measurements.Add(
                        new Measurement(
                            instrument.Meter.Name,
                            instrument.Name,
                            value,
                            CopyTags(tags))));
            listener.Start();

            EvaluationPerformanceInstrumentation
                .RecordCompiledPropertyExpansion("$(Property)");
            EvaluationPerformanceInstrumentation.RecordConditionShape(
                "Compiled",
                "'$(Configuration)' == 'Debug'");
            EvaluationPerformanceInstrumentation.RecordEvent(
                EvaluationPerformanceMetric.CompiledPropertyExpansion);

            measurements.ShouldHaveSingleItem();
            measurements[0].MeterName.ShouldBe(
                EvaluationPerformanceInstrumentation.MeterName);
            measurements[0].Tags.ShouldNotContainKey("expression");
            measurements[0].Tags.ShouldNotContainKey("condition");
        }

        private static Dictionary<string, object?> CopyTags(
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, object?>();
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                result.Add(tag.Key, tag.Value);
            }

            return result;
        }

        private sealed record Measurement(
            string MeterName,
            string InstrumentName,
            double Value,
            Dictionary<string, object?> Tags);
    }
}

#endif
