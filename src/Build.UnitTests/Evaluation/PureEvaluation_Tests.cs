// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Internal;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.Evaluation
{
    public sealed class PureEvaluation_Tests : IDisposable
    {
        private readonly TestEnvironment _env = TestEnvironment.Create();

        public void Dispose()
        {
            _env.Dispose();
        }

        [Fact]
        public void ClassicEvaluationAllowsAmbientPropertyFunctions()
        {
            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup>
                    <Value>$([System.DateTime]::UtcNow)</Value>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Classic);

            project.GetPropertyValue("Value").ShouldNotBeEmpty();
        }

        [Fact]
        public void PureEvaluationDoesNotImportEnvironmentProperties()
        {
            const string propertyName = "MSBUILD_PURE_EVALUATION_TEST_PROPERTY";
            _env.SetEnvironmentVariable(propertyName, "ambient");

            string projectContents =
                $"""
                 <Project>
                   <PropertyGroup>
                     <Value>$({propertyName})</Value>
                   </PropertyGroup>
                 </Project>
                 """;

            Project classicProject = Evaluate(projectContents, ProjectEvaluationMode.Classic);
            Project pureProject = Evaluate(projectContents, ProjectEvaluationMode.Pure);

            classicProject.GetPropertyValue("Value").ShouldBe("ambient");
            classicProject.GetProperty(propertyName).IsEnvironmentProperty.ShouldBeTrue();
            pureProject.GetPropertyValue("Value").ShouldBeEmpty();
            pureProject.GetProperty(propertyName).ShouldBeNull();
        }

        [Theory]
        [InlineData("$([System.DateTime]::UtcNow)")]
        [InlineData("$([System.Guid]::NewGuid())")]
        [InlineData("$([System.IO.Path]::GetRandomFileName())")]
        [InlineData("$([System.IO.Path]::GetFullPath('relative'))")]
        [InlineData("$([System.IO.Directory]::GetParent('a/b').FullName)")]
        [InlineData("$([System.Environment]::GetEnvironmentVariable('PATH'))")]
        [InlineData("$([System.IO.File]::ReadAllText('input.txt'))")]
        [InlineData("$([Microsoft.Build.Utilities.ToolLocationHelper]::GetPlatformSDKLocation('Windows', '10.0'))")]
        [InlineData("$(Registry:HKEY_CURRENT_USER\\Software@TestValue)")]
        public void PureEvaluationRejectsAmbientPropertyFunctions(string expression)
        {
            InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
                () => Evaluate(
                    $"""
                     <Project>
                       <PropertyGroup>
                         <Value>{expression}</Value>
                       </PropertyGroup>
                     </Project>
                     """,
                    ProjectEvaluationMode.Pure));

            exception.ErrorCode.ShouldBe("MSB4286");
        }

        [Fact]
        public void PureEvaluationIgnoresEnableAllPropertyFunctionsEscapeHatch()
        {
            AppContext.TryGetSwitch("Microsoft.Build.EnableAllPropertyFunctions", out bool originalValue);

            try
            {
                AppContext.SetSwitch("Microsoft.Build.EnableAllPropertyFunctions", true);

                InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
                    () => Evaluate(
                        """
                        <Project>
                          <PropertyGroup>
                            <Value>$([System.Diagnostics.Process]::GetCurrentProcess())</Value>
                          </PropertyGroup>
                        </Project>
                        """,
                        ProjectEvaluationMode.Pure));

                exception.ErrorCode.ShouldBe("MSB4286");
            }
            finally
            {
                AppContext.SetSwitch("Microsoft.Build.EnableAllPropertyFunctions", originalValue);
                AvailableStaticMethods.Reset_ForUnitTestsOnly();
            }
        }

        [Fact]
        public void PureEvaluationAllowsPurePropertyFunctions()
        {
            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup>
                    <Value>$([System.String]::Copy('value').ToUpperInvariant())</Value>
                    <VersionIsNewer>$([MSBuild]::VersionGreaterThan('11.0', '10.0'))</VersionIsNewer>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("Value").ShouldBe("VALUE");
            project.GetPropertyValue("VersionIsNewer").ShouldBe("True");
        }

        [Fact]
        public void PureEvaluationAllowsPureDirectoryPathManipulation()
        {
            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup>
                    <Parent>$([System.IO.Directory]::GetParent('$(MSBuildProjectDirectory)/one/two').FullName)</Parent>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("Parent").ShouldEndWith($"{Path.DirectorySeparatorChar}one");
        }

        [Fact]
        public void PureEvaluationAllowsFullPathWithExplicitBase()
        {
            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup>
                    <FullPath>$([System.IO.Path]::GetFullPath('child', '$(MSBuildProjectDirectory)'))</FullPath>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("FullPath").ShouldBe(
                Path.Combine(project.DirectoryPath, "child"));
        }

        [Fact]
        public void PureEvaluationAllowsFullPathOfFullyQualifiedInput()
        {
            string path = Path.Combine(
                Path.GetPathRoot(_env.DefaultTestDirectory.Path)!,
                "one",
                "..",
                "two");
            Project project = Evaluate(
                $"""
                <Project>
                  <PropertyGroup>
                    <FullPath>$([System.IO.Path]::GetFullPath('{path}'))</FullPath>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("FullPath").ShouldBe(
                Path.GetFullPath(path));
        }

        [Theory]
        [InlineData("GetPlatformSDKLocation")]
        [InlineData("GetPlatformSDKDisplayName")]
        public void PureEvaluationAllowsEmptyPlatformSdkQueries(
            string method)
        {
            _ = Evaluate(
                $"""
                <Project>
                  <PropertyGroup>
                    <Value>$([Microsoft.Build.Utilities.ToolLocationHelper]::{method}('', ''))</Value>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);
        }

        [Fact]
        public void PureEvaluationRejectsFileSystemInfoObservations()
        {
            InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
                () => Evaluate(
                    """
                    <Project>
                      <PropertyGroup>
                        <ParentExists>$([System.IO.Directory]::GetParent('one/two').Exists)</ParentExists>
                      </PropertyGroup>
                    </Project>
                    """,
                    ProjectEvaluationMode.Pure));

            exception.ErrorCode.ShouldBe("MSB4286");
        }

        [Fact]
        public void PureEvaluationAllowsDeclarativeFileOperations()
        {
            _env.CreateFile("marker.txt", "marker");
            _env.CreateFile("source.cs", "class C {}");

            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup Condition="Exists('marker.txt')">
                    <MarkerExists>true</MarkerExists>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="*.cs" />
                  </ItemGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("MarkerExists").ShouldBe("true");
            project.GetItems("Compile").ShouldHaveSingleItem().EvaluatedInclude.ShouldBe("source.cs");
        }

        [Fact]
        public void PureEvaluationDoesNotRejectUnexecutedBranches()
        {
            Project project = Evaluate(
                """
                <Project>
                  <PropertyGroup>
                    <Value Condition="'false' == 'true'">$([System.DateTime]::UtcNow)</Value>
                  </PropertyGroup>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.GetPropertyValue("Value").ShouldBeEmpty();
        }

        [Fact]
        public void PureEvaluationAppliesToPropertyFunctionsInItemIncludes()
        {
            InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
                () => Evaluate(
                    """
                    <Project>
                      <ItemGroup>
                        <Input Include="$([System.IO.File]::ReadAllText('input.txt'))" />
                      </ItemGroup>
                    </Project>
                    """,
                    ProjectEvaluationMode.Pure));

            exception.ErrorCode.ShouldBe("MSB4286");
        }

        [Fact]
        public void ReevaluationPreservesPureMode()
        {
            Project project = Evaluate("<Project />", ProjectEvaluationMode.Pure);
            project.Xml.AddPropertyGroup().AddProperty("Value", "$([System.DateTime]::UtcNow)");

            InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
                () => project.ReevaluateIfNecessary());

            exception.ErrorCode.ShouldBe("MSB4286");
        }

        [Fact]
        public void PureEvaluationDoesNotRestrictTargetExecution()
        {
            Project project = Evaluate(
                """
                <Project>
                  <Target Name="Run">
                    <PropertyGroup>
                      <TargetValue>$([System.Guid]::NewGuid())</TargetValue>
                    </PropertyGroup>
                  </Target>
                </Project>
                """,
                ProjectEvaluationMode.Pure);

            project.Build("Run").ShouldBeTrue();
        }

        [Fact]
        public void InvalidEvaluationModeIsRejected()
        {
            Should.Throw<ArgumentOutOfRangeException>(
                () => new ProjectOptions { EvaluationMode = (ProjectEvaluationMode)42 });
        }

        [Fact]
        public void BuildParametersClonePreservesEvaluationMode()
        {
            BuildParameters clone = new BuildParameters
            {
                ProjectEvaluationMode = ProjectEvaluationMode.Pure,
            }.Clone();

            clone.ProjectEvaluationMode.ShouldBe(ProjectEvaluationMode.Pure);
        }

        private Project Evaluate(string projectContents, ProjectEvaluationMode mode)
        {
            string projectPath = _env.CreateFile($"{Guid.NewGuid():N}.proj", projectContents).Path;

            return Project.FromFile(
                projectPath,
                new ProjectOptions
                {
                    EvaluationMode = mode,
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
        }
    }
}
