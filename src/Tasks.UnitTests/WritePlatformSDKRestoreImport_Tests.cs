// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Evaluation;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.Tasks.UnitTests;

public sealed class WritePlatformSDKRestoreImport_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("")]
    [InlineData("space & <xml> 'quote\";$(Injected);%3B")]
    public void PreservesLiteralDiscoveryFacts(string value)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string file = Path.Combine(env.CreateFolder().Path, "sdk.props");
        WritePlatformSDKRestoreImport task = CreateTask(file, value);

        task.Execute().ShouldBeTrue();

        using ProjectCollection projects = new();
        Project project = projects.LoadProject(file);
        project.GetProperty("_PlatformSDKDiscoveredLocation").ShouldNotBeNull();
        project.GetPropertyValue("_PlatformSDKDiscoveredLocation").ShouldBe(value);
        project.GetPropertyValue("_RestoredPlatformSDKInputTargetPlatformIdentifier").ShouldBe(value);
    }

    [Fact]
    public void DoesNotRewriteUnchangedDiscovery()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string file = Path.Combine(env.CreateFolder().Path, "sdk.props");
        WritePlatformSDKRestoreImport task = CreateTask(file, "sdk");

        task.Execute().ShouldBeTrue();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
        DateTime written = File.GetLastWriteTimeUtc(file);
        task.Execute().ShouldBeTrue();

        File.GetLastWriteTimeUtc(file).ShouldBe(written);
    }

    private WritePlatformSDKRestoreImport CreateTask(string file, string value)
    {
        TaskItem input = new("TargetPlatformIdentifier");
        input.SetMetadata("Value", ProjectCollection.Escape(value));
        TaskItem fact = new("_PlatformSDKDiscoveredLocation");
        fact.SetMetadata("Value", ProjectCollection.Escape(value));
        return new()
        {
            BuildEngine = new MockEngine(_output),
            TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
            File = new TaskItem(file),
            Inputs = [input],
            Facts = [fact],
            DeclaredOutputs = [new TaskItem(file)],
        };
    }
}
