// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ActionTaskHost.UnitTests;

public sealed class TaskHost_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void ExecutesWriteLinesToFileFromInvocationFile()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "action host $ files").Path;
        string destination = Path.Combine(directory, "result %3B.txt");
        string manifest = WriteInvocation(directory, typeof(WriteLinesToFile), new()
        {
            ["File"] = destination,
            ["Lines"] = new[] { "first;line", "literal%3Bvalue", "last\\line" },
            ["Overwrite"] = "yes",
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { destination },
        });

        bool succeeded = Run(manifest, out _);

        succeeded.ShouldBeTrue();
        File.ReadAllLines(destination).ShouldBe(["first;line", "literal%3Bvalue", "last\\line"]);
    }

    [Fact]
    public void PreservesTypedValuesItemsMetadataAndExplicitEnvironmentWithoutEngine()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("ACTION_HOST_AMBIENT", "must-not-leak");
        string directory = env.CreateFolder().Path;
        string destination = Path.Combine(directory, "bound.json");
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), new()
        {
            ["File"] = "bound.json",
            ["Value"] = "",
            ["Values"] = Array.Empty<string>(),
            ["Enabled"] = "on",
            ["Count"] = 42,
            ["Mode"] = "second",
            ["Items"] = new object[]
            {
                new { itemSpec = "one%3B;$(literal)\\value", metadata = new { Tag = "tag%25;value" } },
                "two",
            },
            ["Forbidden"] = "",
            ["DeclaredInputs"] = Array.Empty<string>(),
            ["DeclaredOutputs"] = new[] { "bound.json" },
        }, new() { ["ACTION_HOST_EXPLICIT"] = "present" });

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(destination));
        JsonElement root = result.RootElement;
        root.GetProperty("value").GetString().ShouldBe("");
        root.GetProperty("values").GetArrayLength().ShouldBe(0);
        root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        root.GetProperty("count").GetInt32().ShouldBe(42);
        root.GetProperty("mode").GetString().ShouldBe("Second");
        root.GetProperty("items")[0].GetProperty("itemSpec").GetString().ShouldBe("one%3B;$(literal)\\value");
        root.GetProperty("items")[0].GetProperty("tag").GetString().ShouldBe("tag%25;value");
        root.GetProperty("items")[1].GetProperty("itemSpec").GetString().ShouldBe("two");
        root.GetProperty("workingDirectory").GetString().ShouldBe(directory);
        root.GetProperty("explicitEnvironment").GetString().ShouldBe("present");
        root.GetProperty("ambientEnvironment").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("engineLoaded").GetBoolean().ShouldBeFalse();
        root.GetProperty("processId").GetInt32().ShouldNotBe(Environment.ProcessId);
    }

    [Fact]
    public void OmittedParametersKeepTaskDefaults()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters());

        Run(manifest, out _).ShouldBeTrue();

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe.json")));
        result.RootElement.GetProperty("value").GetString().ShouldBe("default");
        result.RootElement.GetProperty("values")[0].GetString().ShouldBe("default");
    }

    [Theory]
    [InlineData("Fail")]
    [InlineData("LogError")]
    [InlineData("Throw")]
    [InlineData("NestedBuild")]
    [InlineData("ProduceOutput")]
    public void TaskFailuresReturnUnsuccessfulExit(string failure)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = ProbeParameters();
        parameters[failure] = failure != "ProduceOutput";
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldNotBeNullOrWhiteSpace();
        File.Exists(Path.Combine(directory, "probe.json")).ShouldBe(failure is "Fail" or "LogError");
        if (failure == "NestedBuild")
        {
            log.ShouldContain("BuildProjectFile");
        }
        else if (failure == "Throw")
        {
            log.ShouldContain("probe-exception");
        }
        else if (failure == "LogError")
        {
            log.ShouldContain("probe-error");
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Required")]
    [InlineData("Duplicate")]
    [InlineData("Null")]
    [InlineData("Array")]
    [InlineData("HostService")]
    [InlineData("Declarations")]
    [InlineData("InPlace")]
    [InlineData("Unset")]
    [InlineData("EmptyUnsupportedArray")]
    [InlineData("NullMetadata")]
    [InlineData("MissingInput")]
    [InlineData("NoOutputs")]
    [InlineData("OverlappingOutputs")]
    public void InvalidParametersFailBeforeTaskExecution(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        Dictionary<string, object?> parameters = ProbeParameters();
        switch (invalid)
        {
            case "Unknown":
                parameters["NoSuchParameter"] = "value";
                break;
            case "Required":
                parameters.Remove("File");
                break;
            case "Duplicate":
                parameters["file"] = "other.json";
                break;
            case "Null":
                parameters["Value"] = null;
                break;
            case "Array":
                parameters["Values"] = "not-an-array";
                break;
            case "HostService":
                parameters["BuildEngine"] = "not-a-service";
                break;
            case "Declarations":
                parameters.Remove("DeclaredInputs");
                break;
            case "InPlace":
                env.CreateFile(Path.Combine(directory, "input.txt"), "original");
                parameters["DeclaredInputs"] = new[] { "input.txt" };
                parameters["DeclaredOutputs"] = new[] { "input.txt" };
                break;
            case "Unset":
                parameters["Forbidden"] = "value";
                break;
            case "EmptyUnsupportedArray":
                parameters["Unsupported"] = Array.Empty<string>();
                break;
            case "NullMetadata":
                parameters["Items"] = new object[]
                {
                    new { itemSpec = "input", metadata = new Dictionary<string, string?> { ["Tag"] = null } },
                };
                break;
            case "MissingInput":
                parameters["DeclaredInputs"] = new[] { "absent.txt" };
                break;
            case "NoOutputs":
                parameters["DeclaredOutputs"] = Array.Empty<string>();
                break;
            case "OverlappingOutputs":
                parameters["DeclaredOutputs"] = new[] { "probe.json", Path.Combine("probe.json", "nested.txt") };
                break;
        }

        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), parameters);

        Run(manifest, out _).ShouldBeFalse();

        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        if (invalid == "InPlace")
        {
            File.ReadAllText(Path.Combine(directory, "input.txt")).ShouldBe("original");
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\": 987}")]
    [InlineData("{\"version\": 1, \"version\": 1}")]
    public void RejectsInvalidInvocationFiles(string contents)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string manifest = env.CreateFile(".json", contents).Path;

        Run(manifest, out string log).ShouldBeFalse();

        log.ShouldContain(manifest);
    }

    [Theory]
    [InlineData("Version", "987")]
    [InlineData("UnknownField", "unknownField")]
    [InlineData("NullEnvironment", "ACTION_HOST_EXPLICIT")]
    public void RejectsInvalidInvocationContext(string invalid, string expectedDiagnostic)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string manifest = WriteInvocation(directory, typeof(ManifestProbeTask), ProbeParameters());
        JsonNode invocation = JsonNode.Parse(File.ReadAllText(manifest))!;
        switch (invalid)
        {
            case "Version":
                invocation["version"] = 987;
                break;
            case "UnknownField":
                invocation["unknownField"] = "value";
                break;
            case "NullEnvironment":
                invocation["environment"] = new JsonObject { ["ACTION_HOST_EXPLICIT"] = null };
                break;
        }

        File.WriteAllText(manifest, invocation.ToJsonString());

        Run(manifest, out string log).ShouldBeFalse();

        File.Exists(Path.Combine(directory, "probe.json")).ShouldBeFalse();
        log.ShouldContain(expectedDiagnostic);
    }

    private static Dictionary<string, object?> ProbeParameters() => new()
    {
        ["File"] = "probe.json",
        ["DeclaredInputs"] = Array.Empty<string>(),
        ["DeclaredOutputs"] = new[] { "probe.json" },
    };

    private static string WriteInvocation(
        string directory,
        Type taskType,
        Dictionary<string, object?> parameters,
        Dictionary<string, string>? environment = null)
    {
        string path = Path.Combine(directory, "invocation.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            taskAssembly = taskType.Assembly.Location,
            taskType = taskType.FullName,
            workingDirectory = directory,
            projectFile = Path.Combine(directory, "build.proj"),
            environment = environment ?? [],
            parameters,
        }));
        return path;
    }

    private bool Run(string manifest, out string log)
    {
        string host = Path.Combine(AppContext.BaseDirectory, "MSBuild.ActionTaskHost.dll");
        string tests = typeof(TaskHost_Tests).Assembly.Location;
        string arguments = $"exec --runtimeconfig \"{Path.ChangeExtension(tests, ".runtimeconfig.json")}\""
            + $" --depsfile \"{Path.ChangeExtension(tests, ".deps.json")}\" \"{host}\" --invocation \"{manifest}\"";
        log = RunnerUtilities.RunProcessAndGetOutput(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            arguments,
            out bool succeeded,
            outputHelper: _output,
            timeoutMilliseconds: 30_000);
        return succeeded;
    }
}

