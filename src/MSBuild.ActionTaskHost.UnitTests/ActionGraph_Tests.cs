// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ActionTaskHost.UnitTests;

public sealed class ActionGraph_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [UnixOnlyFact]
    public void EmitsWithoutExecutingActionsAndNinjaBuildsIncrementally()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "graph $ space:quote'").Path;
        string project = WriteProject(directory);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();

        string first = Path.Combine(directory, "first.txt");
        string second = Path.Combine(directory, "second.txt");
        File.Exists(first).ShouldBeFalse();
        File.Exists(second).ShouldBeFalse();
        string[] manifests = Directory.GetFiles(graph + ".actions", "*.json");
        manifests.Length.ShouldBe(2);
        using (JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(manifests[0])))
        {
            invocation.RootElement.GetProperty("taskType").GetString().ShouldBe(typeof(GraphTransformTask).FullName);
        }

        Ninja(graph, out bool success);
        success.ShouldBeTrue();
        File.ReadAllText(first).ShouldBe("literal%3B;value|metadata%25");
        File.ReadAllText(second).ShouldBe("literal%3B;value|metadata%25|consumed");
        DateTime firstWrite = File.GetLastWriteTimeUtc(first);
        DateTime secondWrite = File.GetLastWriteTimeUtc(second);
        DateTime manifestWrite = File.GetLastWriteTimeUtc(manifests[0]);
        DateTime graphWrite = File.GetLastWriteTimeUtc(graph);

        Generate(project, graph).ShouldBeTrue();
        File.GetLastWriteTimeUtc(graph).ShouldBe(graphWrite);
        File.GetLastWriteTimeUtc(manifests[0]).ShouldBe(manifestWrite);
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.GetLastWriteTimeUtc(first).ShouldBe(firstWrite);
        File.GetLastWriteTimeUtc(second).ShouldBe(secondWrite);

        File.WriteAllText(first, "changed");
        File.SetLastWriteTimeUtc(first, secondWrite.AddSeconds(2));
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.ReadAllText(second).ShouldBe("changed|consumed");
    }

    [UnixOnlyTheory]
    [InlineData("Unknown")]
    [InlineData("OutputMapping")]
    [InlineData("MissingDeclaration")]
    [InlineData("ConflictingOutputs")]
    [InlineData("Exists")]
    [InlineData("PropertyIO")]
    [InlineData("EvaluationIO")]
    [InlineData("ContinueOnError")]
    [InlineData("OnError")]
    public void RejectsUnsupportedGraphsWithoutPublishingOrExecutingActions(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = invalid switch
        {
            "Unknown" => contents.Replace("</Target>", "<Exec Command=\"touch forbidden.txt\" /></Target>"),
            "OutputMapping" => contents.Replace("DeclaredOutputs=\"first.txt\" />", "DeclaredOutputs=\"first.txt\"><Output TaskParameter=\"Text\" PropertyName=\"Bad\" /></GraphTransformTask>"),
            "MissingDeclaration" => contents.Replace("DeclaredOutputs=\"first.txt\"", ""),
            "ConflictingOutputs" => contents.Replace("DeclaredOutputs=\"second.txt\"", "DeclaredOutputs=\"first.txt\""),
            "Exists" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"Exists('first.txt')\""),
            "PropertyIO" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"$([System.IO.File]::Exists('first.txt'))\""),
            "EvaluationIO" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([System.IO.File]::WriteAllText('$(MSBuildProjectDirectory)/forbidden.txt', 'bad'))</Bad></PropertyGroup><Target Name=\"Build\""),
            "ContinueOnError" => contents.Replace("Text=\"@(Values)\"", "Text=\"@(Values)\" ContinueOnError=\"true\""),
            "OnError" => contents.Replace("</Target>", "<OnError ExecuteTargets=\"Failure\" /></Target><Target Name=\"Failure\" />"),
            _ => throw new InvalidOperationException(invalid),
        };
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void FailedActionBlocksItsConsumer()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("Text=\"@(Values)\"", "Text=\"@(Values)\" Fail=\"true\""));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool success);

        success.ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void CapturesEachTaskBucketAndInvalidatesOnManifestChanges()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project)
            .Replace("<Target Name=\"Build\"", "<ItemGroup><Batch Include=\"one;two\" /></ItemGroup><Target Name=\"Build\"")
            .Replace("File=\"first.txt\" Text=\"@(Values)\" DeclaredInputs=\"\" DeclaredOutputs=\"first.txt\"",
                "File=\"%(Batch.Identity).txt\" Text=\"@(Values)\" DeclaredInputs=\"\" DeclaredOutputs=\"%(Batch.Identity).txt\"")
            .Replace("<GraphTransformTask File=\"second.txt\" Source=\"first.txt\" DeclaredInputs=\"first.txt\" DeclaredOutputs=\"second.txt\" />", "");
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        Ninja(graph, out bool success);
        success.ShouldBeTrue();
        string first = Path.Combine(directory, "one.txt");
        string second = Path.Combine(directory, "two.txt");
        File.ReadAllText(first).ShouldBe("literal%3B;value|metadata%25");
        File.ReadAllText(second).ShouldBe("literal%3B;value|metadata%25");

        File.WriteAllText(project, contents.Replace("Text=\"@(Values)\"", "Text=\"replacement\""));
        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out success);
        success.ShouldBeTrue();
        File.ReadAllText(first).ShouldBe("replacement|");
        File.ReadAllText(second).ShouldBe("replacement|");
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructsActionsThroughTargetDependenciesAndCallTarget(bool callTarget)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        string producer = """
            <GraphValueTask DeclaredInputs="" DeclaredOutputs="">
              <Output TaskParameter="Values" ItemName="Values" />
            </GraphValueTask>
            <GraphTransformTask File="first.txt" Text="@(Values)" DeclaredInputs="" DeclaredOutputs="first.txt" />
            """;
        int start = contents.IndexOf("<GraphValueTask ", StringComparison.Ordinal);
        int end = contents.IndexOf("<GraphTransformTask File=\"second.txt\"", StringComparison.Ordinal);
        contents = contents.Remove(start, end - start);
        contents = contents.Replace("</Project>", $"<Target Name=\"Produce\">{producer}</Target></Project>");
        contents = callTarget
            ? contents.Replace("<GraphTransformTask File=\"second.txt\"", "<CallTarget Targets=\"Produce\" /><GraphTransformTask File=\"second.txt\"")
            : contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" DependsOnTargets=\"Produce\"");
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "second.txt")).ShouldBe("literal%3B;value|metadata%25|consumed");
    }

    private static string WriteProject(string directory)
    {
        string assembly = SecurityElement.Escape(typeof(GraphValueTask).Assembly.Location)!;
        string project = Path.Combine(directory, "build.proj");
        File.WriteAllText(project, $$"""
            <Project>
              <UsingTask TaskName="GraphValueTask" AssemblyFile="{{assembly}}" />
              <UsingTask TaskName="GraphTransformTask" AssemblyFile="{{assembly}}" />
              <Target Name="Build" Inputs="first.txt" Outputs="second.txt">
                <GraphValueTask DeclaredInputs="" DeclaredOutputs="">
                  <Output TaskParameter="Values" ItemName="Values" />
                </GraphValueTask>
                <GraphTransformTask File="first.txt" Text="@(Values)" DeclaredInputs="" DeclaredOutputs="first.txt" />
                <GraphTransformTask File="second.txt" Source="first.txt" DeclaredInputs="first.txt" DeclaredOutputs="second.txt" />
              </Target>
            </Project>
            """);
        return project;
    }

    private bool Generate(string project, string graph)
    {
        string testDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string framework = Path.GetFileName(testDirectory);
        string configuration = Path.GetFileName(Path.GetDirectoryName(testDirectory))!;
        string host = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..",
            "MSBuild.ActionTaskHost", configuration, framework, "MSBuild.ActionTaskHost.dll"));
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")!;
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" --hardened-graph -nr:false -v:minimal"
                + $" -p:MSBuildEmitActionGraph=\"{graph}\" -p:MSBuildActionTaskHost=\"{host}\""
                + $" -p:MSBuildActionDotNetHost=\"{dotnet}\"",
            out bool succeeded, outputHelper: _output);
        return succeeded;
    }

    private void Ninja(string graph, out bool success)
        => RunnerUtilities.RunProcessAndGetOutput("ninja", $"-f \"{graph}\" -j1", out success, outputHelper: _output);
}

[MSBuildDeclaredIOTask]
public sealed class GraphValueTask : Utilities.Task
{
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[] Values { get; private set; } = [];

    public override bool Execute()
    {
        Utilities.TaskItem value = new("literal%253B%3Bvalue", treatAsFilePath: false);
        ((ITaskItem2)value).SetMetadataValueLiteral("Tag", "metadata%25");
        Values = [value];
        return true;
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphTransformTask : Utilities.Task
{
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];
    public ITaskItem[] Text { get; set; } = [];
    public string Source { get; set; } = "";
    public bool Fail { get; set; }

    [Required]
    public string File { get; set; } = "";

    public override bool Execute()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "Microsoft.Build")
            {
                throw new InvalidOperationException("File actions must run without the engine.");
            }
        }

        if (Fail)
        {
            return false;
        }

        string text = Source.Length == 0
            ? Text[0].ItemSpec + "|" + Text[0].GetMetadata("Tag")
            : System.IO.File.ReadAllText(Source) + "|consumed";
        System.IO.File.WriteAllText(File, text);
        return true;
    }
}
