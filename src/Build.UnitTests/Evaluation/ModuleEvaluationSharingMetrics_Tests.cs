// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Engine.UnitTests.TestComparers;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.Evaluation
{
    public sealed class ModuleEvaluationSharingMetrics_Tests
    {
        [Fact]
        public void ObservedInputsPartitionImportedOperations()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var imported = environment.CreateFile(
                "common.targets",
                """
                <Project>
                  <PropertyGroup>
                    <Invariant>constant</Invariant>
                    <Variant>$(Flavor)</Variant>
                    <Undefined>$(NotDefined)</Undefined>
                  </PropertyGroup>
                  <ItemGroup>
                    <Source Include="$(Flavor)" Marker="$(Flavor)" />
                    <Copy Include="@(Source)" />
                  </ItemGroup>
                </Project>
                """);
            var project = environment.CreateFile(
                "root.proj",
                """
                <Project>
                  <Import Project="common.targets" />
                </Project>
                """);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharingMeasurement();

            for (int i = 0; i < 3; i++)
            {
                Project.FromFile(
                    project.Path,
                    new ProjectOptions
                    {
                        EvaluationContext = context,
                        GlobalProperties = new Dictionary<string, string>
                        {
                            ["Flavor"] = $"flavor-{i}",
                            ["Irrelevant"] = $"noise-{i}",
                        },
                        ProjectCollection = projectCollection,
                    });
            }

            ModuleEvaluationSharingMetrics metrics =
                context.GetModuleEvaluationSharingMetrics();

            ModuleEvaluationOperationMetrics invariant = FindOperation(
                metrics,
                imported.Path,
                "PropertyAssignment",
                "Invariant");
            invariant.Executions.ShouldBe(3);
            invariant.DistinctVariants.ShouldBe(1);

            ModuleEvaluationOperationMetrics variant = FindOperation(
                metrics,
                imported.Path,
                "PropertyAssignment",
                "Variant");
            variant.Executions.ShouldBe(3);
            variant.DistinctVariants.ShouldBe(3);
            variant.Dependencies.ShouldContain("Property:Flavor");
            variant.Dependencies.ShouldNotContain("Property:Irrelevant");

            ModuleEvaluationOperationMetrics undefined = FindOperation(
                metrics,
                imported.Path,
                "PropertyAssignment",
                "Undefined");
            undefined.Executions.ShouldBe(3);
            undefined.DistinctVariants.ShouldBe(1);
            undefined.Dependencies.ShouldContain("Property:NotDefined");

            ModuleEvaluationOperationMetrics copy = FindOperation(
                metrics,
                imported.Path,
                "ItemOperationApplication",
                "Copy");
            copy.Executions.ShouldBe(3);
            copy.DistinctVariants.ShouldBe(3);
            copy.Dependencies.ShouldContain("Item:Source");
        }

        [Fact]
        public void ProjectGraphUsesMeasurementContext()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var project = environment.CreateFile(
                "root.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Measured>value</Measured>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharingMeasurement();

            _ = new ProjectGraph(
                new ProjectGraphOptions
                {
                    EntryPoints = [new ProjectGraphEntryPoint(project.Path)],
                    EvaluationContext = context,
                    ProjectCollection = projectCollection,
                });

            ModuleEvaluationOperationMetrics operation = FindOperation(
                context.GetModuleEvaluationSharingMetrics(),
                project.Path,
                "PropertyAssignment",
                "Measured");
            operation.Executions.ShouldBe(1);
            operation.DistinctVariants.ShouldBe(1);
        }

        [Fact]
        public void PurePropertyAssignmentsReplayMatchingVariants()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "replay.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Invariant>constant</Invariant>
                    <Variant>$(Flavor)</Variant>
                    <Undefined>$(NotDefined)</Undefined>
                    <Conditional Condition="'$(Flavor)' == 'A'">selected</Conditional>
                    <Overridden>first</Overridden>
                    <Overridden>$(Flavor)</Overridden>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Flavor)' == 'A'">
                    <Grouped>selected</Grouped>
                  </PropertyGroup>
                  <Choose>
                    <When Condition="'$(Flavor)' == 'A'">
                      <PropertyGroup>
                        <Nested>selected</Nested>
                      </PropertyGroup>
                    </When>
                  </Choose>
                </Project>
                """);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharing();

            Project first = Evaluate(
                projectFile.Path,
                projectCollection,
                context,
                "A",
                "noise-1");
            Project second = Evaluate(
                projectFile.Path,
                projectCollection,
                context,
                "A",
                "noise-2");
            Project third = Evaluate(
                projectFile.Path,
                projectCollection,
                context,
                "B",
                "noise-3");
            EvaluationContext scalarContext = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared,
                ProjectEvaluationMode.Pure);
            ProjectCollection scalarProjectCollection =
                environment.CreateProjectCollection().Collection;
            Project scalarFirst = Evaluate(
                projectFile.Path,
                scalarProjectCollection,
                scalarContext,
                "A",
                "noise-1");
            Project scalarSecond = Evaluate(
                projectFile.Path,
                scalarProjectCollection,
                scalarContext,
                "A",
                "noise-2");
            Project scalarThird = Evaluate(
                projectFile.Path,
                scalarProjectCollection,
                scalarContext,
                "B",
                "noise-3");

            AssertEquivalentProperties(scalarFirst, first);
            AssertEquivalentProperties(scalarSecond, second);
            AssertEquivalentProperties(scalarThird, third);
            first.ConditionedProperties["Flavor"]
                .ShouldBe(scalarFirst.ConditionedProperties["Flavor"]);
            second.ConditionedProperties["Flavor"]
                .ShouldBe(scalarSecond.ConditionedProperties["Flavor"]);
            third.ConditionedProperties["Flavor"]
                .ShouldBe(scalarThird.ConditionedProperties["Flavor"]);
            first.GetPropertyValue("Invariant").ShouldBe("constant");
            second.GetPropertyValue("Invariant").ShouldBe("constant");
            third.GetPropertyValue("Invariant").ShouldBe("constant");
            first.GetPropertyValue("Variant").ShouldBe("A");
            second.GetPropertyValue("Variant").ShouldBe("A");
            third.GetPropertyValue("Variant").ShouldBe("B");
            first.GetPropertyValue("Conditional").ShouldBe("selected");
            second.GetPropertyValue("Conditional").ShouldBe("selected");
            third.GetPropertyValue("Conditional").ShouldBeEmpty();
            first.GetPropertyValue("Grouped").ShouldBe("selected");
            second.GetPropertyValue("Grouped").ShouldBe("selected");
            third.GetPropertyValue("Grouped").ShouldBeEmpty();
            first.GetPropertyValue("Nested").ShouldBe("selected");
            second.GetPropertyValue("Nested").ShouldBe("selected");
            third.GetPropertyValue("Nested").ShouldBeEmpty();
            second.GetProperty("Overridden").Predecessor.EvaluatedValue.ShouldBe("first");
            third.GetProperty("Overridden").Predecessor.EvaluatedValue.ShouldBe("first");

            ModuleEvaluationSharingMetrics metrics =
                context.GetModuleEvaluationSharingMetrics();
            ModuleEvaluationOperationMetrics invariant = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Invariant");
            invariant.Executions.ShouldBe(3);
            invariant.DistinctVariants.ShouldBe(1);
            invariant.Replays.ShouldBe(2);

            ModuleEvaluationOperationMetrics variant = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Variant");
            variant.Executions.ShouldBe(3);
            variant.DistinctVariants.ShouldBe(2);
            variant.Replays.ShouldBe(1);
            variant.Dependencies.ShouldNotContain("Property:Irrelevant");

            ModuleEvaluationOperationMetrics undefined = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Undefined");
            undefined.DistinctVariants.ShouldBe(1);
            undefined.Replays.ShouldBe(2);

            ModuleEvaluationOperationMetrics conditional = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Conditional");
            conditional.Executions.ShouldBe(3);
            conditional.DistinctVariants.ShouldBe(2);
            conditional.Replays.ShouldBe(1);
            conditional.ScalarFallbacks.ShouldBe(0);

            ModuleEvaluationOperationMetrics groupCondition =
                metrics.Operations.Single(
                    operation =>
                        operation.ModulePath == projectFile.Path &&
                        operation.Kind == "PropertyGroupCondition" &&
                        operation.Dependencies.Contains("Property:Flavor"));
            groupCondition.Executions.ShouldBe(3);
            groupCondition.DistinctVariants.ShouldBe(2);
            groupCondition.Replays.ShouldBe(1);
            groupCondition.ScalarFallbacks.ShouldBe(0);
        }

        [Fact]
        public void FileSystemConditionsUseScalarFallback()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "filesystem-condition.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('missing.file')">
                    <Grouped>local</Grouped>
                  </PropertyGroup>
                  <PropertyGroup>
                    <Value Condition="Exists('missing.file')">local</Value>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharing();

            _ = Project.FromFile(
                projectFile.Path,
                CreateOptions(projectCollection, context, null, "first"));
            _ = Project.FromFile(
                projectFile.Path,
                CreateOptions(projectCollection, context, null, "second"));

            ModuleEvaluationOperationMetrics operation = FindOperation(
                context.GetModuleEvaluationSharingMetrics(),
                projectFile.Path,
                "PropertyAssignment",
                "Value");
            operation.Executions.ShouldBe(2);
            operation.Replays.ShouldBe(0);
            operation.ScalarFallbacks.ShouldBe(2);
            ModuleEvaluationOperationMetrics groupCondition =
                context.GetModuleEvaluationSharingMetrics()
                    .Operations.Single(
                        candidate =>
                            candidate.ModulePath == projectFile.Path &&
                            candidate.Kind == "PropertyGroupCondition" &&
                            candidate.ScalarFallbacks == 2);
            groupCondition.Executions.ShouldBe(2);
            groupCondition.Replays.ShouldBe(0);
        }

        [Fact]
        public void GlobalOverrideUsesScalarFallback()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "global.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>local</Value>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharing();

            Project local = Project.FromFile(
                projectFile.Path,
                CreateOptions(projectCollection, context, null, "first"));
            Project global = Project.FromFile(
                projectFile.Path,
                CreateOptions(projectCollection, context, "global", "second"));
            Project replayedLocal = Project.FromFile(
                projectFile.Path,
                CreateOptions(projectCollection, context, null, "third"));

            local.GetPropertyValue("Value").ShouldBe("local");
            global.GetPropertyValue("Value").ShouldBe("global");
            replayedLocal.GetPropertyValue("Value").ShouldBe("local");

            ModuleEvaluationOperationMetrics operation = FindOperation(
                context.GetModuleEvaluationSharingMetrics(),
                projectFile.Path,
                "PropertyAssignment",
                "Value");
            operation.Executions.ShouldBe(3);
            operation.DistinctVariants.ShouldBe(2);
            operation.Replays.ShouldBe(1);
            operation.ScalarFallbacks.ShouldBe(1);
        }

        [Fact]
        public void ProjectGraphPublishesDuplicateSafePropertyVariants()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "cohort.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Invariant>constant</Invariant>
                    <Variant>$(Flavor)</Variant>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationContext context =
                EvaluationContext.CreateForModuleEvaluationSharing();
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            var entryPoints = new[]
            {
                CreateEntryPoint(projectFile.Path, "A", "one"),
                CreateEntryPoint(projectFile.Path, "A", "two"),
                CreateEntryPoint(projectFile.Path, "B", "three"),
            };

            ProjectGraph graph = new(
                new ProjectGraphOptions
                {
                    EntryPoints = entryPoints,
                    EvaluationContext = context,
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    ProjectCollection = projectCollection,
                });

            graph.ProjectNodes.Count.ShouldBe(3);
            ModuleEvaluationSharingMetrics metrics =
                context.GetModuleEvaluationSharingMetrics();
            FindOperation(
                    metrics,
                    projectFile.Path,
                    "PropertyAssignment",
                    "Invariant")
                .DistinctVariants.ShouldBe(1);
            ModuleEvaluationOperationMetrics variant = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Variant");
            variant.DistinctVariants.ShouldBe(2);
            metrics.PropertyReplayCacheVariants.ShouldBe(3);
        }

        [Fact]
        public void OrdinaryCompiledModuleContextReplaysSafeScalarOperations()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEvaluationEnvVarName,
                "1");
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleReplayEnvVarName,
                "1");
            var projectFile = environment.CreateFile(
                "classic-replay.proj",
                """
                <Project>
                  <PropertyGroup Condition="'$(Flavor)' == 'A'">
                    <Invariant>constant</Invariant>
                    <Variant>$(Flavor)</Variant>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationContext context = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;

            Project first = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Flavor"] = "A",
                        ["Irrelevant"] = "first",
                    },
                    ProjectCollection = projectCollection,
                });
            Project second = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Flavor"] = "A",
                        ["Irrelevant"] = "second",
                    },
                    ProjectCollection = projectCollection,
                });

            second.GetPropertyValue("Invariant")
                .ShouldBe(first.GetPropertyValue("Invariant"));
            ModuleEvaluationSharingMetrics metrics =
                context.GetModuleEvaluationSharingMetrics();
            metrics.PropertyReplayCacheHits.ShouldBeGreaterThan(0);
            metrics.PropertyReplayCacheMisses.ShouldBeGreaterThan(0);
            metrics.ConditionReplayCacheHits.ShouldBeGreaterThan(0);
            metrics.ConditionReplayCacheMisses.ShouldBeGreaterThan(0);
        }

        [Fact]
        public void ClassicReplayRejectsAmbientStaticPropertyFunctions()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEvaluationEnvVarName,
                "1");
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleReplayEnvVarName,
                "1");
            var projectFile = environment.CreateFile(
                "ambient.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>$( [System.Guid]::NewGuid() )</Value>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationContext context = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared);
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;

            Project first = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Configuration"] = "first",
                    },
                    ProjectCollection = projectCollection,
                });
            Project second = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Configuration"] = "second",
                    },
                    ProjectCollection = projectCollection,
                });

            second.GetPropertyValue("Value")
                .ShouldNotBe(first.GetPropertyValue("Value"));
            ModuleEvaluationSharingMetrics metrics =
                context.GetModuleEvaluationSharingMetrics();
            metrics.PropertyReplayCacheHits.ShouldBe(0);
            metrics.PropertyReplayCacheMisses.ShouldBe(0);
        }

        [Fact]
        public void ConcurrentPropertyReplayPublicationDeduplicatesVariants()
        {
            const int workerCount = 16;
            var cache = new PropertyAssignmentReplayCache();
            var operation = new EvaluationOperationId(
                "module.proj",
                1,
                1,
                1,
                "PropertyAssignment",
                "Value");
            var reads = new Dictionary<string, string>
            {
                ["Flavor"] = "A",
            };
            using var barrier = new Barrier(workerCount);
            Task<PropertyAssignmentVariant>[] tasks =
                Enumerable.Range(0, workerCount)
                    .Select(_ => Task.Factory.StartNew(
                        () =>
                        {
                            cache.TryFind(
                                    operation,
                                    name => reads.TryGetValue(
                                        name,
                                        out string? value)
                                            ? value
                                            : null,
                                    out PropertyAssignmentVariant _)
                                .ShouldBeFalse();
                            barrier.SignalAndWait();
                            return cache.Publish(
                                operation,
                                reads,
                                assigned: true,
                                evaluatedValueEscaped: "value",
                                ConditionedPropertiesDelta.Empty);
                        },
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default))
                    .ToArray();

            PropertyAssignmentVariant[] variants =
                Task.WhenAll(tasks).GetAwaiter().GetResult();

            variants.ShouldAllBe(
                variant => ReferenceEquals(variant, variants[0]));
            EvaluationReplayCacheMetrics metrics = cache.GetMetrics();
            metrics.Misses.ShouldBe(workerCount);
            metrics.PublishedVariants.ShouldBe(1);
            metrics.PublicationContentions.ShouldBe(workerCount - 1);
        }

        [Fact]
        public void CompiledPropertyEffectsFormContiguousBatchesAndPreserveFinalState()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEvaluationEnvVarName,
                "1");
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEffectBatchingEnvVarName,
                "1");
            var projectFile = environment.CreateFile(
                "effect-batches.proj",
                """
                <Project TreatAsLocalProperty="Local">
                  <PropertyGroup>
                    <First>one</First>
                    <First>two</First>
                    <LiteralPath>folder\file</LiteralPath>
                    <Dynamic>$(Flavor)</Dynamic>
                    <AfterDynamic>constant</AfterDynamic>
                    <Conditional Condition="'$(Flavor)' == 'A'">selected</Conditional>
                    <Empty />
                    <Global>local</Global>
                    <Local>local</Local>
                  </PropertyGroup>
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["Flavor"] = "A",
                ["Global"] = "global",
                ["Local"] = "global",
            };
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared);
            Project optimized = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = optimizedContext,
                    GlobalProperties = globals,
                    ProjectCollection = optimizedCollection,
                });
            ProjectCollection scalarCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext scalarContext = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Isolated,
                ProjectEvaluationMode.Classic,
                fileSystem: null);
            Project scalar = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = scalarContext,
                    GlobalProperties = globals,
                    ProjectCollection = scalarCollection,
                });

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("Global").ShouldBe("global");
            optimized.GetPropertyValue("Local").ShouldBe("local");
            optimized.GetProperty("First").Predecessor.ShouldBeNull();
            optimized.AllEvaluatedProperties
                .Where(property => property.Xml is not null)
                .Select(property => property.Name)
                .ShouldNotContain("First");

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            module.Properties[0].IsDeadStore.ShouldBeTrue();
            module.Properties[1].IsDeadStore.ShouldBeFalse();
            TableRange segments = module.PropertyGroups[0].PropertySegments;
            segments.Count.ShouldBe(1);
            PropertySegmentTemplate[] loweredSegments =
                module.PropertySegments
                    .Skip(segments.Start)
                    .Take(segments.Count)
                    .ToArray();
            loweredSegments.Select(segment => segment.Kind)
                .ShouldBe(
                    new[]
                    {
                        PropertySegmentKind.CompiledEffectBatch,
                    });
            loweredSegments.Select(segment => segment.Properties.Count)
                .ShouldBe(new[] { 9 });
            loweredSegments[0].ExternalPropertyReads.Count.ShouldBe(1);
            module.PropertyInstructions
                .Skip(loweredSegments[0].Instructions.Start)
                .Take(loweredSegments[0].Instructions.Count)
                .Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind
                        .BranchIfPropertyConditionFalse)
                .ShouldBe(1);
        }

        [Fact]
        public void CompiledPropertyEffectsFoldLocalPropertyChains()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEvaluationEnvVarName,
                "1");
            environment.SetEnvironmentVariable(
                Traits.EnableCompiledModuleEffectBatchingEnvVarName,
                "1");
            var projectFile = environment.CreateFile(
                "folded-property-effects.proj",
                """
                <Project>
                  <PropertyGroup>
                    <A>one</A>
                    <B>$(A)-two</B>
                    <C>prefix-$(B)-suffix</C>
                    <GlobalA>local</GlobalA>
                    <GlobalB>$(GlobalA)-two</GlobalB>
                    <External>$(Flavor)</External>
                    <D>after</D>
                    <E>$(D)-end</E>
                  </PropertyGroup>
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["Flavor"] = "external",
                ["GlobalA"] = "global",
            };
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared);
            Project optimized = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = optimizedContext,
                    GlobalProperties = globals,
                    ProjectCollection = optimizedCollection,
                });
            Project scalar = Project.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = EvaluationContext.Create(
                        EvaluationContext.SharingPolicy.Isolated,
                        ProjectEvaluationMode.Classic,
                        fileSystem: null),
                    GlobalProperties = globals,
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("B").ShouldBe("one-two");
            optimized.GetPropertyValue("C")
                .ShouldBe("prefix-one-two-suffix");
            optimized.GetPropertyValue("GlobalA").ShouldBe("global");
            optimized.GetPropertyValue("GlobalB").ShouldBe("global-two");
            optimized.GetPropertyValue("External").ShouldBe("external");
            optimized.GetPropertyValue("E").ShouldBe("after-end");

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            TableRange segments = module.PropertyGroups[0].PropertySegments;
            segments.Count.ShouldBe(1);
            PropertySegmentTemplate[] loweredSegments =
                module.PropertySegments
                    .Skip(segments.Start)
                    .Take(segments.Count)
                    .ToArray();
            loweredSegments.Select(segment => segment.Kind)
                .ShouldBe(
                    new[]
                    {
                        PropertySegmentKind.CompiledEffectBatch,
                    });
            loweredSegments.Select(segment => segment.Properties.Count)
                .ShouldBe(new[] { 8 });
            loweredSegments[0].ExternalPropertyReads.Count.ShouldBe(1);
            module.Properties.Count(
                    property => property.CompiledValueParts.Count > 0)
                .ShouldBe(5);
        }

        [Fact]
        public void CompiledPropertyEffectsRemainCompactUntilLegacyAccess()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "compact-property-effects.proj",
                """
                <Project>
                  <PropertyGroup>
                    <First>one</First>
                    <Second>$(First)-two</Second>
                    <Third>prefix-$(Second)</Third>
                  </PropertyGroup>
                </Project>
                """);
            ProjectInstance instance = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext =
                        EvaluationContext.CreateForCompiledModuleEvaluation(
                            ProjectEvaluationMode.Pure,
                            useCompiledModuleEffectBatches: true),
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });

            instance.CompactPropertyCount.ShouldBe(3);
            instance.GetPropertyValue("Second").ShouldBe("one-two");
            instance.CompactPropertyCount.ShouldBe(3);

            ProjectPropertyInstance second =
                instance.GetProperty("second");
            second.Name.ShouldBe("Second");
            second.EvaluatedValue.ShouldBe("one-two");
            instance.CompactPropertyCount.ShouldBe(2);

            instance.SetProperty("Third", "changed");
            instance.GetPropertyValue("Third").ShouldBe("changed");
            instance.CompactPropertyCount.ShouldBe(1);

            instance.Properties.Select(property => property.Name)
                .ShouldContain("First");
            instance.CompactPropertyCount.ShouldBe(0);
        }

        [Fact]
        public void ConstantCompiledBlockAppliesOneImmutableDelta()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "constant-compact-block.proj",
                """
                <Project>
                  <PropertyGroup>
                    <FIRST>dead</FIRST>
                    <First>one</First>
                    <Second>two</Second>
                    <Third>three</Third>
                  </PropertyGroup>
                </Project>
                """);
            ProjectInstance instance = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext =
                        EvaluationContext.CreateForCompiledModuleEvaluation(
                            ProjectEvaluationMode.Pure,
                            useCompiledModuleEffectBatches: true),
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });

            instance.CompactPropertyDeltaApplications.ShouldBe(1);
            instance.CompactPropertyCount.ShouldBe(3);
            instance.GetPropertyValue("First").ShouldBe("one");
            instance.GetPropertyValue("Second").ShouldBe("two");
            instance.GetPropertyValue("Third").ShouldBe("three");
            instance.GetProperty("first").Name.ShouldBe("First");
        }

        [Fact]
        public void DynamicCompiledBlockReplaysResidualInstructions()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "dynamic-compact-block.proj",
                """
                <Project>
                  <PropertyGroup>
                    <First>$(Flavor)-$(Mode)-$(Flavor)</First>
                    <Second>$(First)-two</Second>
                    <Third>prefix-$(Second)</Third>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection collection =
                environment.CreateProjectCollection().Collection;
            ProjectRootElement root =
                ProjectRootElement.Open(projectFile.Path, collection);
            EvaluationContext context =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);

            ProjectInstance first = Evaluate("A", "debug");
            ProjectInstance repeated = Evaluate("A", "debug");
            ProjectInstance distinctFlavor = Evaluate("B", "debug");
            ProjectInstance distinctMode = Evaluate("A", "release");

            first.GetPropertyValue("Third")
                .ShouldBe("prefix-A-debug-A-two");
            repeated.GetPropertyValue("Third")
                .ShouldBe("prefix-A-debug-A-two");
            distinctFlavor.GetPropertyValue("Third")
                .ShouldBe("prefix-B-debug-B-two");
            distinctMode.GetPropertyValue("Third")
                .ShouldBe("prefix-A-release-A-two");
            first.CompactPropertyDeltaApplications.ShouldBe(0);
            repeated.CompactPropertyDeltaApplications.ShouldBe(0);
            distinctFlavor.CompactPropertyDeltaApplications.ShouldBe(0);
            distinctMode.CompactPropertyDeltaApplications.ShouldBe(0);

            EvaluationModule module =
                context.EvaluationModuleCache.GetModule(root);
            PropertySegmentTemplate segment =
                module.PropertySegments[
                    module.PropertyGroups[0].PropertySegments.Start];
            segment.ExternalPropertyReads.Count.ShouldBe(2);
            segment.Instructions.Count.ShouldBe(12);
            module.PropertyInstructions
                .Skip(segment.Instructions.Start)
                .Take(segment.Instructions.Count)
                .Select(instruction => instruction.Kind)
                .ShouldBe(
                    new[]
                    {
                        PropertyInstructionKind.SetValue,
                        PropertyInstructionKind.AppendExternalProperty,
                        PropertyInstructionKind.AppendLiteral,
                        PropertyInstructionKind.AppendExternalProperty,
                        PropertyInstructionKind.AppendLiteral,
                        PropertyInstructionKind.AppendExternalProperty,
                        PropertyInstructionKind.SetValue,
                        PropertyInstructionKind.AppendLocalProperty,
                        PropertyInstructionKind.AppendLiteral,
                        PropertyInstructionKind.SetValue,
                        PropertyInstructionKind.AppendLiteral,
                        PropertyInstructionKind.AppendLocalProperty,
                    });
            segment.ConstantState.ShouldBeNull();

            ProjectInstance Evaluate(string flavor, string mode) =>
                ProjectInstance.FromProjectRootElement(
                    root,
                    new ProjectOptions
                    {
                        EvaluationContext = context,
                        EvaluationMode = ProjectEvaluationMode.Pure,
                        GlobalProperties =
                            new Dictionary<string, string>
                            {
                                ["Flavor"] = flavor,
                                ["Mode"] = mode,
                            },
                        ProjectCollection = collection,
                    });
        }

        [Fact]
        public void ResidualProgramDistinguishesUndefinedAndEmptyInputs()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "undefined-compact-block.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Result>prefix-$(Input)-suffix</Result>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection collection =
                environment.CreateProjectCollection().Collection;
            ProjectRootElement root =
                ProjectRootElement.Open(projectFile.Path, collection);
            EvaluationContext context =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);

            var emptyGlobals = new Dictionary<string, string>();
            ProjectInstance undefined = Evaluate(emptyGlobals);
            ProjectInstance repeatedUndefined =
                Evaluate(emptyGlobals);
            ProjectInstance definedEmpty = Evaluate(
                new Dictionary<string, string>
                {
                    ["Input"] = string.Empty,
                });

            undefined.GetPropertyValue("Result")
                .ShouldBe("prefix--suffix");
            repeatedUndefined.GetPropertyValue("Result")
                .ShouldBe("prefix--suffix");
            definedEmpty.GetPropertyValue("Result")
                .ShouldBe("prefix--suffix");
            undefined.CompactPropertyDeltaApplications.ShouldBe(0);
            repeatedUndefined.CompactPropertyDeltaApplications.ShouldBe(0);
            definedEmpty.CompactPropertyDeltaApplications.ShouldBe(0);

            EvaluationModule module =
                context.EvaluationModuleCache.GetModule(root);
            PropertySegmentTemplate segment =
                module.PropertySegments[
                    module.PropertyGroups[0].PropertySegments.Start];
            segment.Instructions.Count.ShouldBe(4);
            segment.ConstantState.ShouldBeNull();

            ProjectInstance Evaluate(
                IDictionary<string, string> globalProperties) =>
                ProjectInstance.FromProjectRootElement(
                    root,
                    new ProjectOptions
                    {
                        EvaluationContext = context,
                        EvaluationMode = ProjectEvaluationMode.Pure,
                        GlobalProperties = globalProperties,
                        ProjectCollection = collection,
                    });
        }

        [Fact]
        public void ResidualProgramExecutesSimplePropertyConditions()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "compiled-conditions.proj",
                """
                <Project>
                  <PropertyGroup Condition="'$(Flavor)' == 'A' And '$(Second)' == 'B'">
                    <Grouped>selected</Grouped>
                  </PropertyGroup>
                  <PropertyGroup>
                    <Conditional Condition="'$(Flavor)' != ''">set</Conditional>
                    <Empty Condition="'$(Missing)' == ''">empty</Empty>
                    <Numeric Condition="'01' == '1'">numeric</Numeric>
                    <ConditionalGlobal Condition="'$(SuppressedCondition)' == 'x'">local</ConditionalGlobal>
                    <AndTrue Condition="'$(Flavor)' == 'A' And '$(Second)' == 'B'">and</AndTrue>
                    <AndShortCircuit Condition="'$(Flavor)' == 'missing' And '$(SkippedAnd)' == 'x'">wrong</AndShortCircuit>
                    <OrTrue Condition="'$(Flavor)' == 'A' Or '$(SkippedOr)' == 'x'">or</OrTrue>
                    <OrFalse Condition="'$(Flavor)' == 'missing' Or '$(Second)' == 'missing'">wrong</OrFalse>
                    <Nested Condition="('$(Flavor)' == 'missing' Or '$(Second)' == 'B') And '$(Third)' != ''">nested</Nested>
                    <NestedAndOr Condition="('$(Flavor)' == 'A' And '$(Second)' == 'B') Or '$(SkippedNested)' == 'x'">nested-and-or</NestedAndOr>
                    <PathMatched Condition="'$(PathValue)' == '/tmp/compiled-condition'">path</PathMatched>
                    <After>$(Conditional)-after</After>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Never)' == 'true'">
                    <Unreachable Condition="(">wrong</Unreachable>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                optimizedContext,
                new Dictionary<string, string>
                {
                    ["Flavor"] = "A",
                    ["Second"] = "B",
                    ["Third"] = "C",
                    ["PathValue"] = "/tmp\\compiled-condition",
                    ["ConditionalGlobal"] = "global",
                });
            Project optimizedUndefined = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                optimizedContext,
                new Dictionary<string, string>());

            ProjectCollection scalarCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext scalarContext = EvaluationContext.Create(
                EvaluationContext.SharingPolicy.Shared,
                ProjectEvaluationMode.Pure);
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                scalarCollection,
                scalarContext,
                new Dictionary<string, string>
                {
                    ["Flavor"] = "A",
                    ["Second"] = "B",
                    ["Third"] = "C",
                    ["PathValue"] = "/tmp\\compiled-condition",
                    ["ConditionalGlobal"] = "global",
                });
            Project scalarUndefined = EvaluateWithGlobals(
                projectFile.Path,
                scalarCollection,
                scalarContext,
                new Dictionary<string, string>());

            AssertEquivalentProperties(scalar, optimized);
            AssertEquivalentProperties(
                scalarUndefined,
                optimizedUndefined);
            optimized.GetPropertyValue("Grouped").ShouldBe("selected");
            optimized.GetPropertyValue("Conditional").ShouldBe("set");
            optimized.GetPropertyValue("Empty").ShouldBe("empty");
            optimized.GetPropertyValue("Numeric").ShouldBe("numeric");
            optimized.GetPropertyValue("ConditionalGlobal")
                .ShouldBe("global");
            optimized.GetPropertyValue("AndTrue").ShouldBe("and");
            optimized.GetPropertyValue("AndShortCircuit").ShouldBeEmpty();
            optimized.GetPropertyValue("OrTrue").ShouldBe("or");
            optimized.GetPropertyValue("OrFalse").ShouldBeEmpty();
            optimized.GetPropertyValue("Nested").ShouldBe("nested");
            optimized.GetPropertyValue("NestedAndOr")
                .ShouldBe("nested-and-or");
            optimized.GetPropertyValue("PathMatched").ShouldBe("path");
            optimized.GetPropertyValue("Unreachable").ShouldBeEmpty();
            optimized.GetPropertyValue("After").ShouldBe("set-after");
            optimizedUndefined.GetPropertyValue("Grouped").ShouldBeEmpty();
            optimizedUndefined.GetPropertyValue("Conditional")
                .ShouldBeEmpty();
            optimizedUndefined.GetPropertyValue("After")
                .ShouldBe("-after");
            optimized.ConditionedProperties["Flavor"]
                .ShouldBe(scalar.ConditionedProperties["Flavor"]);
            optimized.ConditionedProperties.ContainsKey("Missing")
                .ShouldBeFalse();
            optimized.ConditionedProperties.ContainsKey(
                    "SuppressedCondition")
                .ShouldBeFalse();
            optimized.ConditionedProperties.ContainsKey("SkippedAnd")
                .ShouldBeFalse();
            optimized.ConditionedProperties.ContainsKey("SkippedOr")
                .ShouldBeFalse();
            optimized.ConditionedProperties.ContainsKey("SkippedNested")
                .ShouldBeFalse();

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            module.PropertyGroups[0].CompiledConditionId
                .ShouldBeGreaterThan(0);
            PropertyGroupTemplate secondGroup =
                module.PropertyGroups[1];
            module.Properties
                .Skip(secondGroup.Properties.Start)
                .Take(secondGroup.Properties.Count)
                .Count(property =>
                    property.CompiledConditionId > 0)
                .ShouldBe(9);
            module.PropertySegments
                .Skip(secondGroup.PropertySegments.Start)
                .Take(secondGroup.PropertySegments.Count)
                .SelectMany(segment =>
                    module.PropertyInstructions
                        .Skip(segment.Instructions.Start)
                        .Take(segment.Instructions.Count))
                .Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind
                        .BranchIfPropertyConditionFalse)
                .ShouldBe(9);
            module.Properties
                .Skip(secondGroup.Properties.Start)
                .Take(secondGroup.Properties.Count)
                .Single(property =>
                    module.GetStringValue(property.NameStringId) ==
                    "Nested")
                .CompiledConditionId
                .ShouldBe(-1);
            module.CompiledConditionInstructions
                .Any(instruction =>
                    instruction.Kind ==
                    CompiledConditionInstructionKind
                        .BranchIfComparisonTrue)
                .ShouldBeTrue();
        }

        [Fact]
        public void CompiledConditionsEvaluateConcatenatedPropertyOperands()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "compiled-condition-operands.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Configuration>Debug</Configuration>
                    <Platform>AnyCPU</Platform>
                    <PathRoot>/tmp\</PathRoot>
                    <EscapedSemicolon>%3B</EscapedSemicolon>
                    <Percent>%</Percent>
                    <ConfigurationPlatform Condition="'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'">matched</ConfigurationPlatform>
                    <Prefixed Condition="'prefix-$(Configuration)-suffix' == 'prefix-Debug-suffix'">matched</Prefixed>
                    <Path Condition="'$(PathRoot)compiled' == '/tmp/compiled'">matched</Path>
                    <Escaped Condition="'prefix$(EscapedSemicolon)suffix' == 'prefix;suffix'">matched</Escaped>
                    <CrossPartEscape Condition="'$(Percent)3B' == ';'">matched</CrossPartEscape>
                    <Undefined Condition="'prefix$(Missing)suffix' == 'prefixsuffix'">matched</Undefined>
                    <FunctionFallback Condition="'$(Configuration.ToUpper())' == 'DEBUG'">matched</FunctionFallback>
                  </PropertyGroup>
                </Project>
                """);
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                optimizedContext,
                new Dictionary<string, string>());

            ProjectCollection scalarCollection =
                environment.CreateProjectCollection().Collection;
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                scalarCollection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                new Dictionary<string, string>());

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("ConfigurationPlatform")
                .ShouldBe("matched");
            optimized.GetPropertyValue("Prefixed").ShouldBe("matched");
            optimized.GetPropertyValue("Path").ShouldBe("matched");
            optimized.GetPropertyValue("Escaped").ShouldBe("matched");
            optimized.GetPropertyValue("CrossPartEscape")
                .ShouldBe("matched");
            optimized.GetPropertyValue("Undefined").ShouldBe("matched");
            optimized.GetPropertyValue("FunctionFallback")
                .ShouldBe("matched");
            optimized.ConditionedProperties["Configuration"]
                .ShouldBe(scalar.ConditionedProperties["Configuration"]);
            optimized.ConditionedProperties["Platform"]
                .ShouldBe(scalar.ConditionedProperties["Platform"]);

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            PropertyGroupTemplate group = module.PropertyGroups[0];
            PropertyTemplate[] properties = module.Properties
                .Skip(group.Properties.Start)
                .Take(group.Properties.Count)
                .ToArray();
            properties
                .Single(property =>
                    module.GetStringValue(property.NameStringId) ==
                    "FunctionFallback")
                .CompiledConditionId
                .ShouldBe(-1);
            properties
                .Where(property =>
                    module.GetStringValue(property.NameStringId) is
                        "ConfigurationPlatform" or
                        "Prefixed" or
                        "Path" or
                        "Escaped" or
                        "CrossPartEscape" or
                        "Undefined")
                .ShouldAllBe(property =>
                    property.CompiledConditionId > 0);
            module.CompiledConditionComparisons
                .SelectMany(comparison =>
                    new[] { comparison.Left, comparison.Right })
                .Count(operand =>
                    operand.Kind ==
                    CompiledConditionOperandKind.ExpandedValue)
                .ShouldBeGreaterThanOrEqualTo(6);
            module.CompiledConditionValueParts
                .Any(part =>
                    part.Kind ==
                    CompiledConditionValuePartKind.Property)
                .ShouldBeTrue();
        }

        [Fact]
        public void CompactPropertiesExecuteConditionsAndHonorGlobals()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "compact-barriers.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Local>one</Local>
                    <Conditional Condition="'$(Local)' == 'one'">selected</Conditional>
                    <AfterBarrier>after</AfterBarrier>
                    <Global>local</Global>
                    <DerivedGlobal>$(Global)-derived</DerivedGlobal>
                  </PropertyGroup>
                </Project>
                """);
            ProjectInstance instance = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext =
                        EvaluationContext.CreateForCompiledModuleEvaluation(
                            ProjectEvaluationMode.Pure,
                            useCompiledModuleEffectBatches: true),
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Global"] = "external",
                    },
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });

            instance.GetPropertyValue("Local").ShouldBe("one");
            instance.GetPropertyValue("Conditional")
                .ShouldBe("selected");
            instance.GetPropertyValue("AfterBarrier")
                .ShouldBe("after");
            instance.GetPropertyValue("Global")
                .ShouldBe("external");
            instance.GetPropertyValue("DerivedGlobal")
                .ShouldBe("external-derived");
            instance.CompactPropertyCount.ShouldBe(4);
            instance.GetProperty("Local").ShouldNotBeNull();
            instance.GetProperty("Global").ShouldNotBeNull();
        }

        [Fact]
        public void ExpandedPropertyValuesStayInResidualProgram()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "expanded-property-values.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Base>abc</Base>
                    <Upper>$(Base.ToUpperInvariant())</Upper>
                    <Fallback>$([MSBuild]::ValueOrDefault('$(Missing)', 'fallback'))</Fallback>
                    <Suppressed>$([System.Int32]::Parse('not-a-number'))</Suppressed>
                    <ConditionalFunction Condition="'$(Flavor)' == 'A'">$(Base.ToUpperInvariant())-conditional</ConditionalFunction>
                    <After>$(Upper)-after</After>
                  </PropertyGroup>
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["Flavor"] = "A",
                ["Suppressed"] = "global",
            };
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                optimizedContext,
                globals);
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Isolated,
                    ProjectEvaluationMode.Pure),
                globals);

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("Upper").ShouldBe("ABC");
            optimized.GetPropertyValue("Fallback")
                .ShouldBe("fallback");
            optimized.GetPropertyValue("Suppressed")
                .ShouldBe("global");
            optimized.GetPropertyValue("ConditionalFunction")
                .ShouldBe("ABC-conditional");
            optimized.GetPropertyValue("After")
                .ShouldBe("ABC-after");

            ProjectInstance compact = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = optimizedContext,
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    GlobalProperties = globals,
                    ProjectCollection = optimizedCollection,
                });
            compact.GetPropertyValue("Upper").ShouldBe("ABC");
            compact.GetPropertyValue("Fallback")
                .ShouldBe("fallback");
            compact.GetPropertyValue("ConditionalFunction")
                .ShouldBe("ABC-conditional");
            compact.GetPropertyValue("After").ShouldBe("ABC-after");
            compact.CompactPropertyCount.ShouldBe(5);

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            TableRange segments =
                module.PropertyGroups[0].PropertySegments;
            segments.Count.ShouldBe(1);
            PropertySegmentTemplate segment =
                module.PropertySegments[segments.Start];
            segment.Kind.ShouldBe(
                PropertySegmentKind.CompiledEffectBatch);
            module.Properties.Count(property =>
                    property.RequiresExpansion)
                .ShouldBe(1);
            module.PropertyInstructions
                .Skip(segment.Instructions.Start)
                .Take(segment.Instructions.Count)
                .Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind.SetExpandedValue)
                .ShouldBe(1);
            module.PropertyInstructions
                .Skip(segment.Instructions.Start)
                .Take(segment.Instructions.Count)
                .Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind.AppendFunction)
                .ShouldBe(3);
            module.CompiledPropertyFunctions
                .Count(function =>
                    function.Kind ==
                    CompiledPropertyFunctionKind.StringToUpperInvariant)
                .ShouldBe(2);
        }

        [Fact]
        public void FrequentPropertyFunctionsCompileIntoResidualProgram()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "compiled-property-functions.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Base>AbC</Base>
                    <Spaced>  value  </Spaced>
                    <EscapedSource>A%3bB</EscapedSource>
                    <Root>$(MSBuildThisFileDirectory)</Root>
                    <ExpectedPath>$(MSBuildThisFileDirectory)child</ExpectedPath>
                    <Lower>$(Base.ToLowerInvariant())</Lower>
                    <Upper>$(Base.ToUpperInvariant())</Upper>
                    <Contains>$(Base.Contains('bC'))</Contains>
                    <StartsWith>$(Base.StartsWith('A'))</StartsWith>
                    <EndsWith>$(Base.EndsWith('C'))</EndsWith>
                    <Equals>$(Base.Equals('AbC'))</Equals>
                    <NumericEquals>$(Numeric.Equals('1.0'))</NumericEquals>
                    <PathEquals>$(ExpectedPath.Equals('$(PathArgument)'))</PathEquals>
                    <Replaced>$(Base.Replace('b', '-'))</Replaced>
                    <Trimmed>$(Spaced.Trim())</Trimmed>
                    <TrimmedChars>+$(_subset.Trim('+'))+</TrimmedChars>
                    <TrimmedStart>$(Version.TrimStart('vV'))</TrimmedStart>
                    <TrimmedEnd>$(Suffix.TrimEnd('!?'))</TrimmedEnd>
                    <LastDash>$(Rid.LastIndexOf('-'))</LastDash>
                    <SubstringToEnd>$(Rid.Substring('6'))</SubstringToEnd>
                    <SubstringRange>$(Rid.Substring(0, $(LastDash)))</SubstringRange>
                    <EscapedLower>$(EscapedSource.ToLowerInvariant())</EscapedLower>
                    <TrailingSlash>$([MSBuild]::EnsureTrailingSlash('$(Root)'))</TrailingSlash>
                    <Defaulted>$([MSBuild]::ValueOrDefault('$(Missing)', 'fallback'))</Defaulted>
                    <EscapedLiteral>$([MSBuild]::Escape('a;b'))</EscapedLiteral>
                    <DirectoryAbove>$([MSBuild]::GetDirectoryNameOfFileAbove('$(Root)', 'compiled-property-functions.proj'))</DirectoryAbove>
                    <RunningFromVisualStudio>$([MSBuild]::IsRunningFromVisualStudio())</RunningFromVisualStudio>
                    <VersionLessThan>$([MSBuild]::VersionLessThan('18.9', '18.10'))</VersionLessThan>
                    <Sum>$([MSBuild]::Add('40', '2'))</Sum>
                    <Difference>$([MSBuild]::Subtract('44', '2'))</Difference>
                    <TargetFrameworkIdentifier>$([MSBuild]::GetTargetFrameworkIdentifier('net8.0-windows10.0.19041'))</TargetFrameworkIdentifier>
                    <TargetFrameworkVersion>$([MSBuild]::GetTargetFrameworkVersion('net8.0-windows10.0.19041'))</TargetFrameworkVersion>
                    <TargetFrameworkVersionParts>$([MSBuild]::GetTargetFrameworkVersion('net8.0-windows10.0.19041', 1))</TargetFrameworkVersionParts>
                    <TargetPlatformIdentifier>$([MSBuild]::GetTargetPlatformIdentifier('net8.0-windows10.0.19041'))</TargetPlatformIdentifier>
                    <TargetPlatformVersion>$([MSBuild]::GetTargetPlatformVersion('net8.0-windows10.0.19041', 2))</TargetPlatformVersion>
                    <ToolsDirectory32>$([MSBuild]::GetToolsDirectory32())</ToolsDirectory32>
                    <ProcessArchitectureLower>$([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant)</ProcessArchitectureLower>
                    <RuntimeIdentifier>$([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)</RuntimeIdentifier>
                    <VersionToString>$([System.Version]::Parse('18.11.2.3').ToString(2))</VersionToString>
                    <VersionBuildSum>$([MSBuild]::Add($([System.Version]::Parse('1.2.3').Build), 39))</VersionBuildSum>
                    <Combined>$([System.IO.Path]::Combine('$(Root)', 'folder', 'file.txt'))</Combined>
                    <FullPath>$([System.IO.Path]::GetFullPath('$(Root)folder/../file.txt'))</FullPath>
                    <DirectoryName>$([System.IO.Path]::GetDirectoryName('$(Combined)'))</DirectoryName>
                    <DirectorySeparator>$([System.IO.Path]::DirectorySeparatorChar)</DirectorySeparator>
                    <NestedPath>$([System.IO.Path]::GetFullPath(`$([System.IO.Path]::Combine(`$(Root)`, `nested`, `..`, `file.txt`))`))</NestedPath>
                    <NormalizedPath>$([MSBuild]::NormalizePath('$(Root)', 'folder', '$(Lower)'))</NormalizedPath>
                    <NormalizedDirectory>$([MSBuild]::NormalizeDirectory('$(Root)', '$(Base.ToLowerInvariant())'))</NormalizedDirectory>
                    <RawParentheses>$([MSBuild]::NormalizePath('$(Root)', segment(one,two)))</RawParentheses>
                  </PropertyGroup>
                </Project>
                """);
            string root = Path.GetDirectoryName(projectFile.Path)!;
            var globals = new Dictionary<string, string>
            {
                ["Numeric"] = "1",
                ["Rid"] = "linux-x64",
                ["Suffix"] = "value?!",
                ["Version"] = "Vv8.0",
                ["_subset"] = "++subset++",
                ["PathArgument"] = Path.Combine(root, "child")
                    .Replace('/', '\\'),
            };
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                optimizedContext,
                globals);
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Isolated,
                    ProjectEvaluationMode.Pure),
                globals);

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("Lower").ShouldBe("abc");
            optimized.GetPropertyValue("Upper").ShouldBe("ABC");
            optimized.GetPropertyValue("Contains").ShouldBe("True");
            optimized.GetPropertyValue("StartsWith").ShouldBe("True");
            optimized.GetPropertyValue("EndsWith").ShouldBe("True");
            optimized.GetPropertyValue("Equals").ShouldBe("True");
            optimized.GetPropertyValue("NumericEquals").ShouldBe("True");
            optimized.GetPropertyValue("PathEquals").ShouldBe("True");
            optimized.GetPropertyValue("Replaced").ShouldBe("A-C");
            optimized.GetPropertyValue("Trimmed").ShouldBe("value");
            optimized.GetPropertyValue("TrimmedChars").ShouldBe("+subset+");
            optimized.GetPropertyValue("TrimmedStart").ShouldBe("8.0");
            optimized.GetPropertyValue("TrimmedEnd").ShouldBe("value");
            optimized.GetPropertyValue("LastDash").ShouldBe("5");
            optimized.GetPropertyValue("SubstringToEnd").ShouldBe("x64");
            optimized.GetPropertyValue("SubstringRange").ShouldBe("linux");
            optimized.GetPropertyValue("EscapedLower").ShouldBe("a;b");
            optimized.GetPropertyValue("TrailingSlash").ShouldBe(
                FileUtilities.EnsureTrailingSlash(root));
            optimized.GetPropertyValue("Defaulted").ShouldBe("fallback");
            optimized.GetPropertyValue("EscapedLiteral").ShouldBe("a;b");
            optimized.GetPropertyValue("DirectoryAbove").ShouldBe(
                FileUtilities.EnsureTrailingSlash(root));
            optimized.GetPropertyValue("VersionLessThan").ShouldBe("True");
            optimized.GetPropertyValue("Sum").ShouldBe("42");
            optimized.GetPropertyValue("Difference").ShouldBe("42");
            optimized.GetPropertyValue("TargetFrameworkIdentifier")
                .ShouldBe(".NETCoreApp");
            optimized.GetPropertyValue("TargetFrameworkVersion")
                .ShouldBe("8.0");
            optimized.GetPropertyValue("TargetFrameworkVersionParts")
                .ShouldBe("8");
            optimized.GetPropertyValue("TargetPlatformIdentifier")
                .ShouldBe("windows");
            optimized.GetPropertyValue("TargetPlatformVersion")
                .ShouldBe("10.0.19041");
            optimized.GetPropertyValue("ToolsDirectory32")
                .ShouldBe(IntrinsicFunctions.GetToolsDirectory32());
            optimized.GetPropertyValue("VersionToString")
                .ShouldBe("18.11");
            optimized.GetPropertyValue("VersionBuildSum").ShouldBe("42");
            optimized.GetPropertyValue("Combined").ShouldBe(
                Path.Combine(root, "folder", "file.txt"));
            optimized.GetPropertyValue("FullPath").ShouldBe(
                Path.Combine(root, "file.txt"));
            optimized.GetPropertyValue("DirectoryName").ShouldBe(
                Path.Combine(root, "folder"));
            optimized.GetPropertyValue("DirectorySeparator").ShouldBe(
                Path.DirectorySeparatorChar.ToString());
            optimized.GetPropertyValue("NestedPath").ShouldBe(
                Path.Combine(root, "file.txt"));
            optimized.GetPropertyValue("NormalizedPath").ShouldBe(
                Path.GetFullPath(Path.Combine(root, "folder", "abc")));
            optimized.GetPropertyValue("NormalizedDirectory").ShouldBe(
                FileUtilities.EnsureTrailingSlash(
                    Path.GetFullPath(Path.Combine(root, "abc"))));
            optimized.GetPropertyValue("RawParentheses").ShouldBe(
                Path.GetFullPath(
                    Path.Combine(root, "segment(one", "two)")));

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            module.Properties.ShouldAllBe(property =>
                !property.RequiresExpansion);
            module.CompiledPropertyFunctions.Length.ShouldBe(46);
            module.CompiledPropertyFunctions
                .Select(function => function.Kind)
                .ShouldContain(
                    CompiledPropertyFunctionKind.NormalizeDirectory);
            module.CompiledPropertyFunctions
                .Select(function => function.Kind)
                .ShouldContain(
                    CompiledPropertyFunctionKind.NormalizePath);
            module.PropertyInstructions.Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind.SetExpandedValue)
                .ShouldBe(0);
            module.PropertyInstructions.Count(instruction =>
                    instruction.Kind ==
                    PropertyInstructionKind.AppendFunction)
                .ShouldBe(43);
        }

        [Fact]
        public void CompiledPropertyEffectsExpandThisFilePropertiesInProgram()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var imported = environment.CreateFile(
                "context.props",
                """
                <Project>
                  <PropertyGroup>
                    <ImportDirectory>$(MSBuildThisFileDirectory)</ImportDirectory>
                  </PropertyGroup>
                </Project>
                """);
            var projectFile = environment.CreateFile(
                "context.proj",
                $"""
                <Project>
                  <Import Project="{imported.Path}" />
                </Project>
                """);
            ProjectCollection collection =
                environment.CreateProjectCollection().Collection;
            EvaluationContext context =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure,
                    useCompiledModuleEffectBatches: true);
            ProjectInstance instance = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    ProjectCollection = collection,
                });

            instance.GetPropertyValue("ImportDirectory").ShouldBe(
                Path.GetDirectoryName(imported.Path) +
                Path.DirectorySeparatorChar);

            EvaluationModule module =
                context.EvaluationModuleCache.GetModule(
                    ProjectRootElement.Open(imported.Path, collection));
            PropertySegmentTemplate segment =
                module.PropertySegments[
                    module.PropertyGroups[0].PropertySegments.Start];
            segment.Kind.ShouldBe(
                PropertySegmentKind.CompiledEffectBatch);
            module.PropertyInstructions[
                    segment.Instructions.Start]
                .Kind
                .ShouldBe(PropertyInstructionKind.SetValue);
            module.PropertyInstructions[
                    segment.Instructions.Start + 1]
                .Kind
                .ShouldBe(
                    PropertyInstructionKind.AppendContextualProperty);
        }

        [Fact]
        public void CompactPropertyEvaluationPreservesSdkPropertyImportResults()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            const string sdkName = "CompactEvaluation.Sdk";
            string sdkRoot = environment.CreateFolder().Path;
            string sdkDirectory = Path.Combine(
                sdkRoot,
                sdkName,
                "Sdk");
            Directory.CreateDirectory(sdkDirectory);
            File.WriteAllText(
                Path.Combine(sdkDirectory, "Sdk.props"),
                """
                <Project>
                  <PropertyGroup>
                    <FromSdkProps>props</FromSdkProps>
                    <DerivedFromSdkProps>$(FromSdkProps)-derived</DerivedFromSdkProps>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(sdkDirectory, "Sdk.targets"),
                """
                <Project>
                  <PropertyGroup Condition="'$(SpikeDerived)' == 'one-two'">
                    <FromSdkTargets>targets</FromSdkTargets>
                  </PropertyGroup>
                </Project>
                """);
            environment.SetEnvironmentVariable(
                "MSBuildSDKsPath",
                sdkRoot);
            var projectFile = environment.CreateFile(
                "sdk-compact.csproj",
                $"""
                <Project Sdk="{sdkName}">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <SpikeBase>one</SpikeBase>
                    <SpikeDerived>$(SpikeBase)-two</SpikeDerived>
                  </PropertyGroup>
                </Project>
                """);
            ProjectInstance optimized = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext =
                        EvaluationContext.CreateForCompiledModuleEvaluation(
                            ProjectEvaluationMode.Classic,
                            useCompiledModuleEffectBatches: true),
                    EvaluationMode = ProjectEvaluationMode.Classic,
                    EvaluationStage = ProjectEvaluationStage.Properties,
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });
            ProjectInstance scalar = ProjectInstance.FromFile(
                projectFile.Path,
                new ProjectOptions
                {
                    EvaluationContext = EvaluationContext.Create(
                        EvaluationContext.SharingPolicy.Isolated,
                        ProjectEvaluationMode.Classic),
                    EvaluationMode = ProjectEvaluationMode.Classic,
                    EvaluationStage = ProjectEvaluationStage.Properties,
                    ProjectCollection =
                        environment.CreateProjectCollection().Collection,
                });

            optimized.ImportPaths.ShouldContain(
                path =>
                    path.Contains(
                        sdkName,
                        StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(
                        "Sdk.targets",
                        StringComparison.OrdinalIgnoreCase));
            optimized.GetPropertyValue("SpikeDerived")
                .ShouldBe("one-two");
            optimized.GetPropertyValue("DerivedFromSdkProps")
                .ShouldBe("props-derived");
            optimized.GetPropertyValue("FromSdkTargets")
                .ShouldBe("targets");
            optimized.CompactPropertyCount.ShouldBeGreaterThan(0);

            optimized.Properties
                .Select(property =>
                    $"{property.Name}={property.EvaluatedValue}")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ShouldBe(
                    scalar.Properties
                        .Select(property =>
                            $"{property.Name}={property.EvaluatedValue}")
                        .OrderBy(
                            value => value,
                            StringComparer.OrdinalIgnoreCase));
        }

        [Fact]
        public void CompactModuleInterpreterPreservesRootHeaderSemanticsAndCachesLowering()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "headers.proj",
                """
                <Project InitialTargets="RootStart;$(InitialSuffix)"
                         DefaultTargets="$(ConfiguredDefault)"
                         TreatAsLocalProperty="Local">
                  <PropertyGroup>
                    <Local>module</Local>
                  </PropertyGroup>
                  <Target Name="Configured" />
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["ConfiguredDefault"] = "Configured",
                ["InitialSuffix"] = "AfterRoot",
                ["Local"] = "global",
                ["Irrelevant"] = "first",
            };
            EvaluationContext moduleContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            ProjectCollection optimizedCollection =
                environment.CreateProjectCollection().Collection;
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                moduleContext,
                globals);
            Project repeated = EvaluateWithGlobals(
                projectFile.Path,
                optimizedCollection,
                moduleContext,
                new Dictionary<string, string>(globals)
                {
                    ["Irrelevant"] = "second",
                });
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                globals);

            AssertEquivalentProperties(scalar, optimized);
            optimized.CreateProjectInstance().InitialTargets
                .ShouldBe(["RootStart", "AfterRoot"]);
            optimized.CreateProjectInstance().DefaultTargets
                .ShouldBe(["Configured"]);
            optimized.GetPropertyValue("MSBuildProjectDefaultTargets")
                .ShouldBe("Configured");
            optimized.GetPropertyValue("Local").ShouldBe("module");
            repeated.GetPropertyValue("Local").ShouldBe("module");

            ModuleEvaluationSharingMetrics metrics =
                moduleContext.GetModuleEvaluationSharingMetrics();
            metrics.ModuleLowerings.ShouldBe(1);
            metrics.ModuleCacheHits.ShouldBeGreaterThan(0);
            metrics.Replays.ShouldBe(0);
        }

        [Fact]
        public void CompactModuleInterpreterPreservesPropertyAndImportOrder()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            _ = environment.CreateFile(
                "imported.props",
                """
                <Project InitialTargets="ImportedStart">
                  <PropertyGroup>
                    <Phase>imported</Phase>
                    <ImportedPhase>$(Phase)</ImportedPhase>
                  </PropertyGroup>
                </Project>
                """);
            var projectFile = environment.CreateFile(
                "root.proj",
                """
                <Project InitialTargets="RootStart">
                  <PropertyGroup>
                    <Phase>before</Phase>
                  </PropertyGroup>
                  <Import Project="imported.props" />
                  <PropertyGroup>
                    <AfterImport>$(Phase)</AfterImport>
                    <Phase>after</Phase>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationContext moduleContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                moduleContext,
                new Dictionary<string, string>());
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                new Dictionary<string, string>());

            AssertEquivalentProperties(scalar, optimized);
            optimized.CreateProjectInstance().InitialTargets
                .ShouldBe(["RootStart", "ImportedStart"]);
            optimized.GetPropertyValue("ImportedPhase").ShouldBe("imported");
            optimized.GetPropertyValue("AfterImport").ShouldBe("imported");
            optimized.GetPropertyValue("Phase").ShouldBe("after");
            optimized.GetProperty("Phase").Predecessor.EvaluatedValue
                .ShouldBe("imported");
            optimized.Imports.Select(import => import.ImportedProject.FullPath)
                .ShouldBe(scalar.Imports.Select(import => import.ImportedProject.FullPath));

            ModuleEvaluationSharingMetrics metrics =
                moduleContext.GetModuleEvaluationSharingMetrics();
            metrics.ModuleLowerings.ShouldBe(2);
            metrics.ModuleCacheMisses.ShouldBe(2);
        }

        [Fact]
        public void CompactModuleInterpreterSelectsFirstNestedChooseArm()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "choose.proj",
                """
                <Project>
                  <Choose>
                    <When Condition="'$(Outer)' == 'yes'">
                      <PropertyGroup>
                        <OuterResult>first</OuterResult>
                      </PropertyGroup>
                      <Choose>
                        <When Condition="'$(Inner)' == 'yes'">
                          <PropertyGroup>
                            <NestedResult>first</NestedResult>
                          </PropertyGroup>
                        </When>
                        <When Condition="'true' == 'true'">
                          <PropertyGroup>
                            <NestedResult>later</NestedResult>
                          </PropertyGroup>
                        </When>
                        <Otherwise>
                          <PropertyGroup>
                            <NestedResult>otherwise</NestedResult>
                          </PropertyGroup>
                        </Otherwise>
                      </Choose>
                    </When>
                    <When Condition="'true' == 'true'">
                      <PropertyGroup>
                        <OuterResult>later</OuterResult>
                      </PropertyGroup>
                    </When>
                  </Choose>
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["Outer"] = "yes",
                ["Inner"] = "yes",
            };
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure),
                globals);
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                globals);

            AssertEquivalentProperties(scalar, optimized);
            optimized.GetPropertyValue("OuterResult").ShouldBe("first");
            optimized.GetPropertyValue("NestedResult").ShouldBe("first");
        }

        [Fact]
        public void CompactModuleInterpreterCollectsDeferredElementsAcrossImports()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            _ = environment.CreateFile(
                "imported.props",
                """
                <Project>
                  <ItemDefinitionGroup>
                    <Compile>
                      <Marker>imported</Marker>
                    </Compile>
                  </ItemDefinitionGroup>
                  <ItemGroup>
                    <Compile Include="imported.cs" />
                  </ItemGroup>
                  <Target Name="ImportedTarget" />
                </Project>
                """);
            var projectFile = environment.CreateFile(
                "root.proj",
                """
                <Project>
                  <Import Project="imported.props" />
                  <ItemGroup>
                    <Compile Include="root.cs" />
                  </ItemGroup>
                  <Target Name="RootTarget" />
                </Project>
                """);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure),
                new Dictionary<string, string>());
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                new Dictionary<string, string>());

            optimized.GetItems("Compile")
                .Select(item => $"{item.EvaluatedInclude}|{item.GetMetadataValue("Marker")}")
                .ShouldBe(
                    scalar.GetItems("Compile")
                        .Select(item => $"{item.EvaluatedInclude}|{item.GetMetadataValue("Marker")}"));
            optimized.Targets.Keys.OrderBy(name => name, StringComparer.Ordinal)
                .ShouldBe(
                    scalar.Targets.Keys.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void CompactModuleInterpreterPreservesDeferredConditionsAndSelectedClosure()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.CreateFile(
                "selected.props",
                """
                <Project>
                  <PropertyGroup>
                    <ImportedSelection>true</ImportedSelection>
                  </PropertyGroup>
                </Project>
                """);
            var projectFile = environment.CreateFile(
                "deferred.proj",
                """
                <Project DefaultTargets="Selected">
                  <ImportGroup Condition="'$(Select)' == 'true'">
                    <Import Project="selected.props"
                            Condition="'$(Include)' == 'true'" />
                  </ImportGroup>
                  <ItemDefinitionGroup Condition="'$(Select)' == 'true'">
                    <Compile>
                      <Marker>default</Marker>
                    </Compile>
                  </ItemDefinitionGroup>
                  <ItemDefinitionGroup Condition="'$(Select)' != 'true'">
                    <Compile>
                      <Marker>wrong</Marker>
                    </Compile>
                  </ItemDefinitionGroup>
                  <Choose>
                    <When Condition="'$(Select)' == 'true'">
                      <ItemGroup Condition="'$(Include)' == 'true'">
                        <Compile Include="selected.cs"
                                 Condition="'$(Include)' == 'true'">
                          <Origin>selected</Origin>
                        </Compile>
                      </ItemGroup>
                    </When>
                    <Otherwise>
                      <ItemGroup>
                        <Compile Include="otherwise.cs" />
                      </ItemGroup>
                    </Otherwise>
                  </Choose>
                  <ItemGroup>
                    <Compile Update="selected.cs">
                      <Updated>true</Updated>
                    </Compile>
                  </ItemGroup>
                  <ItemGroup Condition="False">
                    <Compile Include="literal-false.cs" />
                  </ItemGroup>
                  <UsingTask TaskName="SkippedTask"
                             AssemblyFile="missing.dll"
                             Condition="'false' == 'true'" />
                  <Target Name="Selected" />
                </Project>
                """);
            var globals = new Dictionary<string, string>
            {
                ["Select"] = "true",
                ["Include"] = "true",
            };
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                optimizedContext,
                globals);
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                globals);

            optimized.GetItems("Compile")
                .Select(item =>
                    $"{item.EvaluatedInclude}|{item.GetMetadataValue("Marker")}|{item.GetMetadataValue("Origin")}|{item.GetMetadataValue("Updated")}")
                .ShouldBe(
                    scalar.GetItems("Compile")
                        .Select(item =>
                            $"{item.EvaluatedInclude}|{item.GetMetadataValue("Marker")}|{item.GetMetadataValue("Origin")}|{item.GetMetadataValue("Updated")}"));
            optimized.Targets.Keys.OrderBy(name => name, StringComparer.Ordinal)
                .ShouldBe(
                    scalar.Targets.Keys.OrderBy(name => name, StringComparer.Ordinal));
            optimized.Targets.ShouldContainKey("Selected");
            optimized.GetPropertyValue("ImportedSelection")
                .ShouldBe("true");
            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            module.ImportGroups
                .Any(group => group.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.Imports
                .Any(import => import.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.ChooseArms
                .Any(arm => arm.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.ItemDefinitionGroups
                .Any(group => group.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.UsingTasks
                .Any(usingTask => usingTask.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.ItemGroups
                .Any(group => group.CompiledConditionId > 0)
                .ShouldBeTrue();
            module.ItemGroups
                .Any(group =>
                    group.CompiledConditionId > 0 &&
                    module.GetSource(group.SourceId).Condition == "False")
                .ShouldBeTrue();
            module.Items
                .Any(item => item.CompiledConditionId > 0)
                .ShouldBeTrue();
        }

        [Fact]
        public void CompactModuleInterpreterPreservesDeferredLeafSemantics()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            var projectFile = environment.CreateFile(
                "deferred-leaves.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Excluded>excluded.cs</Excluded>
                    <EnableDefinition>true</EnableDefinition>
                    <EnableMetadata>true</EnableMetadata>
                    <TaskPrefix>Custom</TaskPrefix>
                    <TaskAssembly>missing.dll</TaskAssembly>
                    <HookTarget>Hook</HookTarget>
                    <DeferredItemExpression>@(DynamicSource)</DeferredItemExpression>
                  </PropertyGroup>
                  <ItemDefinitionGroup>
                    <Compile Condition="'$(EnableDefinition)' == 'true'">
                      <Definition Condition="'$(EnableMetadata)' == 'true'">default</Definition>
                    </Compile>
                  </ItemDefinitionGroup>
                  <ItemGroup>
                    <Compile Include="keep.cs;excluded.cs"
                             Exclude="$(Excluded)">
                      <Conditional Condition="'$(EnableMetadata)' == 'true'">yes</Conditional>
                    </Compile>
                    <Compile Include="remove.cs">
                      <Category>drop</Category>
                    </Compile>
                    <Removal Include="anything">
                      <Category>DROP</Category>
                    </Removal>
                    <DynamicSource Include="nested.cs" />
                    <Compile Remove="@(Removal)"
                             MatchOnMetadata="Category"
                             MatchOnMetadataOptions="CaseInsensitive" />
                    <Compile Update="keep.cs">
                      <Updated Condition="'%(Compile.Filename)' == 'keep' and '$(EnableMetadata)' == 'true'">%(Filename)</Updated>
                      <Combined>%(Filename)-$(EnableMetadata)</Combined>
                      <FunctionValue>$([System.IO.Path]::GetFileName('function.txt'))</FunctionValue>
                      <Nested>$(DeferredItemExpression)</Nested>
                      <Skipped Condition="'%(Filename)' != 'keep'">wrong</Skipped>
                    </Compile>
                    <Glob Include="*.generated.cs" />
                    <Dynamic Include="$(Excluded)" />
                    <Fallback Include="$([System.IO.Path]::GetFileName('fallback.cs'))" />
                  </ItemGroup>
                  <UsingTask TaskName="$(TaskPrefix).Generated"
                             AssemblyFile="$(TaskAssembly)"
                             Runtime="NET"
                             Architecture="x64"
                             Override="true" />
                  <Target Name="Before"
                          BeforeTargets="$(HookTarget)" />
                  <Target Name="Hook" />
                  <Target Name="After"
                          AfterTargets="$(HookTarget)" />
                </Project>
                """);
            EvaluationContext optimizedContext =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            Project optimized = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                optimizedContext,
                new Dictionary<string, string>());
            Project scalar = EvaluateWithGlobals(
                projectFile.Path,
                environment.CreateProjectCollection().Collection,
                EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    ProjectEvaluationMode.Pure),
                new Dictionary<string, string>());

            optimized.AllEvaluatedItems
                .Select(item =>
                    $"{item.ItemType}|{item.EvaluatedInclude}|{string.Join(",", item.Metadata.Select(metadata => $"{metadata.Name}={metadata.EvaluatedValue}"))}")
                .ShouldBe(
                    scalar.AllEvaluatedItems.Select(item =>
                        $"{item.ItemType}|{item.EvaluatedInclude}|{string.Join(",", item.Metadata.Select(metadata => $"{metadata.Name}={metadata.EvaluatedValue}"))}"));
            optimized.Targets.Values
                .OrderBy(target => target.Name, StringComparer.Ordinal)
                .Select(target =>
                    $"{target.Name}|{target.BeforeTargets}|{target.AfterTargets}")
                .ShouldBe(
                    scalar.Targets.Values
                        .OrderBy(
                            target => target.Name,
                            StringComparer.Ordinal)
                        .Select(target =>
                            $"{target.Name}|{target.BeforeTargets}|{target.AfterTargets}"));
            ProjectItem updated =
                optimized.GetItems("Compile")
                    .Single(item =>
                        item.EvaluatedInclude == "keep.cs");
            updated.GetMetadataValue("Updated").ShouldBe("keep");
            updated.GetMetadataValue("Nested").ShouldBe("nested.cs");
            updated.GetMetadataValue("Skipped").ShouldBe(string.Empty);
            Assert.Equal(
                optimized.CreateProjectInstance().TaskRegistry,
                scalar.CreateProjectInstance().TaskRegistry,
                new TaskRegistryComparers.TaskRegistryComparer());

            EvaluationModule module =
                optimizedContext.EvaluationModuleCache.GetModule(
                    optimized.Xml);
            module.GetExpressionValue(0).ShouldBe(string.Empty);
            module.Items[0].RemoveExpressionId.ShouldBe(0);
            module.Items[0].UpdateExpressionId.ShouldBe(0);
            module.Metadata
                .Count(metadata =>
                    metadata.CompiledConditionId > 0)
                .ShouldBeGreaterThanOrEqualTo(2);
            module.Metadata
                .Single(metadata =>
                    module.GetStringValue(metadata.NameStringId) ==
                    "Combined")
                .CompiledValueParts.Count
                .ShouldBe(3);
            module.Metadata
                .Single(metadata =>
                    module.GetStringValue(metadata.NameStringId) ==
                    "FunctionValue")
                .CompiledValueParts.Start
                .ShouldBe(-1);
            module.Items[0].MatchOnMetadataExpressionId.ShouldBe(0);
            module.CompiledItemSpecFragments
                .Any(fragment =>
                    fragment.Kind ==
                    CompiledItemSpecFragmentKind.Value)
                .ShouldBeTrue();
            module.CompiledItemSpecFragments
                .Any(fragment =>
                    fragment.Kind ==
                    CompiledItemSpecFragmentKind.Glob)
                .ShouldBeTrue();
            module.CompiledItemSpecFragments
                .Any(fragment =>
                    fragment.Kind ==
                    CompiledItemSpecFragmentKind.ItemExpression)
                .ShouldBeTrue();
            ItemTemplate dynamicItem = module.Items
                .Single(item =>
                    module.GetStringValue(item.ItemTypeStringId) ==
                    "Dynamic");
            dynamicItem.CompiledItemSpecFragments.Start
                .ShouldBe(-1);
            dynamicItem.CompiledItemSpecExpansion.Start
                .ShouldBeGreaterThanOrEqualTo(0);
            module.Items
                .Single(item =>
                    module.GetStringValue(item.ItemTypeStringId) ==
                    "Fallback")
                .CompiledItemSpecExpansion.Start
                .ShouldBe(-1);
        }

        [Fact]
        public void CompactModuleCacheRelowersMutatedRoot()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            ProjectRootElement root = ProjectRootElement.Create(projectCollection);
            ProjectPropertyElement property =
                root.AddPropertyGroup().AddProperty("Value", "before");
            EvaluationContext context =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            var options = new ProjectOptions
            {
                EvaluationContext = context,
                EvaluationMode = ProjectEvaluationMode.Pure,
                ProjectCollection = projectCollection,
            };

            ProjectInstance first =
                ProjectInstance.FromProjectRootElement(root, options);
            EvaluationModule firstModule =
                context.EvaluationModuleCache.GetModule(root);
            property.Value = "after";
            ProjectInstance second =
                ProjectInstance.FromProjectRootElement(root, options);
            EvaluationModule secondModule =
                context.EvaluationModuleCache.GetModule(root);

            first.GetPropertyValue("Value").ShouldBe("before");
            second.GetPropertyValue("Value").ShouldBe("after");
            firstModule.ShouldNotBeSameAs(secondModule);
            firstModule.Handle.ShouldNotBe(secondModule.Handle);
            context.EvaluationModuleCache.GetModule(firstModule.Handle)
                .ShouldBeSameAs(firstModule);
            context.EvaluationModuleCache.GetModule(secondModule.Handle)
                .ShouldBeSameAs(secondModule);
            context.GetModuleEvaluationSharingMetrics()
                .ModuleLowerings.ShouldBe(2);
        }

        [Fact]
        public void CompactModuleCacheAssignsDenseHandleAfterConcurrentPublication()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            ProjectCollection projectCollection =
                environment.CreateProjectCollection().Collection;
            ProjectRootElement root = ProjectRootElement.Create(projectCollection);
            ProjectPropertyGroupElement propertyGroup = root.AddPropertyGroup();
            for (int i = 0; i < 2_000; i++)
            {
                propertyGroup.AddProperty($"Property{i}", i.ToString());
            }

            EvaluationContext context =
                EvaluationContext.CreateForCompiledModuleEvaluation(
                    ProjectEvaluationMode.Pure);
            const int workerCount = 16;
            using var barrier = new Barrier(workerCount + 1);
            var tasks = new Task<EvaluationModule>[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                tasks[i] = Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        return context.EvaluationModuleCache.GetModule(root);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            barrier.SignalAndWait();
            EvaluationModule[] modules =
                Task.WhenAll(tasks).GetAwaiter().GetResult();

            modules.ShouldAllBe(module => ReferenceEquals(module, modules[0]));
            modules[0].Handle.ShouldBe(1);
            context.GetModuleEvaluationSharingMetrics()
                .ModuleLowerings.ShouldBe(1);
        }

        private static ProjectGraphEntryPoint CreateEntryPoint(
            string projectPath,
            string flavor,
            string irrelevant)
        {
            return new ProjectGraphEntryPoint(
                projectPath,
                new Dictionary<string, string>
                {
                    ["Flavor"] = flavor,
                    ["Irrelevant"] = irrelevant,
                });
        }

        private static Project Evaluate(
            string projectPath,
            ProjectCollection projectCollection,
            EvaluationContext context,
            string flavor,
            string irrelevant)
        {
            return Project.FromFile(
                projectPath,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    GlobalProperties = new Dictionary<string, string>
                    {
                        ["Flavor"] = flavor,
                        ["Irrelevant"] = irrelevant,
                    },
                    ProjectCollection = projectCollection,
                });
        }

        private static Project EvaluateWithGlobals(
            string projectPath,
            ProjectCollection projectCollection,
            EvaluationContext context,
            IDictionary<string, string> globalProperties)
        {
            return Project.FromFile(
                projectPath,
                new ProjectOptions
                {
                    EvaluationContext = context,
                    EvaluationMode = ProjectEvaluationMode.Pure,
                    GlobalProperties = globalProperties,
                    ProjectCollection = projectCollection,
                });
        }

        private static ProjectOptions CreateOptions(
            ProjectCollection projectCollection,
            EvaluationContext context,
            string? value,
            string irrelevant)
        {
            var globalProperties = new Dictionary<string, string>
            {
                ["Irrelevant"] = irrelevant,
            };
            if (value is not null)
            {
                globalProperties["Value"] = value;
            }

            return new ProjectOptions
            {
                EvaluationContext = context,
                EvaluationMode = ProjectEvaluationMode.Pure,
                GlobalProperties = globalProperties,
                ProjectCollection = projectCollection,
            };
        }

        private static ModuleEvaluationOperationMetrics FindOperation(
            ModuleEvaluationSharingMetrics metrics,
            string modulePath,
            string kind,
            string name)
        {
            return metrics.Operations.Single(
                operation =>
                    operation.ModulePath == modulePath &&
                    operation.Kind == kind &&
                    operation.Name == name);
        }

        private static void AssertEquivalentProperties(
            Project expected,
            Project actual)
        {
            string[] expectedProperties = expected.Properties
                    .Select(property => $"{property.Name}={property.EvaluatedValue}")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            string[] actualProperties = actual.Properties
                    .Select(property => $"{property.Name}={property.EvaluatedValue}")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            actualProperties.ShouldBe(expectedProperties);
        }
    }
}
