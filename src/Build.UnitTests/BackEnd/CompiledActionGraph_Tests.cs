// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if FEATURE_ASSEMBLYLOADCONTEXT
using System.Reflection;
using System.Runtime.Loader;
#endif
using Microsoft.Build.BackEnd;
using Microsoft.Build.Construction;
using Microsoft.Build.Engine.UnitTests.BackEnd;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests.Shared;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    public class CompiledActionGraph_Tests
    {
        [Fact]
        public void TargetPlanPartialEvaluationIsSharedWithinProjectAndRebuiltAfterTranslation()
        {
            using ProjectFromString projectFromString = new("""
                <Project>
                  <Target Name="Build">
                    <CompiledActionGraphTestTask Text="first" Number="1" Values="a;b" />
                  </Target>
                </Project>
                """);

            ProjectInstance original = projectFromString.Project.CreateProjectInstance();
            ProjectTargetInstance originalTarget = original.Targets["Build"];
            CompiledTargetPlan originalPlan = CompiledTargetPlan.PartiallyEvaluate(original, originalTarget);

            Assert.Same(originalPlan, CompiledTargetPlan.PartiallyEvaluate(original, originalTarget));
            Assert.Same(originalPlan.GetAction(0), CompiledTargetPlan.PartiallyEvaluate(original, originalTarget).GetAction(0));

            original.TranslateEntireState = true;
            ((ITranslatable)original).Translate(TranslationHelpers.GetWriteTranslator());
            ProjectInstance translated = ProjectInstance.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            CompiledTargetPlan translatedPlan = CompiledTargetPlan.PartiallyEvaluate(translated, translated.Targets["Build"]);

            Assert.NotSame(originalPlan, translatedPlan);
            Assert.NotSame(originalPlan.GetAction(0).Template, translatedPlan.GetAction(0).Template);
        }

        [Fact]
        public void CompiledActionPreservesOrdinaryTaskBehaviorAndReusesBoundSite()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="$(Text)" Number="42" Values="a;b" />
                <CompiledActionGraphTestTask Text="second" Number="84" Values="c;d" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTargetPlan plan = CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]);
            CompiledTaskAction firstAction = plan.GetAction(0);
            CompiledTaskAction secondAction = plan.GetAction(1);
            MockLogger logger = Build(instance);

            logger.AssertLogContains("compiled-action:first:42:a,b");
            logger.AssertLogContains("compiled-action:second:84:c,d");
            Assert.NotNull(firstAction.GetBoundAction());
            Assert.NotNull(secondAction.GetBoundAction());
            Assert.Same(
                firstAction.GetBoundAction().TaskFactoryWrapper,
                secondAction.GetBoundAction().TaskFactoryWrapper);
        }

        [Fact]
        public void CompiledActionPreservesCaseInsensitiveParameterBinding()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask text="value" number="42" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(0);
            MockLogger logger = Build(instance);

            logger.AssertLogContains("compiled-action:value:42:");
            Assert.NotNull(action.GetBoundAction());
        }

        [Fact]
        public void CompiledActionPreservesRequiredParameterFailureAcrossBatches()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <ItemGroup>
                  <Input Include="first">
                    <Text>required</Text>
                  </Input>
                  <Input Include="second" />
                </ItemGroup>
                <CompiledActionGraphTestTask Text="%(Input.Text)" Number="1" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action = CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);
            MockLogger logger = Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            logger.AssertLogContains("MSB4044");
            Assert.NotNull(action.GetBoundAction());
        }

        [Fact]
        public void TaskSiteWithDeclaredOutputFallsBackToGenericExecutor()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="output" Number="1">
                  <Output TaskParameter="Result" PropertyName="Result" />
                </CompiledActionGraphTestTask>
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action = CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(0);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.Equal("output", instance.GetPropertyValue("Result"));
            Assert.Null(action.GetBoundAction());
        }

        [Fact]
        public void TaskSiteWithUnknownParameterFallsBackBeforeCompiledInvocation()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="valid" Unknown="value" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action = CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(0);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            Assert.Null(action.GetBoundAction());
        }

        [Fact]
        public void CompiledActionPreservesThrowingSetterFailure()
        {
            using TestEnvironment environment = TestEnvironment.Create(ignoreBuildErrorFiles: true);
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <ItemGroup>
                  <Input Include="first" />
                  <Input Include="second">
                    <Value>throw</Value>
                  </Input>
                </ItemGroup>
                <CompiledActionGraphTestTask Text="valid" Throwing="%(Input.Value)" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action = CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);

            Build(instance, out BuildResult result, allowTaskCrashes: true);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            Assert.NotNull(action.GetBoundAction());
        }