public enum ProbeMode
{
    First,
    Second,
}

[MSBuildDeclaredIOTask]
[MSBuildDeclaredIORequiresUnset(nameof(Forbidden))]
public sealed class ManifestProbeTask : Utilities.Task
{
    [Required]
    public ITaskItem? File { get; set; }

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public string Value { get; set; } = "default";

    public string[] Values { get; set; } = ["default"];

    public bool Enabled { get; set; }

    public int Count { get; set; }

    public ProbeMode Mode { get; set; }

    public ITaskItem[] Items { get; set; } = [];

    public DateTimeOffset[] Unsupported { get; set; } = [];

    public string Forbidden { get; set; } = string.Empty;

    public bool Fail { get; set; }

    public bool LogError { get; set; }

    public bool Throw { get; set; }

    public bool NestedBuild { get; set; }

    public bool ProduceOutput { get; set; } = true;

    public override bool Execute()
    {
        ArgumentNullException.ThrowIfNull(File);
        if (Throw)
        {
            throw new InvalidOperationException("probe-exception");
        }

        if (NestedBuild)
        {
            return BuildEngine.BuildProjectFile("child.proj", [], new Hashtable(), new Hashtable());
        }

        if (ProduceOutput)
        {
            System.IO.File.WriteAllText(File.ItemSpec, JsonSerializer.Serialize(new
            {
                value = Value,
                values = Values,
                enabled = Enabled,
                count = Count,
                mode = Mode.ToString(),
                items = Items.Select(item => new { itemSpec = item.ItemSpec, tag = item.GetMetadata("Tag") }).ToArray(),
                workingDirectory = Environment.CurrentDirectory,
                explicitEnvironment = Environment.GetEnvironmentVariable("ACTION_HOST_EXPLICIT"),
                ambientEnvironment = Environment.GetEnvironmentVariable("ACTION_HOST_AMBIENT"),
                engineLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Microsoft.Build"),
                processId = Environment.ProcessId,
            }));
        }

        if (LogError)
        {
            Log.LogError("probe-error");
        }

        return !Fail;
    }
}
