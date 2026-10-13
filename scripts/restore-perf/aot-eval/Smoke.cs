// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Utilities;

internal static class Smoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            throw new ArgumentException("Pass <SDK directory> <existing scratch directory>.");
        }

        string sdkDirectory = Path.GetFullPath(args[0]);
        string scratch = Path.GetFullPath(args[1]);
        Environment.SetEnvironmentVariable("MSBUILD_RESTORE_EVAL_LIBRARY", Path.Combine(sdkDirectory, "RestoreAotEvaluation.so"));
        Environment.SetEnvironmentVariable("MSBUILD_RESTORE_EVAL_SDK_DIRECTORY", sdkDirectory);
        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", Path.Combine(sdkDirectory, "MSBuild.dll"));
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdkDirectory, "Sdks"));
        AppContext.SetData("Microsoft.DotNet.Sdk.Root", sdkDirectory);
        string projectFile = Path.Combine(scratch, "handoff.proj");
        File.WriteAllText(Path.Combine(scratch, "import.targets"), """
            <Project>
              <UsingTask TaskName="CaptureTask" AssemblyFile="$(TaskAssembly)" />
              <ItemDefinitionGroup><Probe><Inherited>definition</Inherited></Probe></ItemDefinitionGroup>
              <Target Name="First"><PropertyGroup><Order>first</Order></PropertyGroup></Target>
              <Target Name="Before" BeforeTargets="Run"><PropertyGroup><Order>$(Order)-before</Order></PropertyGroup></Target>
              <Target Name="After" AfterTargets="Run"><PropertyGroup><Order>$(Order)-after</Order></PropertyGroup></Target>
              <Target Name="Run">
                <CaptureTask Inputs="@(Probe)"><Output TaskParameter="Result" PropertyName="Captured" /></CaptureTask>
              </Target>
            </Project>
            """);
        File.WriteAllText(projectFile, """
            <Project DefaultTargets="Run" InitialTargets="First" TreatAsLocalProperty="Local">
              <PropertyGroup>
                <Local>local</Local>
                <Literal>a%3Bb%24%28Missing%29</Literal>
              </PropertyGroup>
              <Import Project="import.targets" />
              <ItemGroup><Probe Include="a%3Bb;literal%2A"><Text>$(Literal)</Text></Probe></ItemGroup>
            </Project>
            """);

        string[] targetFrameworks = ["net11.0", "net10.0"];
        foreach (string targetFramework in targetFrameworks)
        {
            using ProjectCollection collection = new();
            ProjectInstance project = ProjectInstance.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = collection,
                GlobalProperties = new Dictionary<string, string>
                {
                    ["TaskAssembly"] = typeof(CaptureTask).Assembly.Location,
                    ["TargetFramework"] = targetFramework,
                    ["Local"] = "global",
                },
            });
            Require(project.GetPropertyValue("TargetFramework") == targetFramework, "Global properties changed.");
            Require(project.GetPropertyValue("Local") == "local", "TreatAsLocalProperty changed.");
            Require(project.GetPropertyValue("Literal") == "a;b$(Missing)", "Property escaping changed.");
            Require(project.Items.Count == 2, "Item escaping changed.");
            foreach (ProjectItemInstance item in project.Items)
            {
                Require(item.GetMetadataValue("Text") == "a;b$(Missing)", "Metadata escaping changed.");
                Require(item.GetMetadataValue("Inherited") == "definition", "Item definitions changed.");
                Require(item.GetMetadataValue("FullPath") == Path.GetFullPath(item.EvaluatedInclude, scratch), "Item directory was lost before graph construction.");
                Require(item.GetMetadataValue("DefiningProjectFullPath") == projectFile, "Defining project location changed.");
            }

            Require(project.Build(["Run"], [new ConsoleLogger(LoggerVerbosity.Minimal)]), "Managed task execution failed.");
            Require(project.GetPropertyValue("Order") == "first-before-after", "Target ordering changed.");
            Require(project.GetPropertyValue("Captured") == "a;b|literal*", "UsingTask or item handoff changed.");
        }

        string stockSdkFile = Path.Combine(scratch, "sdk.csproj");
        File.WriteAllText(stockSdkFile, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>
            """);
        using (ProjectCollection collection = new())
        {
            ProjectInstance sdkProject = ProjectInstance.FromFile(stockSdkFile, new ProjectOptions
            {
                ProjectCollection = collection,
                GlobalProperties = new Dictionary<string, string>
                {
                    ["ImportDirectoryBuildProps"] = "false",
                    ["ImportDirectoryBuildTargets"] = "false",
                },
            });
            Require(sdkProject.GetPropertyValue("TargetFramework") == "net11.0", "Full SDK evaluation failed.");
            Require(sdkProject.Targets.ContainsKey("Restore"), "SDK targets were not transferred.");
        }

        Console.WriteLine($"PASS: {Environment.GetEnvironmentVariable("MSBUILD_RESTORE_EVAL_MODE") ?? "managed"} full evaluation and managed execution.");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed class CaptureTask : Microsoft.Build.Utilities.Task
{
    public ITaskItem[] Inputs { get; set; } = [];

    [Output]
    public string Result { get; set; } = "";

    public override bool Execute()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException("The native evaluator must never execute a task.");
        }

        string[] values = new string[Inputs.Length];
        for (int i = 0; i < Inputs.Length; i++)
        {
            values[i] = Inputs[i].ItemSpec;
        }

        Result = string.Join("|", values);
        return true;
    }
}