#if FEATURE_ASSEMBLYLOADCONTEXT
        [DotNetOnlyFact]
        public void MemoizedTaskActionMetadataDoesNotRootCollectibleLoadContext()
        {
            WeakReference baselineLoadContext = CreateMetadataFromCollectibleLoadContext(memoizeMetadata: false);
            CollectUntilUnloaded(baselineLoadContext);
            Assert.False(baselineLoadContext.IsAlive);

            WeakReference loadContext = CreateMetadataFromCollectibleLoadContext(memoizeMetadata: true);
            CollectUntilUnloaded(loadContext);
            Assert.False(loadContext.IsAlive);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateMetadataFromCollectibleLoadContext(bool memoizeMetadata)
        {
            var loadContext = new TaskTestLoadContext();
            Assembly assembly = loadContext.LoadFromAssemblyPath(typeof(CompiledActionGraphTestTask).Assembly.Location);
            Type taskType = assembly.GetType(typeof(CompiledActionGraphTestTask).FullName, throwOnError: true);
            var loadedType = new LoadedType(
                taskType,
                AssemblyLoadInfo.Create(assemblyName: null, assembly.Location),
                assembly,
                typeof(ITaskItem));

            if (memoizeMetadata)
            {
                _ = TaskActionTypeMetadata.GetOrCreate(loadedType);
            }

            var weakReference = new WeakReference(loadContext);
            loadContext.Unload();
            loadedType = null;
            taskType = null;
            assembly = null;
            loadContext = null;
            return weakReference;
        }

        private static void CollectUntilUnloaded(WeakReference loadContext)
        {
            for (int i = 0; loadContext.IsAlive && i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private sealed class TaskTestLoadContext : AssemblyLoadContext
        {
            internal TaskTestLoadContext()
                : base(isCollectible: true)
            {
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (string.Equals(assemblyName.Name, typeof(Microsoft.Build.Utilities.Task).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(Microsoft.Build.Utilities.Task).Assembly;
                }

                if (string.Equals(assemblyName.Name, typeof(ITask).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(ITask).Assembly;
                }

                return null;
            }
        }
#endif

        private static string CreateProject(string targetContents) =>
            $"""
            <Project>
              <UsingTask TaskName="{typeof(CompiledActionGraphTestTask).FullName}" AssemblyFile="{typeof(CompiledActionGraphTestTask).Assembly.Location}" />
              <PropertyGroup>
                <Text>first</Text>
              </PropertyGroup>
              <Target Name="Build">
                {targetContents}
              </Target>
            </Project>
            """;

        private static MockLogger Build(ProjectInstance instance)
        {
            return Build(instance, out _);
        }

        private static MockLogger Build(ProjectInstance instance, out BuildResult result, bool allowTaskCrashes = false)
        {
            var logger = new MockLogger
            {
                AllowTaskCrashes = allowTaskCrashes,
            };
            using var manager = new BuildManager();
            manager.BeginBuild(new BuildParameters
            {
                EnableNodeReuse = false,
                Loggers = new ILogger[] { logger },
            });

            try
            {
                result = manager.BuildRequest(new BuildRequestData(instance, new[] { "Build" }));
            }
            finally
            {
                manager.EndBuild();
            }

            return logger;
        }
    }

    public sealed class CompiledActionGraphTestTask : Microsoft.Build.Utilities.Task
    {
        [Required]
        public string Text { get; set; }

        public int Number { get; set; }

        public string[] Values { get; set; }

        [Output]
        public string Result => Text;

        public string Throwing
        {
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    throw new InvalidOperationException(value);
                }
            }
        }

        public override bool Execute()
        {
            Log.LogMessage(MessageImportance.High, "compiled-action:{0}:{1}:{2}", Text, Number, string.Join(",", Values ?? Array.Empty<string>()));
            return true;
        }
    }
}
