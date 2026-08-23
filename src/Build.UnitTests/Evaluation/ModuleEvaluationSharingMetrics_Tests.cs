// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Engine.UnitTests.TestComparers;
using Microsoft.Build.Execution;
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
        public void ProjectGraphCoordinatesPropertyReplay()
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
                .Replays.ShouldBe(2);
            ModuleEvaluationOperationMetrics variant = FindOperation(
                metrics,
                projectFile.Path,
                "PropertyAssignment",
                "Variant");
            variant.DistinctVariants.ShouldBe(2);
            variant.Replays.ShouldBe(1);
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
            var projectFile = environment.CreateFile(
                "deferred.proj",
                """
                <Project DefaultTargets="Selected">
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
                    <Compile Remove="@(Removal)"
                             MatchOnMetadata="Category"
                             MatchOnMetadataOptions="CaseInsensitive" />
                    <Compile Update="keep.cs">
                      <Updated>%(Filename)</Updated>
                    </Compile>
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
            module.Items[0].MatchOnMetadataExpressionId.ShouldBe(0);
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
