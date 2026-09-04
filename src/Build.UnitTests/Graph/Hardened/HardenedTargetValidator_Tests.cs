// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph.Hardened;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests.Graph.Hardened;

public sealed class HardenedTargetValidator_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void RejectsFileReadInTargetPropertyGroupValue()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <PropertyGroup>
                  <Contents>$([System.IO.File]::ReadAllText('input.txt'))</Contents>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void RejectsClockReadInTargetPropertyCondition()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <PropertyGroup>
                  <Value Condition="$([System.DateTime]::Now) != ''">set</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void RejectsUnsupportedTargetInputs()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build" Inputs="input.txt" Outputs="output.txt" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4286");
    }

    [Fact]
    public void ValidatesDependsOnTargetsClosure()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <PropertyGroup>
                <BuildDependsOn>Prepare;Compile</BuildDependsOn>
              </PropertyGroup>
              <Target Name="Build" DependsOnTargets="$(BuildDependsOn)" Returns="@(Output)" />
              <Target Name="Prepare" />
              <Target Name="Compile">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void ValidatesBeforeAndAfterTargets()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build" />
              <Target Name="BeforeBuild" BeforeTargets="Build" />
              <Target Name="AfterBuild" AfterTargets="Build">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void AllowsReturns()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" Returns="@(Output)">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Output" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Theory]
    [InlineData("$([System.IO.Path]::Combine('a', 'b'))")]
    [InlineData("$([System.IO.Path]::GetFileName('a/b.txt'))")]
    public void AllowsPurePathFunctions(string expression)
    {
        ValidateSuccess(
            $"""
            <Project>
              <Target Name="Build">
                <PropertyGroup>
                  <Value>{expression}</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Theory]
    [InlineData("$([System.IO.Path]::GetTempPath())")]
    [InlineData("$([System.IO.Path]::GetRandomFileName())")]
    [InlineData("$([System.IO.Path]::GetFullPath('relative'))")]
    public void RejectsAmbientPathFunctions(string expression)
    {
        InvalidProjectFileException exception = ValidateFailure(
            $"""
            <Project>
              <Target Name="Build">
                <PropertyGroup>
                  <Value>{expression}</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void DoesNotTreatFunctionNameSubstringAsExists()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" Condition="'FileExists(value)' != ''" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void CollectsAllIndependentDiagnostics()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build" DependsOnTargets="First;Second" />
              <Target Name="First">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
              <Target Name="Second" Inputs="input.txt" Outputs="output.txt">
                <PropertyGroup>
                  <Value>$([System.IO.File]::ReadAllText('input.txt'))</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """);

        HardenedTargetValidator validator = new();

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");

        diagnostics.Count.ShouldBe(4);
        diagnostics.Count(diagnostic => diagnostic.ErrorCode == "MSB4286").ShouldBe(2);
        diagnostics.Count(diagnostic => diagnostic.ErrorCode == "MSB4287").ShouldBe(2);
    }

    [Fact]
    public void DoesNotEvaluateProhibitedTargetEdgeExpression()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build"
                      DependsOnTargets="$([System.IO.File]::ReadAllText('does-not-exist.txt'))" />
            </Project>
            """);

        HardenedTargetValidator validator = new();

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void AllowsDeferredOutputToFlowToDeclaredIOTask()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <Consume Input="$(Generated)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsDeferredOutputPassedToPureTask()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <PureConsume Input="$(Generated)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("Generated");
    }

    [Fact]
    public void RejectsDeferredOutputInPropertyGroupCondition()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <PropertyGroup Condition="'$(Generated)' != ''">
                  <Observed>true</Observed>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void PureOutputRemainsStaticForLaterPureTask()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <PureGenerate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </PureGenerate>
                <PureConsume Input="$(Generated)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["PureGenerate"] = HardenedTaskClassification.Pure,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    private void ValidateSuccess(
        string projectXml,
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        validator.Validate(project, "Build").ShouldBeEmpty();
    }

    private InvalidProjectFileException ValidateFailure(
        string projectXml,
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");
        diagnostics.ShouldNotBeEmpty();
        return diagnostics[0];
    }

    private static ProjectInstance CreateProjectInstance(TestEnvironment environment, string projectXml)
    {
        using ProjectFromString project = new(
            projectXml.Cleanup(),
            globalProperties: null,
            toolsVersion: null,
            environment.CreateProjectCollection().Collection);

        return project.Project.CreateProjectInstance();
    }
}
