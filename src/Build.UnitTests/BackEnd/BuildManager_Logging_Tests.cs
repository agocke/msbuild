// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;
using static Microsoft.Build.UnitTests.ObjectModelHelpers;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    public class BuildManager_Logging_Tests : IDisposable
    {
        private string _mainProject = @"
<Project>

  <Target Name=`MainTarget`>
    <MSBuild Projects=`{0}` Targets=`ChildTarget` />
  </Target>

</Project>";

        private string _childProjectWithCustomBuildEvent = $@"
<Project>

    <UsingTask TaskName=""CustomBuildEventTask"" AssemblyFile=""{Assembly.GetExecutingAssembly().Location}"" />
    <Target Name=`ChildTarget`>
        <CustomBuildEventTask />
    </Target>

</Project>";


        /// <summary>
        /// The mock logger for testing.
        /// </summary>
        private readonly MockLogger _logger;

        /// <summary>
        /// The standard build manager for each test.
        /// </summary>
        private readonly BuildManager _buildManager;

        /// <summary>
        /// The project collection used.
        /// </summary>
        private readonly ProjectCollection _projectCollection;

        private readonly TestEnvironment _env;
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// SetUp
        /// </summary>
        public BuildManager_Logging_Tests(ITestOutputHelper output)
        {
            _output = output;
            // Ensure that any previous tests which may have been using the default BuildManager do not conflict with us.
            BuildManager.DefaultBuildManager.Dispose();

            _logger = new MockLogger(output);
            _buildManager = new BuildManager();
            _projectCollection = new ProjectCollection();

            _env = TestEnvironment.Create(output);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CompactExecutionPreservesProjectStartedPropertiesInBinaryLog(bool outOfProc)
        {
            _env.SetEnvironmentVariable("MSBUILDLOGPROPERTIESANDITEMSAFTEREVALUATION", "0");
            var file = _env.CreateFile(
                "compact-execution.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>initial%3bvalue</Value>
                    <Unused>untouched</Unused>
                  </PropertyGroup>
                  <Target Name="Build">
                    <PropertyGroup>
                      <Value>$(Value)-changed</Value>
                    </PropertyGroup>
                    <Message Text="result:$(Value)" Importance="high" />
                  </Target>
                </Project>
                """);
            ProjectInstance project = ProjectInstance.FromFile(file.Path, new ProjectOptions
            {
                EvaluationContext = EvaluationContext.CreateForCompiledModuleEvaluation(useCompiledModuleEffectBatches: true),
                ProjectCollection = _projectCollection,
            });
            project.CompactPropertyCount.ShouldBe(2);
            string binlog = Path.Combine(_env.CreateFolder().Path, "compact.binlog");
            MockLogger logger = new(_output);
            BuildResult result = _buildManager.Build(
                new BuildParameters(_projectCollection)
                {
                    DisableInProcNode = outOfProc,
                    EnableNodeReuse = false,
                    Loggers = [logger, new BinaryLogger { Parameters = $"{binlog};ProjectImports=None" }],
                },
                new BuildRequestData(project, ["Build"], null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));

            result.ShouldHaveSucceeded();
            result.ProjectStateAfterBuild.GetPropertyValue("Value").ShouldBe("initial;value-changed");
            logger.AssertLogContains("result:initial;value-changed");
            BinaryLogReplayEventSource replay = new();
            MockLogger replayLogger = new(_output);
            replayLogger.Initialize(replay);
            replay.Replay(binlog);
            ProjectStartedEventArgs started = replayLogger.ProjectStartedEvents.ShouldHaveSingleItem();
            DictionaryEntry value = started.Properties.Cast<DictionaryEntry>()
                .Single(entry => (string)entry.Key == "Value");
            value.Value.ShouldBe("initial;value");
            started.Properties.Cast<DictionaryEntry>()
                .Single(entry => (string)entry.Key == "Unused").Value.ShouldBe("untouched");
        }

        [DotNetOnlyFact]
        public void Build_WithCustomBuildArgs_ShouldEmitErrorOnNetCore() => Build_WithCustomBuildArgs_ShouldEmitEvent<BuildErrorEventArgs>();

        [WindowsFullFrameworkOnlyFact]
        public void Build_WithCustomBuildArgs_ShouldEmitWarningOnFramework() => Build_WithCustomBuildArgs_ShouldEmitEvent<BuildWarningEventArgs>();

        private void Build_WithCustomBuildArgs_ShouldEmitEvent<T>() where T : LazyFormattedBuildEventArgs
        {
            var testFiles = _env.CreateTestProjectWithFiles(string.Empty, ["main", "child1"], string.Empty);

            ILoggingService service = LoggingService.CreateLoggingService(LoggerMode.Synchronous, 1);
            service.RegisterLogger(_logger);

            _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");

            _buildManager.BeginBuild(BuildParameters);

            try
            {
                var child1ProjectPath = testFiles.CreatedFiles[1];
                var cleanedUpChildContents = CleanupFileContents(_childProjectWithCustomBuildEvent);
                File.WriteAllText(child1ProjectPath, cleanedUpChildContents);

                var mainProjectPath = testFiles.CreatedFiles[0];
                var cleanedUpMainContents = CleanupFileContents(string.Format(_mainProject, child1ProjectPath));
                File.WriteAllText(mainProjectPath, cleanedUpMainContents);

                var buildRequestData = new BuildRequestData(
                   mainProjectPath,
                   new Dictionary<string, string>(),
                   MSBuildConstants.CurrentToolsVersion,
                   ["MainTarget"],
                   null);

                var submission = _buildManager.PendBuildRequest(buildRequestData);
                var result = submission.Execute();
                var allEvents = _logger.AllBuildEvents;

                allEvents.OfType<T>().ShouldHaveSingleItem();
                allEvents.First(x => x is T).Message.ShouldContain(
                    string.Format(ResourceUtilities.GetResourceString("DeprecatedEventSerialization"),
                    "MyCustomBuildEventArgs"));
            }
            finally
            {
                _buildManager.EndBuild();
            }
        }

        private BuildParameters BuildParameters => new BuildParameters(_projectCollection)
        {
            DisableInProcNode = true,
            EnableNodeReuse = false,
            Loggers = new ILogger[] { _logger }
        };

        /// <summary>
        /// TearDown
        /// </summary>
        public void Dispose()
        {
            try
            {
                _buildManager.Dispose();
                _projectCollection.Dispose();
            }
            finally
            {
                _env.Dispose();
            }
        }
    }
}
