// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
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

        validator.Validate(project, "Build");
    }

    private InvalidProjectFileException ValidateFailure(
        string projectXml,
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        return Should.Throw<InvalidProjectFileException>(() => validator.Validate(project, "Build"));
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
