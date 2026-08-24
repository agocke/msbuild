// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if !NETFRAMEWORK

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Build.BackEnd;
using Shouldly;
using Xunit;

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    public class BuildExecutionInstrumentation_Tests
    {
        [Fact]
        public void ReportsExecutionMetrics()
        {
            _ = BuildExecutionInstrumentation.StartTimestamp();
            var measurements = new List<Measurement>();
            using var listener = CreateListener(
                measurements,
                includeDetails: true);

            using (BuildExecutionInstrumentation.Measure(
                       BuildExecutionMetric.Target,
                       "_GenerateRestoreGraphProjectEntry"))
            {
            }

            using (BuildExecutionInstrumentation.Measure(
                       BuildExecutionMetric.Task,
                       "GetRestoreProjectReferencesTask",
                       "_GenerateRestoreGraphProjectEntry"))
            {
            }

            long dispatchStart =
                BuildExecutionInstrumentation.StartTimestamp();
            BuildExecutionInstrumentation.RecordSince(
                BuildExecutionMetric.ProjectDispatch,
                dispatchStart,
                "/repo/Project.csproj");

            measurements.ShouldContain(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.MeterName &&
                    measurement.InstrumentName ==
                    BuildExecutionInstrumentation
                        .ElapsedInstrumentName &&
                    measurement.Tags["metric"] as string ==
                    nameof(BuildExecutionMetric.Target));
            measurements.ShouldContain(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.MeterName &&
                    measurement.InstrumentName ==
                    BuildExecutionInstrumentation.EventInstrumentName &&
                    measurement.Value == 1 &&
                    measurement.Tags["metric"] as string ==
                    nameof(BuildExecutionMetric.Task));
            measurements.ShouldContain(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.DetailsMeterName &&
                    measurement.Tags["name"] as string ==
                    "_GenerateRestoreGraphProjectEntry");
            measurements.ShouldContain(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.DetailsMeterName &&
                    measurement.Tags["name"] as string ==
                        "GetRestoreProjectReferencesTask" &&
                    measurement.Tags["parent.name"] as string ==
                        "_GenerateRestoreGraphProjectEntry");
            measurements.ShouldContain(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.DetailsMeterName &&
                    measurement.Tags["name"] as string ==
                    "/repo/Project.csproj");
        }

        [Fact]
        public void AggregateMeterDoesNotIncludeNames()
        {
            var measurements = new List<Measurement>();
            using var listener = CreateListener(
                measurements,
                includeDetails: false);

            using (BuildExecutionInstrumentation.Measure(
                       BuildExecutionMetric.Target,
                       "_GenerateRestoreGraphProjectEntry"))
            {
            }

            measurements.ShouldNotBeEmpty();
            measurements.ShouldAllBe(
                measurement =>
                    measurement.MeterName ==
                    BuildExecutionInstrumentation.MeterName &&
                    !measurement.Tags.ContainsKey("name"));
        }

        private static MeterListener CreateListener(
            List<Measurement> measurements,
            bool includeDetails)
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        BuildExecutionInstrumentation.MeterName ||
                    (includeDetails &&
                     instrument.Meter.Name ==
                         BuildExecutionInstrumentation.DetailsMeterName))
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
            return listener;
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
