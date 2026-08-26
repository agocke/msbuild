// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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
    [CollectionDefinition(nameof(CompiledActionGraph_Tests), DisableParallelization = true)]
    public sealed class CompiledActionGraphTestCollection
    {
    }

    [Collection(nameof(CompiledActionGraph_Tests))]
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

            ProjectInstance peer =
                projectFromString.Project.CreateProjectInstance();
            CompiledTargetPlan peerPlan =
                CompiledTargetPlan.PartiallyEvaluate(
                    peer,
                    peer.Targets["Build"]);
            Assert.Same(
                originalPlan.GetAction(0).Program,
                peerPlan.GetAction(0).Program);

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
                <CompiledActionGraphTestTask Text="$(Text)" Number="42" Values="@(FirstValues)" />
                <CompiledActionGraphTestTask Text="second" Number="84" Values="@(SecondValues)" />
                """,
                """
                <ItemGroup>
                  <FirstValues Include="a;b" />
                  <SecondValues Include="c;d" />
                </ItemGroup>
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
            Assert.NotNull(secondAction.GetFastAction());
        }

        [Fact]
        public void FastActionPreservesConstructorFailure()
        {
            using TestEnvironment environment = TestEnvironment.Create(ignoreBuildErrorFiles: true);
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" Behavior="ArmConstructorFailure" />
                <CompiledActionGraphTestTask Text="direct" ContinueOnError="WarnAndContinue" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);

            MockLogger logger = Build(instance, out BuildResult result, allowTaskCrashes: true);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            logger.AssertLogContains("fast-action-constructor-failure");
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void FastActionReadsItemArrayInputWithoutGenericExpansion()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                CompiledTargetPlan.EnablePartialEvaluationEnvVarName,
                "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="$(Text)" Items="@(Input)" />
                """,
                """
                <ItemGroup>
                  <Input Include="a%3bb">
                    <Source>value%3bwith%3bsemicolons</Source>
                  </Input>
                </ItemGroup>
                """));
            ProjectInstance instance =
                projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(1);

            MockLogger logger = Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            logger.AssertLogContains(
                "compiled-item:a;b:value;with;semicolons");
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void SemicolonVectorInputFallsBackToGenericExpansion()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                CompiledTargetPlan.EnablePartialEvaluationEnvVarName,
                "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Values="a;b" />
                """));
            ProjectInstance instance =
                projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(1);

            MockLogger logger = Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            logger.AssertLogContains("compiled-action:direct:0:a,b");
            Assert.Null(action.GetFastAction());
        }

        [Fact]
        public void FastActionPreservesRequiredParameterFailure()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(
                CompiledTargetPlan.EnablePartialEvaluationEnvVarName,
                "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Number="1" />
                """));
            ProjectInstance instance =
                projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(1);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void FastActionPreservesBodyExceptionAndWarnAndContinue()
        {
            using TestEnvironment environment = TestEnvironment.Create(ignoreBuildErrorFiles: true);
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="Throw" ContinueOnError="WarnAndContinue" />
                <CompiledActionGraphTestTask Text="after" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);

            MockLogger logger = Build(instance, out BuildResult result, allowTaskCrashes: true);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            logger.AssertLogContains("fast-action-body-failure");
            logger.AssertLogContains("compiled-action:after:0:");
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void FastActionEvaluatesDynamicConditionAndContinueOnError()
        {
            using TestEnvironment environment = TestEnvironment.Create(ignoreBuildErrorFiles: true);
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <PropertyGroup>
                  <RunDirect>true</RunDirect>
                  <Continue>WarnAndContinue</Continue>
                </PropertyGroup>
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="Throw"
                                             Condition="'$(RunDirect)' == 'true'"
                                             ContinueOnError="$(Continue)" />
                <CompiledActionGraphTestTask Text="skipped"
                                             Condition="'$(RunSkipped)' == 'true'" />
                <CompiledActionGraphTestTask Text="after" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTargetPlan plan =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"]);

            MockLogger logger = Build(
                instance,
                out BuildResult result,
                allowTaskCrashes: true);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            logger.AssertLogContains("fast-action-body-failure");
            logger.AssertLogDoesntContain("compiled-action:skipped:");
            logger.AssertLogContains("compiled-action:after:0:");
            Assert.NotNull(plan.GetAction(2).GetFastAction());
            Assert.NotNull(plan.GetAction(3).GetFastAction());
        }

        [Fact]
        public void FastActionUsesPostBodyBuildEngineForFalseWithoutErrorPolicy()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="ClearBuildEngineAndReturnFalse" ContinueOnError="ErrorAndContinue" />
                <CompiledActionGraphTestTask Text="after" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);

            MockLogger logger = Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Failure, result.OverallResult);
            logger.AssertLogDoesntContain("MSB4181");
            logger.AssertLogContains("compiled-action:after:0:");
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void FastActionDoesNotMaterializeTaskHostWhenTaskDoesNotUseBuildEngine()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="DoNotUseBuildEngine" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(instance, instance.Targets["Build"]).GetAction(1);

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.False(observedContext.TaskHostMaterialized);
            Assert.True(observedContext.TaskEnvironmentInitialized);
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void FastActionInitializesTaskEnvironmentForLoggingOnlyInTraditionalMode()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskHostMaterialized);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionMaterializesTaskHostWhenEngineServicesAreRequested()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="UseEngineServices" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskHostMaterialized);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionInitializesTaskEnvironmentForInterfaceInjection()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            Type taskType = typeof(CompiledActionGraphEnvironmentInterfaceTask);
            using ProjectFromString projectFromString = new(CreateProjectForTask(
                taskType,
                $"""
                <{taskType.FullName} />
                <{taskType.FullName} />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionInitializesTaskEnvironmentForConstructorInjection()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            Type taskType = typeof(CompiledActionGraphEnvironmentConstructorTask);
            using ProjectFromString projectFromString = new(CreateProjectForTask(
                taskType,
                $"""
                <{taskType.FullName} />
                <{taskType.FullName} />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionInitializesTaskEnvironmentForPathConversion()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" Behavior="DoNotUseBuildEngine" />
                <CompiledActionGraphTestTask Text="direct" Path="relative.txt" Behavior="DoNotUseBuildEngine" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionPreservesEagerTaskEnvironmentForUnmarkedTask()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            Type taskType = typeof(CompiledActionGraphLegacyEnvironmentTask);
            using ProjectFromString projectFromString = new(CreateProjectForTask(
                taskType,
                $"""
                <{taskType.FullName} />
                <{taskType.FullName} />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.NotNull(observedContext);
            Assert.True(observedContext.TaskEnvironmentInitialized);
        }

        [Fact]
        public void FastActionResidualContextPreservesContinueOnErrorWithoutTaskHost()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="ReadContinueOnError" ContinueOnError="WarnAndContinue" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();

            ResidualTaskExecutionContext observedContext = null;
            bool observedContinueOnError = false;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context =>
            {
                observedContext = context;
                observedContinueOnError = context.ContinueOnError;
            };
            BuildResult result;
            try
            {
                Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.True(observedContinueOnError);
            Assert.NotNull(observedContext);
            Assert.False(observedContext.TaskHostMaterialized);
        }

        [Fact]
        public void FastActionResidualContextDoesNotMaterializeAfterTaskReturns()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");
            CompiledActionGraphTestTask.ResetState();

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="direct" Behavior="CaptureBuildEngine" />
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            ResidualTaskExecutionContext observedContext = null;
            ResidualTaskExecutionContext.TestOnlyHookOnCreate = context => observedContext = context;
            MockLogger logger;
            BuildResult result;
            try
            {
                logger = Build(instance, out result);
            }
            finally
            {
                ResidualTaskExecutionContext.TestOnlyHookOnCreate = null;
            }

            Assert.NotNull(observedContext);
            observedContext.LogMessageEvent(
                new BuildMessageEventArgs(
                    "late-residual-message",
                    helpKeyword: null,
                    senderName: nameof(CompiledActionGraphTestTask),
                    MessageImportance.High));

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            logger.AssertLogDoesntContain("late-residual-message");
            Assert.False(observedContext.TaskHostMaterialized);
        }

        [Fact]
        public void FastActionCancellationStateCancelsCurrentTask()
        {
            CompiledActionGraphTestTask.ResetState();
            using var cancellationSource = new CancellationTokenSource();
            using var cancellationState = new FastTaskCancellationState(cancellationSource.Token);
            var task = new CompiledActionGraphTestTask();

            cancellationSource.Cancel();
            cancellationState.SetCurrentTask(task, taskLoggingContext: null, template: null);

            Assert.True(CompiledActionGraphTestTask.CancellationObserved);
            Assert.True(cancellationState.IsCancellationRequested);
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
        public void FastActionPublishesItemArrayOutputWithMetadata()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="warmup" />
                <CompiledActionGraphTestTask Text="a%3bb">
                  <Output TaskParameter="OutputItems" ItemName="%43aptured" />
                </CompiledActionGraphTestTask>
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(1);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            ProjectItemInstance output = Assert.Single(
                instance.GetItems("Captured"));
            Assert.Equal("a;b", output.EvaluatedInclude);
            Assert.Equal("value;with;semicolons", output.GetMetadataValue("Source"));
            Assert.NotNull(action.GetFastAction());
        }

        [Fact]
        public void ValueTypeItemArrayOutputFallsBackToGenericExecutor()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <CompiledActionGraphTestTask Text="output">
                  <Output TaskParameter="ValueTypeOutputItems" ItemName="Numbers" />
                </CompiledActionGraphTestTask>
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(0);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.Equal(
                "42",
                Assert.Single(instance.GetItems("Numbers")).EvaluatedInclude);
            Assert.Null(action.GetFastAction());
        }

        [Fact]
        public void PropertyOutputFallsBackToGenericExecutor()
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
            Assert.Null(action.GetFastAction());
        }

        [Fact]
        public void ConditionalOutputFallsBackToGenericExecutor()
        {
            using TestEnvironment environment = TestEnvironment.Create();
            environment.SetEnvironmentVariable(CompiledTargetPlan.EnablePartialEvaluationEnvVarName, "1");

            using ProjectFromString projectFromString = new(CreateProject(
                """
                <PropertyGroup>
                  <Capture>true</Capture>
                </PropertyGroup>
                <CompiledActionGraphTestTask Text="output">
                  <Output TaskParameter="OutputItems"
                          ItemName="Captured"
                          Condition="'$(Capture)' == 'true'" />
                </CompiledActionGraphTestTask>
                """));
            ProjectInstance instance = projectFromString.Project.CreateProjectInstance();
            CompiledTaskAction action =
                CompiledTargetPlan.PartiallyEvaluate(
                    instance,
                    instance.Targets["Build"])
                    .GetAction(1);

            Build(instance, out BuildResult result);

            Assert.Equal(BuildResultCode.Success, result.OverallResult);
            Assert.Single(instance.GetItems("Captured"));
            Assert.Null(action.GetFastAction());
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

        private static string CreateProject(
            string targetContents,
            string projectContents = "") =>
            $"""
            <Project>
              <UsingTask TaskName="{typeof(CompiledActionGraphTestTask).FullName}" AssemblyFile="{typeof(CompiledActionGraphTestTask).Assembly.Location}" />
              <PropertyGroup>
                <Text>first</Text>
              </PropertyGroup>
              {projectContents}
              <Target Name="Build">
                {targetContents}
              </Target>
            </Project>
            """;

        private static string CreateProjectForTask(
            Type taskType,
            string targetContents) =>
            $"""
            <Project>
              <UsingTask TaskName="{taskType.FullName}" AssemblyFile="{taskType.Assembly.Location}" />
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

    [MSBuildMultiThreadableTask]
    public sealed class CompiledActionGraphTestTask : Microsoft.Build.Utilities.Task, ICancelableTask
    {
        private static int s_failNextConstructor;
        private static int s_cancellationObserved;

        public CompiledActionGraphTestTask()
        {
            if (Interlocked.Exchange(ref s_failNextConstructor, 0) != 0)
            {
                throw new InvalidOperationException("fast-action-constructor-failure");
            }
        }

        internal static bool CancellationObserved => Volatile.Read(ref s_cancellationObserved) != 0;

        [Required]
        public string Text { get; set; }

        public string Behavior { get; set; }

        public int Number { get; set; }

        public string[] Values { get; set; }

        public ITaskItem[] Items { get; set; }

        public AbsolutePath Path { get; set; }

        [Output]
        public string Result => Text;

        [Output]
        public ITaskItem[] OutputItems
        {
            get
            {
                var item = new Microsoft.Build.Utilities.TaskItem(Text);
                item.SetMetadata("Source", "value;with;semicolons");
                return new ITaskItem[] { item };
            }
        }

        [Output]
        public TaskItem<int>[] ValueTypeOutputItems =>
            new TaskItem<int>[] { new(42) };

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
            if (string.Equals(Behavior, "ArmConstructorFailure", StringComparison.Ordinal))
            {
                Volatile.Write(ref s_failNextConstructor, 1);
            }
            else if (string.Equals(Behavior, "Throw", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("fast-action-body-failure");
            }
            else if (string.Equals(Behavior, "ClearBuildEngineAndReturnFalse", StringComparison.Ordinal))
            {
                BuildEngine = null;
                return false;
            }
            else if (string.Equals(Behavior, "DoNotUseBuildEngine", StringComparison.Ordinal))
            {
                return true;
            }
            else if (string.Equals(Behavior, "UseEngineServices", StringComparison.Ordinal))
            {
                if (BuildEngine is IBuildEngine10 buildEngine10)
                {
                    _ = buildEngine10.EngineServices;
                }

                return true;
            }
            else if (string.Equals(Behavior, "ReadContinueOnError", StringComparison.Ordinal))
            {
                return BuildEngine.ContinueOnError;
            }
            else if (string.Equals(Behavior, "CaptureBuildEngine", StringComparison.Ordinal))
            {
                return true;
            }

            Log.LogMessage(MessageImportance.High, "compiled-action:{0}:{1}:{2}", Text, Number, string.Join(",", Values ?? Array.Empty<string>()));
            if (Items != null)
            {
                for (int i = 0; i < Items.Length; i++)
                {
                    Log.LogMessage(
                        MessageImportance.High,
                        "compiled-item:{0}:{1}",
                        Items[i].ItemSpec,
                        Items[i].GetMetadata("Source"));
                }
            }

            return true;
        }

        public void Cancel()
        {
            Volatile.Write(ref s_cancellationObserved, 1);
        }

        internal static void ResetState()
        {
            Volatile.Write(ref s_failNextConstructor, 0);
            Volatile.Write(ref s_cancellationObserved, 0);
        }
    }

    [MSBuildMultiThreadableTask]
    public sealed class CompiledActionGraphEnvironmentInterfaceTask : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        public override bool Execute() => TaskEnvironment != null;
    }

    [MSBuildMultiThreadableTask]
    public sealed class CompiledActionGraphEnvironmentConstructorTask : Microsoft.Build.Utilities.Task
    {
        public CompiledActionGraphEnvironmentConstructorTask(TaskEnvironment taskEnvironment)
        {
            ArgumentNullException.ThrowIfNull(taskEnvironment);
        }

        public override bool Execute() => true;
    }

    public sealed class CompiledActionGraphLegacyEnvironmentTask : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }
}
