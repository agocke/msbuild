// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph.Hardened;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests.BackEnd;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests.Graph.Hardened;

public sealed class HardenedTargetValidator_Tests(ITestOutputHelper output)
{
    private static readonly HardenedTaskDescriptor s_writeCodeFragmentDescriptor =
        new(
            HardenedTaskClassification.DeclaredIO,
            requiredUnsetParameters: ["OutputDirectory"],
            inputPathParameters: ["DeclaredInputs"],
            outputPathParameters: ["DeclaredOutputs"]);

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
    public void TargetBucketBodyOrderMatchesOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>z</Kind>
                </Input>
                <Input Include="b">
                  <Kind>a</Kind>
                </Input>
                <Input Include="c">
                  <Kind>z</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build" Returns="%(Input.Kind)">
                <ItemGroup>
                  <Observed Include="@(Input->'%(Kind):%(Identity)')"
                            KeepDuplicates="true" />
                </ItemGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        DescribeItemSpecs(validationLookup.GetItems("Observed"))
            .ShouldBe(DescribeItemSpecs(ordinaryProject.GetItems("Observed")));
        DescribeItemSpecs(validationLookup.GetItems("Observed")).ShouldBe(
        [
            "z:a",
            "z:c",
            "a:b",
        ]);
    }

    [Fact]
    public void TargetBucketsKeepSiblingMutationsIsolated()
    {
        const string projectXml = """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>first</Kind>
                </Input>
                <Input Include="b">
                  <Kind>second</Kind>
                </Input>
              </ItemGroup>
              <Target Name="Build" Returns="%(Input.Kind)">
                <PropertyGroup>
                  <Last Condition="'$(Last)' == ''">%(Input.Identity)</Last>
                </PropertyGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        validationLookup.GetProperty("Last")!.EvaluatedValue
            .ShouldBe(ordinaryProject.GetPropertyValue("Last"));
        ordinaryProject.GetPropertyValue("Last").ShouldBe("b");
    }

    [Fact]
    public void FalseTargetConditionMatchesOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <ItemGroup>
                <Input Include="a;b" />
              </ItemGroup>
              <Target Name="Build" Condition="false">
                <ItemGroup>
                  <Input Remove="b" />
                </ItemGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        DescribeItemSpecs(validationLookup.GetItems("Input"))
            .ShouldBe(DescribeItemSpecs(ordinaryProject.GetItems("Input")));
        DescribeItemSpecs(validationLookup.GetItems("Input")).ShouldBe(["a", "b"]);
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
    public void CollectsCircularTargetDependency()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build" DependsOnTargets="Prepare" />
              <Target Name="Prepare" DependsOnTargets="Build" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4006");
        exception.Message.ShouldContain("Build");
    }

    [Fact]
    public void CollectsCircularCallTargetDependency()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <Target Name="Build">
                <CallTarget Targets="Build" />
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4006");
        exception.Message.ShouldContain("Build");
    }

    [Fact]
    public void AfterTargetCycleDoesNotCreateCircularDependency()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build" AfterTargets="AfterBuild" />
              <Target Name="AfterBuild" AfterTargets="Build" />
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
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
    public void RejectsOutputFromDeclaredIOTask()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build">
                <Generate DeclaredInputs=""
                          DeclaredOutputs="output.txt">
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </Generate>
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["Generate"] = new HardenedTaskDescriptor(
                    HardenedTaskClassification.DeclaredIO,
                    inputPathParameters: ["DeclaredInputs"],
                    outputPathParameters: ["DeclaredOutputs"]),
            });

        InvalidProjectFileException exception = validator.Validate(project, "Build").ShouldHaveSingleItem();

        exception.ErrorCode.ShouldBe("MSB4286");
        exception.Message.ShouldContain("Output element");
        exception.Message.ShouldContain("Declared-IO task 'Generate'");
    }

    [Fact]
    public void RecordsWriteCodeFragmentFootprintWithoutExecutingTask()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder projectFolder = environment.CreateFolder(createFolder: true);
        string generatedFile = Path.Combine(projectFolder.Path, "obj", "GeneratedAssemblyInfo.cs");
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            projectFolder,
            """
            <Project>
              <PropertyGroup>
                <GeneratedFile>$(MSBuildProjectDirectory)/obj/GeneratedAssemblyInfo.cs</GeneratedFile>
              </PropertyGroup>
              <Target Name="Build">
                <WriteCodeFragment OutputFile="$(GeneratedFile)"
                                   DeclaredInputs=""
                                   DeclaredOutputs="$(GeneratedFile)" />
                <ItemGroup>
                  <Compile Include="$(GeneratedFile)" />
                  <FileWrites Include="$(GeneratedFile)" />
                </ItemGroup>
                <ZeroIOConsume DeclaredInputs="" DeclaredOutputs="" Input="@(Compile);@(FileWrites)" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["WriteCodeFragment"] = s_writeCodeFragmentDescriptor,
                ["ZeroIOConsume"] = HardenedTaskDescriptor.DeclaredIO,
            });

        validator.Validate(project, "Build").ShouldBeEmpty();

        File.Exists(generatedFile).ShouldBeFalse();
        Lookup validationLookup = validator.GetValidationLookupForTesting();
        DescribeItemSpecs(validationLookup.GetItems("Compile"))
            .ShouldBe([generatedFile.Replace('\\', '/')]);
        DescribeItemSpecs(validationLookup.GetItems("FileWrites"))
            .ShouldBe([generatedFile.Replace('\\', '/')]);
        HardenedDeclaredIOFootprint footprint =
            validator.GetDeclaredIOFootprintsForTesting().Where(footprint => footprint.InputPaths.Count != 0 || footprint.OutputPaths.Count != 0).ShouldHaveSingleItem();
        footprint.InputPaths.ShouldBeEmpty();
        footprint.OutputPaths.ShouldBe([FileUtilities.NormalizePath(generatedFile)]);
    }

    [Fact]
    public void ExplicitDeclaredListsMayContainTheSameOperationalPath()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder projectFolder = environment.CreateFolder(createFolder: true);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            projectFolder,
            """
            <Project>
              <Target Name="Build">
                <Consume File="obj/generated.txt"
                         DeclaredInputs="obj/generated.txt"
                         DeclaredOutputs="obj/generated.txt" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["Consume"] = new HardenedTaskDescriptor(
                    HardenedTaskClassification.DeclaredIO,
                    inputPathParameters: ["DeclaredInputs"],
                    outputPathParameters: ["DeclaredOutputs"]),
            });

        validator.Validate(project, "Build").ShouldBeEmpty();

        string path = FileUtilities.NormalizePath(projectFolder.Path, "obj/generated.txt");
        HardenedDeclaredIOFootprint footprint =
            validator.GetDeclaredIOFootprintsForTesting().ShouldHaveSingleItem();
        footprint.InputPaths.ShouldBe([path]);
        footprint.OutputPaths.ShouldBe([path]);
    }

    [Fact]
    public void ExplicitDeclaredListsMayBeEmpty()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder projectFolder = environment.CreateFolder(createFolder: true);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            projectFolder,
            """
            <Project>
              <Target Name="Build">
                <Consume File="obj/generated.txt"
                         DeclaredInputs=""
                         DeclaredOutputs="obj/generated.txt" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["Consume"] = new HardenedTaskDescriptor(
                    HardenedTaskClassification.DeclaredIO,
                    inputPathParameters: ["DeclaredInputs"],
                    outputPathParameters: ["DeclaredOutputs"]),
            });

        validator.Validate(project, "Build").ShouldBeEmpty();

        HardenedDeclaredIOFootprint footprint =
            validator.GetDeclaredIOFootprintsForTesting().ShouldHaveSingleItem();
        footprint.InputPaths.ShouldBeEmpty();
        footprint.OutputPaths.ShouldBe(
            [FileUtilities.NormalizePath(projectFolder.Path, "obj/generated.txt")]);
    }

    [Theory]
    [InlineData("DeclaredOutputs=\"obj/generated.txt\"", "DeclaredInputs")]
    [InlineData("DeclaredInputs=\"obj/generated.txt\"", "DeclaredOutputs")]
    public void RejectsMissingDeclaredList(string suppliedDeclaration, string missingParameter)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            $"""
            <Project>
              <Target Name="Build">
                <Consume File="obj/generated.txt"
                         {suppliedDeclaration} />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["Consume"] = new HardenedTaskDescriptor(
                    HardenedTaskClassification.DeclaredIO,
                    inputPathParameters: ["DeclaredInputs"],
                    outputPathParameters: ["DeclaredOutputs"]),
            });

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4286");
        diagnostics[0].Message.ShouldContain($"a missing '{missingParameter}' parameter");
        validator.GetDeclaredIOFootprintsForTesting().ShouldBeEmpty();
    }

    [Fact]
    public void DeclaredInputListMayUseZeroIOTaskOutput()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder projectFolder = environment.CreateFolder(createFolder: true);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            projectFolder,
            """
            <Project>
              <Target Name="Build">
                <ZeroIOGenerate DeclaredInputs="" DeclaredOutputs="">
                  <Output TaskParameter="Result" PropertyName="GeneratedInput" />
                </ZeroIOGenerate>
                <Consume File="obj/generated.txt"
                         DeclaredInputs="$(GeneratedInput)"
                         DeclaredOutputs="obj/generated.txt" />
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["ZeroIOGenerate"] = HardenedTaskDescriptor.DeclaredIO,
                ["Consume"] = new HardenedTaskDescriptor(
                    HardenedTaskClassification.DeclaredIO,
                    inputPathParameters: ["DeclaredInputs"],
                    outputPathParameters: ["DeclaredOutputs"]),
            },
            (target, task, taskLookup) =>
            {
                if (task.Name == "ZeroIOGenerate")
                {
                    taskLookup.SetProperty(
                        ProjectPropertyInstance.Create("GeneratedInput", "obj/generated.txt"));
                }
            });

        validator.Validate(project, lookup, ["Build"]).ShouldBeEmpty();

        string path = FileUtilities.NormalizePath(projectFolder.Path, "obj/generated.txt");
        HardenedDeclaredIOFootprint footprint =
            validator.GetDeclaredIOFootprintsForTesting().Where(footprint => footprint.InputPaths.Count != 0 || footprint.OutputPaths.Count != 0).ShouldHaveSingleItem();
        footprint.InputPaths.ShouldBe([path]);
        footprint.OutputPaths.ShouldBe([path]);
    }

    [Fact]
    public void CanonicalizesRelativeWriteCodeFragmentOutput()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder projectFolder = environment.CreateFolder(createFolder: true);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            projectFolder,
            """
            <Project>
              <ItemGroup>
                <AssemblyAttribute Include="System.Reflection.AssemblyVersionAttribute" />
              </ItemGroup>
              <Target Name="Build">
                <WriteCodeFragment AssemblyAttributes="@(AssemblyAttribute)"
                                   OutputFile="obj/generated/AssemblyInfo.cs"
                                   DeclaredInputs=""
                                   DeclaredOutputs="obj/generated/AssemblyInfo.cs" />
                <ItemGroup>
                  <Compile Include="obj/generated/AssemblyInfo.cs" />
                </ItemGroup>
                <ZeroIOConsume DeclaredInputs="" DeclaredOutputs="" Input="@(Compile)" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["WriteCodeFragment"] = s_writeCodeFragmentDescriptor,
                ["ZeroIOConsume"] = HardenedTaskDescriptor.DeclaredIO,
            });

        validator.Validate(project, "Build").ShouldBeEmpty();

        DescribeItemSpecs(validator.GetValidationLookupForTesting().GetItems("Compile"))
            .ShouldBe(["obj/generated/AssemblyInfo.cs"]);
        validator.GetDeclaredIOFootprintsForTesting().Where(footprint => footprint.InputPaths.Count != 0 || footprint.OutputPaths.Count != 0)
            .ShouldHaveSingleItem()
            .OutputPaths.ShouldBe(
                [FileUtilities.NormalizePath(projectFolder.Path, "obj/generated/AssemblyInfo.cs")]);
    }

    [Fact]
    public void RejectsWriteCodeFragmentOutputDirectory()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <ItemGroup>
                <AssemblyAttribute Include="System.Reflection.AssemblyVersionAttribute" />
              </ItemGroup>
              <Target Name="Build">
                <WriteCodeFragment AssemblyAttributes="@(AssemblyAttribute)"
                                   OutputDirectory="obj/generated"
                                   OutputFile="AssemblyInfo.cs"
                                   DeclaredInputs=""
                                   DeclaredOutputs="obj/generated/AssemblyInfo.cs" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["WriteCodeFragment"] = s_writeCodeFragmentDescriptor,
            });

        IReadOnlyList<InvalidProjectFileException> diagnostics = validator.Validate(project, "Build");

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4286");
        diagnostics[0].Message.ShouldContain("OutputDirectory");
    }

    [Fact]
    public void AllowsDeclaredIOInvocationWithEmptyOutputList()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <ItemGroup>
                <AssemblyAttribute Include="System.Reflection.AssemblyVersionAttribute" />
              </ItemGroup>
              <Target Name="Build">
                <WriteCodeFragment AssemblyAttributes="@(AssemblyAttribute)"
                                   OutputFile=""
                                   DeclaredInputs=""
                                   DeclaredOutputs="" />
              </Target>
            </Project>
            """);
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskDescriptor>
            {
                ["WriteCodeFragment"] = s_writeCodeFragmentDescriptor,
            });

        validator.Validate(project, "Build").ShouldBeEmpty();
        validator.GetDeclaredIOFootprintsForTesting()
            .ShouldHaveSingleItem()
            .OutputPaths.ShouldBeEmpty();
        validator.GetValidationLookupForTesting().GetItems("Compile").ShouldBeEmpty();
    }

    [Fact]
    public void EmptyItemOperationAllowsMetadataConditionSyntax()
    {
        ValidateSuccess(
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <PackageReference Publish="false"
                                    Condition="('%(PackageReference.PrivateAssets)' == 'All') And ('%(PackageReference.Publish)' == '')" />
                </ItemGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
    }

    [Fact]
    public void NonemptyItemSourceRejectsProhibitedPerItemCondition()
    {
        InvalidProjectFileException exception = ValidateFailure(
            """
            <Project>
              <ItemGroup>
                <ProjectReference Include="referenced.proj" />
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup>
                  <Existent Include="@(ProjectReference)" Condition="Exists('%(Identity)')" />
                </ItemGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());

        exception.ErrorCode.ShouldBe("MSB4287");
        exception.Message.ShouldContain("Exists");
    }

    [Theory]
    [InlineData("false And Exists('input.txt')")]
    [InlineData("true Or Exists('input.txt')")]
    [InlineData("false And (1 &lt; 'not-a-number')")]
    public void ShortCircuitSkipsProhibitedOrErroringConditionBranch(string condition)
    {
        ValidateSuccess(
            $$"""
            <Project>
              <Target Name="Build">
                <PropertyGroup Condition="{{condition}}">
                  <Observed>static</Observed>
                </PropertyGroup>
              </Target>
            </Project>
            """,
            new Dictionary<string, HardenedTaskClassification>());
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
    public void ExecutedZeroIOTaskOutputIsAvailableToLaterZeroIOTask()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <Target Name="Build">
                <ZeroIOGenerate DeclaredInputs="" DeclaredOutputs="">
                  <Output TaskParameter="Result" PropertyName="Generated" />
                </ZeroIOGenerate>
                <ZeroIOConsume DeclaredInputs="" DeclaredOutputs="" Input="$(Generated)" />
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        int executionCount = 0;
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskClassification>
            {
                ["ZeroIOGenerate"] = HardenedTaskClassification.DeclaredIO,
                ["ZeroIOConsume"] = HardenedTaskClassification.DeclaredIO,
            },
            (target, task, taskLookup) =>
            {
                executionCount++;
                if (task.Name == "ZeroIOGenerate")
                {
                    taskLookup.SetProperty(ProjectPropertyInstance.Create("Generated", "concrete"));
                }
            });

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            validator.Validate(project, lookup, ["Build"]);

        diagnostics.ShouldBeEmpty();
        executionCount.ShouldBe(2);
    }

    [Fact]
    public void ZeroIOTaskWithDeferredInputDoesNotExecuteDuringValidation()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(
            environment,
            """
            <Project>
              <ItemGroup>
                <Input Include="a" />
              </ItemGroup>
              <Target Name="Build">
                <ZeroIO DeclaredInputs="" DeclaredOutputs="" Input="@(Input)" />
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        lookup.EnableHardenedState().AddTaskOutputItems(
            "Input",
            ValueState.Deferred(new ValueOrigin("deferred input membership")));
        int executionCount = 0;
        HardenedTargetValidator validator = new(
            new Dictionary<string, HardenedTaskClassification>
            {
                ["ZeroIO"] = HardenedTaskClassification.DeclaredIO,
            },
            (target, task, taskLookup) => executionCount++);

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            validator.Validate(project, lookup, ["Build"]);

        diagnostics.ShouldNotBeEmpty();
        executionCount.ShouldBe(0);
    }

    [Fact]
    public void RejectsUnauditedTelemetryTaskWithoutSpecialCasing()
    {
        InvalidProjectFileException exception = ValidateFailure(
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

        exception.ErrorCode.ShouldBe("MSB4286");
        exception.Message.ShouldContain("AllowEmptyTelemetry");
    }

    [Fact]
    public void IntrinsicBucketFormationOrderAndConditionsMatchOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <ItemGroup>
                <Input Include="a">
                  <Kind>z</Kind>
                  <Emit>true</Emit>
                </Input>
                <Input Include="b">
                  <Kind>a</Kind>
                  <Emit>false</Emit>
                </Input>
                <Input Include="c">
                  <Kind>z</Kind>
                  <Emit>true</Emit>
                </Input>
                <Input Include="d">
                  <Kind>m</Kind>
                  <Emit>true</Emit>
                </Input>
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup>
                  <Observed Include="@(Input->'%(Kind):%(Identity)', ',')"
                            Condition="'%(Input.Emit)' == 'true' And '%(Input.Kind)' != ''" />
                </ItemGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        DescribeItemSpecs(validationLookup.GetItems("Observed"))
            .ShouldBe(DescribeItemSpecs(ordinaryProject.GetItems("Observed")));
        DescribeItemSpecs(validationLookup.GetItems("Observed")).ShouldBe(
        [
            "z:a,z:c",
            "m:d",
        ]);
    }

    [Fact]
    public void HardenedGlobExecutionUsesGraphConstructionSnapshot()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder workspace = environment.CreateFolder(createFolder: true);
        string nestedDirectory = Path.Combine(workspace.Path, "files", "nested");
        Directory.CreateDirectory(nestedDirectory);
        string originalFile = Path.Combine(nestedDirectory, "before.txt");
        string lateFile = Path.Combine(nestedDirectory, "after.txt");
        File.WriteAllText(originalFile, string.Empty);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            workspace,
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <Observed Include="files/**/*.txt" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        var plan = new HardenedItemOperationPlan(workspace.Path);
        HardenedTargetValidator validator = new();

        validator.Validate(project, lookup, ["Build"], plan).ShouldBeEmpty();

        File.Delete(originalFile);
        File.WriteAllText(lateFile, string.Empty);

        ProjectTargetInstance target = project.Targets["Build"];
        ProjectItemGroupTaskInstance itemGroup = target.Children
            .OfType<ProjectItemGroupTaskInstance>()
            .Single();
        lookup.ConfigureHardenedItemOperationPlan(plan);
        lookup.EnterHardenedBucket(target, sequenceNumber: 0);
        new ItemGroupIntrinsicTask(
            itemGroup,
            CreateTargetLoggingContext(),
            project,
            logTaskInputs: false).ExecuteTask(lookup);

        DescribeGlobItems(lookup.GetItems("Observed")).ShouldBe(
        [
            "files/nested/before.txt||nested/",
        ]);
    }

    [Fact]
    public void HardenedGlobExecutionRejectsMissingPreResolution()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder workspace = environment.CreateFolder(createFolder: true);
        Directory.CreateDirectory(Path.Combine(workspace.Path, "files"));
        File.WriteAllText(Path.Combine(workspace.Path, "files", "input.txt"), string.Empty);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            workspace,
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <Observed Include="files/*.txt" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        ProjectTargetInstance target = project.Targets["Build"];
        ProjectItemGroupTaskInstance itemGroup = target.Children
            .OfType<ProjectItemGroupTaskInstance>()
            .Single();
        lookup.ConfigureHardenedItemOperationPlan(
            new HardenedItemOperationPlan(workspace.Path));
        lookup.EnterHardenedBucket(target, sequenceNumber: 0);

        InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(
            () => new ItemGroupIntrinsicTask(
                itemGroup,
                CreateTargetLoggingContext(),
                project,
                logTaskInputs: false).ExecuteTask(lookup));

        exception.ErrorCode.ShouldBe("MSB4289");
    }

    [Fact]
    public void HardenedGlobRejectsWorkspaceEscape()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder parent = environment.CreateFolder(createFolder: true);
        string workspace = Path.Combine(parent.Path, "workspace");
        string outside = Path.Combine(parent.Path, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "input.txt"), string.Empty);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            workspace,
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <Observed Include="../outside/*.txt" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        var plan = new HardenedItemOperationPlan(workspace);

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            new HardenedTargetValidator().Validate(project, lookup, ["Build"], plan);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4290");
    }

    [RequiresSymbolicLinksFact]
    public void HardenedGlobRejectsSymlinkEscape()
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        TransientTestFolder parent = environment.CreateFolder(createFolder: true);
        string workspace = Path.Combine(parent.Path, "workspace");
        string outside = Path.Combine(parent.Path, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "input.txt"), string.Empty);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "linked"), outside);
        ProjectInstance project = CreateProjectInstanceFromFile(
            environment,
            workspace,
            """
            <Project>
              <Target Name="Build">
                <ItemGroup>
                  <Observed Include="linked/*.txt" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        var plan = new HardenedItemOperationPlan(workspace);

        IReadOnlyList<InvalidProjectFileException> diagnostics =
            new HardenedTargetValidator().Validate(project, lookup, ["Build"], plan);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].ErrorCode.ShouldBe("MSB4290");
    }

    [Fact]
    public void PropertyBucketDiscoveryOrderMatchesOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <ItemGroup>
                <A Include="a">
                  <M>1</M>
                </A>
                <B Include="b">
                  <M>2</M>
                </B>
              </ItemGroup>
              <Target Name="Build">
                <PropertyGroup>
                  <Chosen Condition="'%(B.M)' != '' Or '%(A.M)' != ''">%(A.M)x%(B.M)</Chosen>
                </PropertyGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        validationLookup.GetProperty("Chosen")!.EvaluatedValue
            .ShouldBe(ordinaryProject.GetPropertyValue("Chosen"));
        ordinaryProject.GetPropertyValue("Chosen").ShouldBe("x2");
    }

    [Fact]
    public void FalseItemGroupConditionMatchesOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <PropertyGroup>
                <Enable>false</Enable>
              </PropertyGroup>
              <ItemGroup>
                <Input Include="a;b" />
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup Condition="'$(Enable)' == 'true'">
                  <Input Remove="b" />
                </ItemGroup>
                <ItemGroup>
                  <Observed Include="@(Input)" />
                </ItemGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);

        (ProjectInstance ordinaryProject, Lookup validationLookup) = BuildOrdinaryAndValidateHardened(
            environment,
            projectXml,
            new Dictionary<string, HardenedTaskClassification>());

        DescribeItemSpecs(validationLookup.GetItems("Input"))
            .ShouldBe(DescribeItemSpecs(ordinaryProject.GetItems("Input")));
        DescribeItemSpecs(validationLookup.GetItems("Observed"))
            .ShouldBe(DescribeItemSpecs(ordinaryProject.GetItems("Observed")));
        DescribeItemSpecs(validationLookup.GetItems("Observed")).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void ConstructiveItemOperationsMatchOrdinaryBuild()
    {
        const string projectXml = """
            <Project>
              <PropertyGroup>
                <OutputRoot>obj/</OutputRoot>
              </PropertyGroup>
              <ItemGroup>
                <Source Include="a.cs">
                  <Kind>keep</Kind>
                  <Order>1</Order>
                </Source>
                <Source Include="b.cs">
                  <Kind>drop</Kind>
                  <Order>2</Order>
                </Source>
                <Source Include="c.cs">
                  <Kind>keep</Kind>
                  <Order>3</Order>
                </Source>
                <Source Include="d.cs">
                  <Kind>keep</Kind>
                  <Order>4</Order>
                </Source>
              </ItemGroup>
              <Target Name="Build">
                <ItemGroup>
                  <Selected Include="@(Source->'$(OutputRoot)%(Filename).out')"
                            Condition="'%(Source.Kind)' == 'keep'"
                            KeepDuplicates="true">
                    <SourceOrder>%(Source.Order)</SourceOrder>
                  </Selected>
                  <Selected Remove="$(OutputRoot)a.out" />
                  <Selected Condition="'%(Selected.SourceOrder)' == '3'">
                    <State>updated</State>
                  </Selected>
                  <Final Include="@(Selected)" KeepDuplicates="true" />
                </ItemGroup>
              </Target>
            </Project>
            """;
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance ordinaryProject = BuildOrdinaryProject(
            CreateProjectInstance(environment, projectXml));
        ProjectInstance validationProject = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new();

        validator.Validate(validationProject, "Build").ShouldBeEmpty();

        Lookup validationLookup = validator.GetValidationLookupForTesting();
        DescribeItems(validationLookup.GetItems("Selected"))
            .ShouldBe(DescribeItems(ordinaryProject.GetItems("Selected")));
        DescribeItems(validationLookup.GetItems("Final"))
            .ShouldBe(DescribeItems(ordinaryProject.GetItems("Final")));
        DescribeItems(validationLookup.GetItems("Final")).ShouldBe(
        [
            "obj/c.out|3|updated",
            "obj/d.out|4|",
        ]);
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
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications,
        Action<Lookup>? configureLookup = null)
    {
        using TestEnvironment environment = TestEnvironment.Create(_output);
        ProjectInstance project = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        if (configureLookup is null)
        {
            return validator.Validate(project, "Build");
        }

        var lookup = new Lookup(project.ItemsToBuildWith, project.PropertiesToBuildWith);
        configureLookup(lookup);

        return validator.Validate(project, lookup, ["Build"]);
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

    private static ProjectInstance CreateProjectInstanceFromFile(
        TestEnvironment environment,
        TransientTestFolder folder,
        string projectXml)
        => CreateProjectInstanceFromFile(environment, folder.Path, projectXml);

    private static ProjectInstance CreateProjectInstanceFromFile(
        TestEnvironment environment,
        string folder,
        string projectXml)
    {
        string projectPath = Path.Combine(folder, "test.proj");
        File.WriteAllText(projectPath, projectXml.Cleanup());
        var projectCollection = environment.CreateProjectCollection();
        return ProjectInstance.FromFile(
            projectPath,
            new ProjectOptions { ProjectCollection = projectCollection.Collection });
    }

    private ProjectInstance BuildOrdinaryProject(ProjectInstance project)
    {
        using BuildManager buildManager = new();
        BuildResult result = buildManager.Build(
            new BuildParameters
            {
                EnableNodeReuse = false,
                Loggers = [new MockLogger(_output)],
                MaxNodeCount = 1,
            },
            new BuildRequestData(
                project,
                ["Build"],
                hostServices: null,
                BuildRequestDataFlags.ProvideProjectStateAfterBuild));

        result.ShouldHaveSucceeded();
        result.ProjectStateAfterBuild.ShouldNotBeNull();
        return result.ProjectStateAfterBuild;
    }

    private static TargetLoggingContext CreateTargetLoggingContext()
        => new(
            new MockLoggingService(),
            new BuildEventContext(
                nodeId: 1,
                projectContextId: 2,
                targetId: 3,
                taskId: 4));

    private (ProjectInstance OrdinaryProject, Lookup ValidationLookup) BuildOrdinaryAndValidateHardened(
        TestEnvironment environment,
        string projectXml,
        IReadOnlyDictionary<string, HardenedTaskClassification> taskClassifications)
    {
        ProjectInstance ordinaryProject = BuildOrdinaryProject(
            CreateProjectInstance(environment, projectXml));
        ProjectInstance validationProject = CreateProjectInstance(environment, projectXml);
        HardenedTargetValidator validator = new(taskClassifications);

        validator.Validate(validationProject, "Build").ShouldBeEmpty();

        return (ordinaryProject, validator.GetValidationLookupForTesting());
    }

    private static string[] DescribeItemSpecs(IEnumerable<ProjectItemInstance> items)
        => [.. items.Select(item => item.EvaluatedInclude.Replace('\\', '/'))];

    private static string[] DescribeItems(IEnumerable<ProjectItemInstance> items)
        => [.. items.Select(
            item =>
                $"{item.EvaluatedInclude.Replace('\\', '/')}|" +
                $"{item.GetMetadataValue("SourceOrder")}|" +
                item.GetMetadataValue("State"))];

    private static string[] DescribeGlobItems(IEnumerable<ProjectItemInstance> items)
        => [.. items.Select(
            item =>
                $"{item.EvaluatedInclude.Replace('\\', '/')}|" +
                $"{item.GetMetadataValue("SourceOrder")}|" +
                item.GetMetadataValue("RecursiveDir").Replace('\\', '/'))];
}
