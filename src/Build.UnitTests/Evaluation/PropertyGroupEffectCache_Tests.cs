// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.Evaluation;

public sealed class PropertyGroupEffectCache_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void ReusesPureFunctionChainsAndPartitionsLiveIns()
    {
        using TestEnvironment env = CreateEnvironment();
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var file = env.CreateFile("functions.proj", """
            <Project>
              <PropertyGroup>
                <Upper>$(Flavor.ToUpperInvariant())</Upper>
                <Match>$([System.Text.RegularExpressions.Regex]::Match('$(Upper)', '(A|B)').Groups[1].Value)</Match>
                <Version>$([System.Version]::Parse('1.2.3').Major)</Version>
                <Overwritten>first</Overwritten>
                <Overwritten>$(Match)-$(Version)</Overwritten>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        var properties = new Dictionary<string, string> { ["Flavor"] = "a", ["Noise"] = "first" };
        ProjectInstance first = Evaluate(root, collection, properties);
        long hits = Hits;
        properties["Noise"] = "second";
        ProjectInstance second = Evaluate(root, collection, properties);
        Hits.ShouldBe(hits + 1);
        first.GetPropertyValue("Overwritten").ShouldBe("A-1");
        second.GetPropertyValue("Overwritten").ShouldBe("A-1");

        properties["Flavor"] = "b";
        Evaluate(root, collection, properties).GetPropertyValue("Overwritten").ShouldBe("B-1");
        Hits.ShouldBe(hits + 1);
        Evaluate(root, collection, properties).GetPropertyValue("Overwritten").ShouldBe("B-1");
        Hits.ShouldBe(hits + 2);
    }

    [Fact]
    public void RechecksFileContentAndExistenceBeforeReplaying()
    {
        using TestEnvironment env = CreateEnvironment();
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var input = env.CreateFile("input.txt", "first");
        var marker = env.CreateFile("marker.txt", "");
        var directory = env.CreateFolder();
        var file = env.CreateFile("observations.proj", """
            <Project>
              <PropertyGroup>
                <Text>$([System.IO.File]::ReadAllText('$(Input)'))</Text>
                <Present>$([System.IO.File]::Exists('$(Marker)'))</Present>
                <DirectoryPresent>$([System.IO.Directory]::Exists('$(Directory)'))</DirectoryPresent>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        var properties = new Dictionary<string, string>
        {
            ["Input"] = input.Path,
            ["Marker"] = marker.Path,
            ["Directory"] = directory.Path,
        };
        ProjectInstance first = Evaluate(root, collection, properties);
        first.GetPropertyValue("Text").ShouldBe("first");
        first.GetPropertyValue("DirectoryPresent").ShouldBe("True");
        long hits = Hits;
        Evaluate(root, collection, properties).GetPropertyValue("Present").ShouldBe("True");
        Hits.ShouldBe(hits + 1);

        Directory.Delete(directory.Path);
        Evaluate(root, collection, properties).GetPropertyValue("DirectoryPresent").ShouldBe("False");
        Hits.ShouldBe(hits + 1);

        File.WriteAllText(input.Path, "second");
        File.Delete(marker.Path);
        ProjectInstance changed = Evaluate(root, collection, properties);
        changed.GetPropertyValue("Text").ShouldBe("second");
        changed.GetPropertyValue("Present").ShouldBe("False");
        changed.GetPropertyValue("DirectoryPresent").ShouldBe("False");
        Hits.ShouldBe(hits + 1);
        Evaluate(root, collection, properties).GetPropertyValue("Text").ShouldBe("second");
        Hits.ShouldBe(hits + 2);

        File.Delete(input.Path);
        InvalidProjectFileException cachedError = Should.Throw<InvalidProjectFileException>(
            () => Evaluate(root, collection, properties));
        env.SetEnvironmentVariable(Traits.EnablePropertyGroupAttachmentEnvVarName, null);
        InvalidProjectFileException ordinaryError = Should.Throw<InvalidProjectFileException>(
            () => Evaluate(root, collection, properties));
        cachedError.ErrorCode.ShouldBe(ordinaryError.ErrorCode);
        cachedError.LineNumber.ShouldBe(ordinaryError.LineNumber);
    }

    [Fact]
    public void RelativeFileObservationsDoNotCrossWorkingDirectories()
    {
        using TestEnvironment env = CreateEnvironment();
        var first = env.CreateFolder();
        var second = env.CreateFolder();
        env.CreateFile(first, "input.txt", "first");
        env.CreateFile(second, "input.txt", "second");
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var file = env.CreateFile("relative.proj", """
            <Project>
              <PropertyGroup>
                <Text>$([System.IO.File]::ReadAllText('input.txt'))</Text>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        env.SetCurrentDirectory(first.Path);
        Evaluate(root, collection).GetPropertyValue("Text").ShouldBe("first");
        long hits = Hits;
        Evaluate(root, collection).GetPropertyValue("Text").ShouldBe("first");
        Hits.ShouldBe(hits + 1);
        env.SetCurrentDirectory(second.Path);
        Evaluate(root, collection).GetPropertyValue("Text").ShouldBe("second");
        Hits.ShouldBe(hits + 1);
        Evaluate(root, collection).GetPropertyValue("Text").ShouldBe("second");
        Hits.ShouldBe(hits + 2);
    }

    [UnixOnlyFact]
    public void RechecksPathHeuristicAndWorkingDirectory()
    {
        using TestEnvironment env = CreateEnvironment();
        var folder = env.CreateFolder();
        env.SetCurrentDirectory(folder.Path);
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var file = env.CreateFile("paths.proj", """
            <Project>
              <PropertyGroup>
                <Path>child\file.txt</Path>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        Evaluate(root, collection).GetPropertyValue("Path").ShouldBe(@"child\file.txt");
        long hits = Hits;
        Evaluate(root, collection).GetPropertyValue("Path").ShouldBe(@"child\file.txt");
        Hits.ShouldBe(hits + 1);

        Directory.CreateDirectory(Path.Combine(folder.Path, "child"));
        Evaluate(root, collection).GetPropertyValue("Path").ShouldBe("child/file.txt");
        Hits.ShouldBe(hits + 1);
        Evaluate(root, collection).GetPropertyValue("Path").ShouldBe("child/file.txt");
        Hits.ShouldBe(hits + 2);

        var other = env.CreateFolder();
        env.SetCurrentDirectory(other.Path);
        Evaluate(root, collection).GetPropertyValue("Path").ShouldBe(@"child\file.txt");
        Hits.ShouldBe(hits + 2);
    }

    [Fact]
    public void NeverReplaysUnsupportedFunctions()
    {
        using TestEnvironment env = CreateEnvironment();
        const string variable = "PropertyGroupReuseTest";
        env.SetEnvironmentVariable(variable, "first");
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var file = env.CreateFile("unsupported.proj", """
            <Project>
              <PropertyGroup>
                <Fresh>$([System.Guid]::NewGuid())</Fresh>
                <Environment>$([System.Environment]::GetEnvironmentVariable('PropertyGroupReuseTest'))</Environment>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        ProjectInstance first = Evaluate(root, collection);
        long hits = Hits;
        env.SetEnvironmentVariable(variable, "second");
        ProjectInstance second = Evaluate(root, collection);
        second.GetPropertyValue("Fresh").ShouldNotBe(first.GetPropertyValue("Fresh"));
        first.GetPropertyValue("Environment").ShouldBe("first");
        second.GetPropertyValue("Environment").ShouldBe("second");
        Hits.ShouldBe(hits);
    }

    [Fact]
    public void PartitionsCultureGlobalOverridesAndXmlVersions()
    {
        using TestEnvironment env = CreateEnvironment();
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var file = env.CreateFile("context.proj", """
            <Project>
              <PropertyGroup>
                <Value>$(Input.ToUpper())</Value>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        var properties = new Dictionary<string, string> { ["Input"] = "i" };
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("I");
        long hits = Hits;
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("I");
        Hits.ShouldBe(hits + 1);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("\u0130");
        Hits.ShouldBe(hits + 1);
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("\u0130");
        Hits.ShouldBe(hits + 2);

        properties["Value"] = "global";
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("global");
        properties.Remove("Value");
        root.Properties.Single().Value = "$(Input.ToLowerInvariant())";
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("i");

        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        hits = Hits;
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("i");
        Evaluate(root, collection, properties).GetPropertyValue("Value").ShouldBe("i");
        Hits.ShouldBe(hits);
    }

    [Fact]
    public void PreservesReassignmentLoggingAndReadObserverFallback()
    {
        using TestEnvironment env = CreateEnvironment();
        ProjectCollection collection = env.CreateProjectCollection().Collection;
        var logger = new MockLogger(_output);
        collection.RegisterLogger(logger);
        var file = env.CreateFile("logging.proj", """
            <Project>
              <PropertyGroup>
                <Value>before</Value>
                <Value>$([System.String]::Concat('af', 'ter'))</Value>
              </PropertyGroup>
            </Project>
            """);
        ProjectRootElement root = ProjectRootElement.Open(file.Path, collection);
        Evaluate(root, collection).GetPropertyValue("Value").ShouldBe("after");
        long hits = Hits;
        Evaluate(root, collection).GetPropertyValue("Value").ShouldBe("after");
        Hits.ShouldBe(hits + 1);
        logger.FullLog.ShouldContain("before");
        logger.FullLog.ShouldContain("after");

        env.SetEnvironmentVariable("MsBuildLogPropertyTracking", "8");
        Evaluate(root, collection).GetPropertyValue("Value").ShouldBe("after");
        Hits.ShouldBe(hits + 1);
    }

    private TestEnvironment CreateEnvironment()
    {
        TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable(Traits.EnablePropertyGroupAttachmentEnvVarName, "1");
        env.SetEnvironmentVariable("MsBuildLogPropertyTracking", "0");
        env.WithTransientTestState(new CultureState());
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        return env;
    }

    private static ProjectInstance Evaluate(ProjectRootElement root, ProjectCollection collection,
        Dictionary<string, string>? properties = null) =>
        new(root, properties, toolsVersion: null, collection);

    private static long Hits => long.Parse(
        PropertyGroupEffectCache.FormatStatistics().Split(' ')[0].AsSpan("hits=".Length), CultureInfo.InvariantCulture);

    private sealed class CultureState : TransientTestState
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public override void Revert()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
