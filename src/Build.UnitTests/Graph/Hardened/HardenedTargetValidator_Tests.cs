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
    public void AllowsTargetInputsAndOutputs()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" Inputs="input.txt" Outputs="output.txt" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void AllowsDeferredTargetInputsWhenTheyDoNotDetermineBatching()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="Generate;Consume" />
              <Target Name="Generate">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
              </Target>
              <Target Name="Consume" Inputs="@(Generated)" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsDeferredMetadataThatDeterminesTargetBatching()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="Generate;Consume" />
              <Target Name="Generate">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
              </Target>
              <Target Name="Consume" Inputs="@(Generated)" Outputs="%(Generated.OutputPath)" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("batching of target 'Consume'");
    }

    [Fact]
    public void OutputsRetainsLegacyReturnRoleWhenReturnsIsAbsent()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" Outputs="$(Generated)">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
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
    public void CollectsMissingTargetsAcrossRequestedAndDependencyClosure()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build" DependsOnTargets="MissingDependency;Existing" />
              <Target Name="Existing">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """);

        HardenedTargetValidator validator = new();

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            validator.Validate(project, ["MissingRequested", "Build"]);

        diagnostics.Count.ShouldBe(3);
        diagnostics.Count(diagnostic => diagnostic.ErrorCode == "MSB4057").ShouldBe(2);
        diagnostics.Count(diagnostic => diagnostic.ErrorCode == "MSB4287").ShouldBe(1);
    }

    [Fact]
    public void TargetAssignedPropertyCanDetermineLaterTargetDependency()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="SetDependency;Dispatch" />
              <Target Name="SetDependency">
                <PropertyGroup>
                  <NextTarget>Leaf</NextTarget>
                </PropertyGroup>
              </Target>
              <Target Name="Dispatch" DependsOnTargets="$(NextTarget)" />
              <Target Name="Leaf">
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
    public void UnconditionalPropertyAssignmentOverwritesDeferredStateForLaterTargetDependency()
    {
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="SetDependency;Dispatch" />
              <Target Name="SetDependency">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="NextTarget" />
                </Generate>
                <PropertyGroup>
                  <NextTarget>Leaf</NextTarget>
                </PropertyGroup>
              </Target>
              <Target Name="Dispatch" DependsOnTargets="$(NextTarget)" />
              <Target Name="Leaf">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void FalseTargetConditionSkipsDependenciesAndBody()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build"
                      Condition="'false' == 'true'"
                      DependsOnTargets="Missing">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void CollectsTargetDependencyExpansionFailure()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build"
                      DependsOnTargets="$([System.String]::MethodThatDoesNotExist())" />
              <Target Name="Other">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """);

        HardenedTargetValidator validator = new();

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            validator.Validate(project, ["Build", "Other"]);

        diagnostics.Count.ShouldBe(2);
        diagnostics.ShouldContain(diagnostic => diagnostic.ErrorCode == "MSB4186");
        diagnostics.ShouldContain(diagnostic => diagnostic.ErrorCode == "MSB4287");
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

    [Fact]
    public void ExpandsComputedPropertyOutputDestination()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <PropertyGroup>
                <OutputName>Generated</OutputName>
              </PropertyGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="$(OutputName)" />
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
    public void ExpandsComputedItemOutputDestination()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <PropertyGroup>
                <Suffix>Files</Suffix>
              </PropertyGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Discovered$(Suffix)" />
                </Generate>
                <PureConsume Items="@(DiscoveredFiles)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("item 'DiscoveredFiles'");
        exception.Message.ShouldNotContain("Discovered$(Suffix)");
    }

    [Fact]
    public void RejectsDeferredOutputDestinationWithoutUpdatingRawName()
    {
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <Generate>
                  <Output TaskParameter="Result" PropertyName="$(Deferred)" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4288");
        diagnostics[0].Message.ShouldContain("PropertyName of output from task 'Generate'");
    }

    [Fact]
    public void OutputTaskParameterContributesToBatching()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>source</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="%(Input.Kind)" ItemName="Output" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsDeferredMetadataInOutputTaskParameter()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
                <Generate>
                  <Output TaskParameter="%(Generated.Kind)" ItemName="Output" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("batching of task 'Generate'");
    }

    [Fact]
    public void RejectsDeferredPropertyInOutputTaskParameter()
    {
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <Generate>
                  <Output TaskParameter="$(Deferred)" ItemName="Output" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4288");
        diagnostics[0].Message.ShouldContain("TaskParameter");
    }

    [Fact]
    public void ResolvesOutputDestinationAgainstTargetAssignedProperty()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <PropertyGroup>
                <Suffix>Old</Suffix>
              </PropertyGroup>
              <Target Name="Build">
                <PropertyGroup>
                  <Suffix>New</Suffix>
                </PropertyGroup>
                <Generate>
                  <Output TaskParameter="Result" ItemName="Discovered$(Suffix)" />
                </Generate>
                <PureConsume Input="@(DiscoveredNew)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("item 'DiscoveredNew'");
    }

    [Fact]
    public void ConcretePropertyOverlayDoesNotMutateProjectInstance()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <PropertyGroup>
                <Value>Old</Value>
              </PropertyGroup>
              <Target Name="Build">
                <PropertyGroup>
                  <Value>New</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """);

        HardenedTargetValidator validator = new();

        validator.Validate(project, "Build").ShouldBeEmpty();
        project.GetPropertyValue("Value").ShouldBe("Old");
    }

    [Fact]
    public void RejectsDeferredMetadataInOutputDestination()
    {
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
                <Generate>
                  <Output TaskParameter="Result" ItemName="%(Generated.Kind)" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4288");
        diagnostics[0].Message.ShouldContain("batching of task 'Generate'");
    }

    [Theory]
    [InlineData("$([MSBuild]::Escape($(Deferred)))")]
    [InlineData("$(SomeProp.Replace('a', $(Deferred)))")]
    public void RejectsNestedDeferredPropertyReferences(string expression)
    {
        InvalidProjectFileException exception = ValidateFailure(
            $"""
            <Project>
              <PropertyGroup>
                <SomeProp>abc</SomeProp>
              </PropertyGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <PropertyGroup>
                  <Value>{expression}</Value>
                </PropertyGroup>
                <PureConsume Input="$(Value)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("Deferred");
    }

    [Fact]
    public void ValidatesReturnsBatchingBeforeTargetBody()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>static</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build" Returns="%(Input.Kind)">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <ItemGroup>
                  <Input>
                    <Kind>$(Deferred)</Kind>
                  </Input>
                </ItemGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void PropertyBatchingStatePropagatesToAssignedProperty()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
                <PropertyGroup>
                  <Value>%(Generated.Kind)</Value>
                </PropertyGroup>
                <PureConsume Input="$(Value)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void ReportsTargetConditionMetadataOnce()
    {
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(
            """
            <Project>
              <Target Name="Build" Condition="'%(Kind)' != ''" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4286");
        diagnostics[0].Message.ShouldContain("metadata expressions in the condition of target 'Build'");
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

        diagnostics.Count.ShouldBe(2);
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
    public void AllowsStaticContinueOnError()
    {
        ValidateSuccess(
            """
            <Project>
              <PropertyGroup>
                <FailureBehavior>WarnAndContinue</FailureBehavior>
              </PropertyGroup>
              <Target Name="Build">
                <Generate ContinueOnError="$(FailureBehavior)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void AllowsStaticMetadataToBatchContinueOnError()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <FailureBehavior>WarnAndContinue</FailureBehavior>
                </Input>
              </ItemGroup>
              <Target Name="Build">
                <Generate Input="@(Input)"
                          ContinueOnError="%(Input.FailureBehavior)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsDeferredContinueOnError()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="FailureBehavior" />
                </Generate>
                <Consume ContinueOnError="$(FailureBehavior)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("ContinueOnError");
    }

    [Fact]
    public void ValidatesOnErrorTargetClosure()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate />
                <OnError ExecuteTargets="Cleanup" />
              </Target>
              <Target Name="Cleanup">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4287");
    }

    [Fact]
    public void CollectsMissingOnErrorTarget()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate />
                <OnError ExecuteTargets="Missing" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4057");
    }

    [Fact]
    public void AllowsTaskStatusToSelectOnErrorPath()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate />
                <OnError ExecuteTargets="Cleanup"
                         Condition="'$(MSBuildLastTaskResult)' == 'false'" />
              </Target>
              <Target Name="Cleanup" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsTaskStatusInOrdinaryStaticCondition()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate />
                <PropertyGroup Condition="'$(MSBuildLastTaskResult)' == 'true'">
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
        exception.Message.ShouldContain("MSBuildLastTaskResult");
    }

    [Fact]
    public void FalseTaskConditionDoesNotCreateOutputs()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate Condition="false">
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
    }

    [Fact]
    public void FalseTaskConditionDoesNotValidateUnusedParameters()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <PureConsume Condition="false" Input="$(Deferred)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    [Fact]
    public void FalseTaskConditionDoesNotSetTaskStatus()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate Condition="false" />
                <PropertyGroup Condition="'$(MSBuildLastTaskResult)' == ''">
                  <Observed>true</Observed>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void FalseTaskConditionStillValidatesBatching()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
                <PureConsume Condition="false" Input="%(Generated.Identity)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("batching of task 'PureConsume'");
    }

    [Fact]
    public void AllowsStaticMSBuildRoutingInputs()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <ProjectToBuild Include="child.proj">
                  <Properties>Configuration=Debug</Properties>
                  <UndefineProperties>RuntimeIdentifier</UndefineProperties>
                  <AdditionalProperties>Platform=AnyCPU</AdditionalProperties>
                  <ToolsVersion>Current</ToolsVersion>
                  <SkipNonexistentProjects>Build</SkipNonexistentProjects>
                </ProjectToBuild>
              </ItemGroup>
              <Target Name="Build">
                <MSBuild
                  Projects="@(ProjectToBuild)"
                  Targets="Build"
                  Properties="TargetFramework=net11.0"
                  RemoveProperties="SelfContained"
                  ToolsVersion="Current"
                  SkipNonexistentProjects="Build"
                  SkipNonexistentTargets="true"
                  TargetAndPropertyListSeparators=";" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void RejectsDeferredMSBuildProjects()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="ProjectsToBuild" />
                </Generate>
                <MSBuild Projects="@(ProjectsToBuild)" Targets="Build" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("parameter 'Projects'");
    }

    [Theory]
    [InlineData("Targets")]
    [InlineData("Properties")]
    [InlineData("RemoveProperties")]
    [InlineData("ToolsVersion")]
    [InlineData("SkipNonexistentProjects")]
    [InlineData("SkipNonexistentTargets")]
    [InlineData("TargetAndPropertyListSeparators")]
    public void RejectsDeferredMSBuildRoutingParameter(string parameterName)
    {
        InvalidProjectFileException exception = ValidateFailure(
            $"""
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <MSBuild Projects="child.proj" {parameterName}="$(Deferred)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain($"parameter '{parameterName}'");
    }

    [Theory]
    [InlineData("Properties")]
    [InlineData("UndefineProperties")]
    [InlineData("AdditionalProperties")]
    [InlineData("ToolsVersion")]
    [InlineData("SkipNonexistentProjects")]
    public void RejectsDeferredMSBuildProjectMetadata(string metadataName)
    {
        InvalidProjectFileException exception = ValidateFailure(
            $"""
            <Project>
              <ItemGroup>
                <ProjectToBuild Include="child.proj" />
              </ItemGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <ItemGroup>
                  <ProjectToBuild Update="child.proj">
                    <{metadataName}>$(Deferred)</{metadataName}>
                  </ProjectToBuild>
                </ItemGroup>
                <MSBuild Projects="@(ProjectToBuild)" Targets="Build" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain($"'{metadataName}' metadata");
    }

    [Fact]
    public void AllowsDeferredUnrelatedMSBuildProjectMetadata()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <ProjectToBuild Include="child.proj" />
                <TargetToBuild Include="Build" />
              </ItemGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Deferred" />
                </Generate>
                <ItemGroup>
                  <ProjectToBuild Update="child.proj">
                    <Payload>$(Deferred)</Payload>
                  </ProjectToBuild>
                  <TargetToBuild Update="Build">
                    <Payload>$(Deferred)</Payload>
                  </TargetToBuild>
                </ItemGroup>
                <MSBuild Projects="@(ProjectToBuild)" Targets="@(TargetToBuild)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void ValidatesCallTargetClosure()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="Called" />
              </Target>
              <Target Name="Called">
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
    public void RejectsDeferredCallTargetTargets()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="CalledTarget" />
                </Generate>
                <CallTarget Targets="$(CalledTarget)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("Targets");
    }

    [Fact]
    public void FalseCallTargetConditionSkipsCalledClosure()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="Called" Condition="false" />
              </Target>
              <Target Name="Called">
                <PropertyGroup>
                  <Value>$([System.Guid]::NewGuid())</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void CollectsMissingCallTargetTarget()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="Missing" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4057");
    }

    [Fact]
    public void CallTargetDoesNotLeakPropertyAssignmentsToCaller()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Value" />
                </Generate>
                <CallTarget Targets="SetValue" />
                <PureConsume Input="$(Value)" />
              </Target>
              <Target Name="SetValue">
                <PropertyGroup>
                  <Value>static</Value>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("parameter 'Input'");
    }

    [Fact]
    public void CallTargetDoesNotSeeEarlierCallerAssignments()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Value" />
                </Generate>
                <CallTarget Targets="Consume" />
              </Target>
              <Target Name="Consume">
                <PureConsume Input="$(Value)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    [Fact]
    public void CallTargetAssignmentsAreVisibleAfterCallerCompletes()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="GenerateValue" />
              </Target>
              <Target Name="GenerateValue">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Value" />
                </Generate>
              </Target>
              <Target Name="Consume" AfterTargets="Build">
                <PureConsume Input="$(Value)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("parameter 'Input'");
    }

    [Fact]
    public void CallerAssignmentsOverrideCallTargetAssignments()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="GenerateValue" />
                <PropertyGroup>
                  <Value>static</Value>
                </PropertyGroup>
              </Target>
              <Target Name="GenerateValue">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Value" />
                </Generate>
              </Target>
              <Target Name="Consume" AfterTargets="Build">
                <PureConsume Input="$(Value)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    [Theory]
    [InlineData("Returns")]
    [InlineData("Outputs")]
    public void StaticCallTargetReturnsRemainStatic(string returnAttribute)
    {
        ValidateSuccess(
            $"""
            <Project>
              <ItemGroup>
                <Returned Include="static">
                  <Payload>value</Payload>
                </Returned>
              </ItemGroup>
              <Target Name="Build">
                <CallTarget Targets="Called">
                  <Output TaskParameter="TargetOutputs" ItemName="Result" />
                </CallTarget>
                <PureConsume Input="@(Result)" />
              </Target>
              <Target Name="Called" {returnAttribute}="@(Returned)" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    [Fact]
    public void DeferredCallTargetReturnsPreserveProducerOrigin()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="Called">
                  <Output TaskParameter="TargetOutputs" ItemName="Result" />
                </CallTarget>
                <PureConsume Input="@(Result)" />
              </Target>
              <Target Name="Called" Returns="@(Returned)">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Returned" />
                </Generate>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("task 'CallTarget'");
        exception.Message.ShouldContain("task 'Generate'");
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

    [Fact]
    public void CollectTargetFrameworkForTelemetryValidatesWithoutSpecialCasing()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="_CollectTargetFrameworkForTelemetry" />
              <Target Name="_CollectTargetFrameworkForTelemetry">
                <ItemGroup>
                  <TFTelemetry Include="TargetFrameworkVersion" Value="$([MSBuild]::Escape('$(TargetFrameworkMoniker)'))" />
                  <TFTelemetry Include="RuntimeIdentifier" Value="$(RuntimeIdentifier)" />
                  <TFTelemetry Include="SelfContained" Value="$(SelfContained)" />
                  <TFTelemetry Include="UseApphost" Value="$(UseApphost)" />
                  <TFTelemetry Include="OutputType" Value="$(OutputType)" />
                  <TFTelemetry Include="UseArtifactsOutput" Value="$(UseArtifactsOutput)" />
                  <TFTelemetry Include="ArtifactsPathLocationType" Value="$(_ArtifactsPathLocationType)" />
                  <TFTelemetry Include="TargetPlatformIdentifier" Value="$(TargetPlatformIdentifier)" />
                  <TFTelemetry Include="UseMonoRuntime" Value="$(UseMonoRuntime)" />
                  <TFTelemetry Include="PublishAot" Value="$(PublishAot)" />
                  <TFTelemetry Include="PublishTrimmed" Value="$(PublishTrimmed)" />
                  <TFTelemetry Include="PublishSelfContained" Value="$(PublishSelfContained)" />
                  <TFTelemetry Include="PublishReadyToRun" Value="$(PublishReadyToRun)" />
                  <TFTelemetry Include="PublishReadyToRunComposite" Value="$(PublishReadyToRunComposite)" />
                  <TFTelemetry Include="PublishProtocol" Value="$(PublishProtocol)" />
                  <TFTelemetry Include="Configuration" Value="$(Configuration)" Hash="true" />
                </ItemGroup>
                <AllowEmptyTelemetry EventName="targetframeworkeval" EventData="@(TFTelemetry)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void AllowsQualifiedAndUnqualifiedStaticTaskBatching()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>source</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build">
                <Consume Items="@(Input)" Kind="%(Kind)" QualifiedKind="%(Input.Kind)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void AllowsStaticMetadataInItemAndTaskConditions()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>source</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup>
                  <Selected Include="%(Input.Identity)" Condition="'%(Input.Kind)' == 'source'" />
                </ItemGroup>
                <Consume Items="@(Selected)" Condition="'%(Input.Kind)' == 'source'" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Fact]
    public void RejectsDeferredMetadataUsedAsBatchKey()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" ItemName="Generated" />
                </Generate>
                <Consume Items="@(Generated)" Kind="%(Generated.Kind)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("batching of task 'Consume'");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void AllowsDeferredPayloadMetadataToFlowToDeclaredIOTask()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <ItemGroup>
                  <Input Include="a">
                    <Payload>$(Generated)</Payload>
                  </Input>
                </ItemGroup>
                <Consume Items="@(Input)" />
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
    public void RejectsDeferredPayloadMetadataPassedToPureTask()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <ItemGroup>
                  <Input Include="a">
                    <Payload>$(Generated)</Payload>
                  </Input>
                </ItemGroup>
                <PureConsume Items="@(Input)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("metadata 'Payload' on item 'Input'");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void SupportsStaticItemOperationsAndMetadataFilters()
    {
        ValidateSuccess(
            """
            <Project>
              <ItemGroup>
                <Source Include="a">
                  <Key>one</Key>
                  <Keep>value</Keep>
                  <Drop>value</Drop>
                </Source>
                <Removal Include="b">
                  <Key>two</Key>
                </Removal>
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup>
                  <Kept Include="@(Source)" KeepMetadata="Key;Keep" KeepDuplicates="true" />
                  <RemovedMetadata Include="@(Source)" RemoveMetadata="Drop" />
                  <Source Remove="@(Removal)" MatchOnMetadata="Key" MatchOnMetadataOptions="CaseInsensitive" />
                  <Kept RemoveMetadata="Keep">
                    <Updated>value</Updated>
                  </Kept>
                </ItemGroup>
                <Consume First="@(Kept)" Second="@(RemovedMetadata)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Consume"] = HardenedTaskClassification.DeclaredIO,
            });
    }

    [Theory]
    [InlineData("KeepMetadata=\"Key\"")]
    [InlineData("RemoveMetadata=\"Payload\"")]
    public void MetadataFiltersCanRemoveDeferredPayloadFromCopiedItems(string filter)
    {
        ValidateSuccess(
            $"""
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <ItemGroup>
                  <Source Include="a">
                    <Key>static</Key>
                    <Payload>$(Generated)</Payload>
                  </Source>
                  <Copy Include="@(Source)" {filter} />
                </ItemGroup>
                <PureConsume Items="@(Copy)" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });
    }

    [Fact]
    public void RejectsDeferredMetadataUsedByMatchOnMetadata()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <ItemGroup>
                <Removal Include="b">
                  <Key>static</Key>
                </Removal>
              </ItemGroup>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <ItemGroup>
                  <Input Include="a">
                    <Key>$(Generated)</Key>
                  </Input>
                  <Input Remove="@(Removal)" MatchOnMetadata="Key" />
                </ItemGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("MatchOnMetadata 'Key' on item 'Input'");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void PreservesOriginThroughIncludeAndTransform()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <Generate>
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
                <ItemGroup>
                  <Source Include="a">
                    <Payload>$(Generated)</Payload>
                  </Source>
                  <Copy Include="@(Source)" />
                </ItemGroup>
                <PureConsume Input="@(Copy->'%(Payload)')" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>
            {
                ["Generate"] = HardenedTaskClassification.DeclaredIO,
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        exception.ErrorCode.ShouldBe("MSB4288");
        exception.Message.ShouldContain("transform of item 'Copy'");
        exception.Message.ShouldContain("Include into item 'Copy'");
        exception.Message.ShouldContain("output 'Result' of task 'Generate'");
    }

    [Fact]
    public void BlockedMetadataDoesNotCreateDeferredCascade()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <Input Include="a">
                    <Payload>$([System.IO.File]::ReadAllText('input.txt'))</Payload>
                  </Input>
                </ItemGroup>
                <PureConsume Input="@(Input->'%(Payload)')" />
              </Target>
            </Project>
            """);

        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskClassification>
            {
                ["PureConsume"] = HardenedTaskClassification.Pure,
            });

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4287");
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
        IReadOnlyList<InvalidProjectFileException> diagnostics = ValidateDiagnostics(projectXml, taskClassifications);
        diagnostics.ShouldNotBeEmpty();
        return diagnostics[0];
    }

    private IReadOnlyList<InvalidProjectFileException> ValidateDiagnostics(
        string projectXml,
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        return validator.Validate(project, "Build");
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
