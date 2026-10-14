// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.ActionTaskHost.UnitTests;

public sealed class ActionGraph_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HardenedReferenceDefaultsIgnoreInstallationAssemblyFolders(bool sdkProfile, bool configExists)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile, configExists);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt"))
            .ShouldBe("literal%3B;value|{CandidateAssemblyFiles};{HintPathFromItem};{TargetFrameworkDirectory}|");
    }

    [UnixOnlyTheory]
    [InlineData("{gac}")]
    [InlineData("{Registry:machine,v4.8,AssemblyFoldersEx}")]
    [InlineData("{AssemblyFoldersFromConfig:machine.config,v4.8}")]
    [InlineData("{RawFileName}")]
    [InlineData("relative-reference-directory")]
    public void RejectsUnsafeExplicitReferenceSearchOverridesBeforePublishingGraph(string searchPath)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile: true, configExists: false, searchPath);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain("MSB3669");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void AcceptsAnExplicitAbsoluteReferenceSearchDirectoryWithoutProbingIt()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string references = Path.Combine(directory, "nonexistent-reference-pack");
        string project = WriteReferenceSearchProject(directory, sdkProfile: true, configExists: false, references);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe("literal%3B;value|" + references + "|");
        Directory.Exists(references).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryReferenceDefaultsRetainTheirExistingSearchSources(bool sdkProfile)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteReferenceSearchProject(directory, sdkProfile, configExists: true);

        string contents = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -getProperty:AssemblySearchPaths,AssemblyFoldersConfigFileSearchPath -nr:false -nologo -v:q",
            out bool succeeded, outputHelper: _output);

        succeeded.ShouldBeTrue();
        contents.ShouldContain("{RawFileName}");
        contents.ShouldContain("{AssemblyFoldersFromConfig:");
        if (!sdkProfile)
        {
            contents.ShouldContain("{GAC}");
            contents.ShouldContain("{Registry:");
            contents.ShouldContain("{AssemblyFolders}");
        }
    }

    [UnixOnlyTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RegularRestoreCapturesGraphInputsOnlyWhenRequested(bool capture, bool multiTarget)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = Path.Combine(directory, "restore.csproj");
        XDocument contents = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources", "HelloWorld.csproj"));
        XElement targetFramework = contents.Descendants("TargetFramework").ShouldHaveSingleItem();
        string[] frameworks = multiTarget
            ? [targetFramework.Value, targetFramework.Value + "-windows"]
            : [targetFramework.Value];
        targetFramework.Name = multiTarget ? "TargetFrameworks" : "TargetFramework";
        targetFramework.Value = string.Join(";", frameworks);
        contents.Root!.Element("PropertyGroup")!.Add(
            new XElement("RestoreSources", directory),
            new XElement("NuGetAudit", "false"));
        File.WriteAllText(project, contents.ToString());
        if (multiTarget)
        {
            foreach (string framework in frameworks)
            {
                string intermediate = Path.Combine(directory, "obj", "Debug", framework);
                Directory.CreateDirectory(intermediate);
                File.WriteAllLines(Path.Combine(intermediate, "restore.csproj.FileListAbsolute.txt"),
                    [Path.Combine(directory, "prior-" + framework + ".dll")]);
            }

            File.WriteAllText(Path.Combine(directory, "obj", "restore.csproj.UnmanagedRegistration.cache"), "");
        }

        string arguments = $"\"{project}\" -t:Restore -p:MSBuildRestoreGraphInputs={capture} -nr:false -v:minimal";
        RunnerUtilities.ExecBootstrapedMSBuild(arguments, out bool succeeded, outputHelper: _output);
        succeeded.ShouldBeTrue();
        Dictionary<string, DateTime> writes = new();
        foreach (string framework in frameworks)
        {
            string snapshotDirectory = Path.Combine(directory, "obj", "hardened", "Debug", "AnyCPU", framework);
            string sdk = Path.Combine(snapshotDirectory, "restore.csproj.platform-sdk.props");
            string unmanaged = Path.Combine(snapshotDirectory, "restore.csproj.UnmanagedRegistration.cache.props");
            string clean = Path.Combine(directory, "obj", "Debug", framework, "restore.csproj.FileListAbsolute.txt.hardened.props");
            string[] imports = [sdk, unmanaged, clean];
            foreach (string file in imports)
            {
                File.Exists(file).ShouldBe(capture, file);
                if (capture)
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
                    writes.Add(file, File.GetLastWriteTimeUtc(file));
                }
            }

            if (capture)
            {
                XElement cache = XDocument.Load(unmanaged).Root!;
                XNamespace ns = cache.Name.Namespace;
                (cache.Element(ns + "PropertyGroup")?.Element(ns + "_HardenedUnmanagedRegistrationCacheExists")?.Value ?? "")
                    .ShouldBe(multiTarget ? "true" : "");
                IEnumerable<XElement> priorWrites = XDocument.Load(clean).Descendants(ns + "_CleanUnfilteredPriorFileWrites");
                if (multiTarget)
                {
                    priorWrites.ShouldHaveSingleItem().Attribute("Include")!.Value
                        .ShouldBe(Path.Combine(directory, "prior-" + framework + ".dll"));
                }
                else
                {
                    priorWrites.ShouldBeEmpty();
                }
            }
        }

        if (capture)
        {
            RunnerUtilities.ExecBootstrapedMSBuild(arguments, out succeeded, outputHelper: _output);
            succeeded.ShouldBeTrue();
            foreach (KeyValuePair<string, DateTime> write in writes)
            {
                File.GetLastWriteTimeUtc(write.Key).ShouldBe(write.Value);
            }
        }
    }

    [UnixOnlyTheory]
    [InlineData(false, "out $ space'quote/", "out $ space'quote/")]
    [InlineData(true, "out $ space'quote/", "out $ space'quote/")]
    [InlineData(false, "unused/../out %253B%3B space/", "out %3B; space/")]
    public void ConstructsTargetDirectoryFromTheExplicitProjectBase(bool absolute, string outDir, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string commonTargets = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion, "Microsoft.Common.CurrentVersion.targets");
        XElement properties = new("PropertyGroup",
            new XElement("OutDir", absolute ? Path.Combine(directory, outDir) : outDir),
            new XElement("TargetDir", "must-be-overridden"));
        foreach (XElement element in XDocument.Load(commonTargets).Root!.Elements("PropertyGroup").Elements("TargetDir"))
        {
            properties.Add(new XElement(element));
        }

        string contents = File.ReadAllText(project)
            .Replace("<Target Name=\"Build\"", properties + "\n<Target Name=\"Build\"")
            .Replace("</GraphValueTask>", """
                </GraphValueTask>
                <ItemGroup>
                  <Values>
                    <Tag>$(TargetDir)</Tag>
                  </Values>
                </ItemGroup>
                """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt"))
            .ShouldBe("literal%3B;value|" + Path.GetFullPath(Path.Combine(directory, expected)));
    }

    [UnixOnlyTheory]
    [InlineData("Windows Metadata")]
    [InlineData("References/CommonConfiguration/Neutral")]
    public void RestoreCapturesSDKIOButLeavesPropertyDerivationInGraphConstruction(string metadataSubdirectory)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string sdk = Path.Combine(directory, "sdk & space'quote");
        string metadata = Path.Combine(sdk, metadataSubdirectory);
        Directory.CreateDirectory(metadata);
        string project = WritePlatformSDKProject(directory, sdk);
        RestorePlatformSDK(project).ShouldBeTrue();
        string snapshot = Path.Combine(directory, "sdk.props");
        string contents = File.ReadAllText(snapshot);
        contents.ShouldContain("<_PlatformSDKPathExists>true</_PlatformSDKPathExists>");
        contents.ShouldNotContain("<TargetPlatformSdkPath>");
        contents.ShouldNotContain("<TargetPlatformWinMDLocation>");
        File.SetLastWriteTimeUtc(snapshot, DateTime.UtcNow.AddMinutes(-10));
        DateTime written = File.GetLastWriteTimeUtc(snapshot);
        RestorePlatformSDK(project).ShouldBeTrue();
        File.GetLastWriteTimeUtc(snapshot).ShouldBe(written);
        Directory.Move(sdk, sdk + ".moved");
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph,
            "-p:TargetPlatformWinMDLocation=explicit-winmd -p:TargetPlatformDisplayName=explicit-display").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "result.txt")).ShouldBeFalse();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "result.txt")).Replace('\\', '/')
            .ShouldBe(metadata.Replace('\\', '/') + "|explicit-winmd|GraphMissingSDK,Version=1.0|explicit-display|");
    }

    [UnixOnlyTheory]
    [InlineData("MSBuildRestoreGraphInputs")]
    [InlineData("MSBuildRestorePlatformSDK")]
    public void RestoreRecordsAnAbsentSDKWithoutRepeatingMachineDiscovery(string restoreProperty)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        RestorePlatformSDK(project, restoreProperty).ShouldBeTrue();
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        Ninja(graph, out bool succeeded);

        succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "result.txt"))
            .ShouldBe("||GraphMissingSDK,Version=1.0|GraphMissingSDK 1.0|");
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingOrMismatchedPlatformSDKDiscovery(bool restore)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        if (restore)
        {
            RestorePlatformSDK(project).ShouldBeTrue();
        }

        string graph = Path.Combine(directory, "build.ninja");
        Generate(project, graph, out string log, "-p:TargetPlatformVersion=2.0").ShouldBeFalse();

        log.ShouldContain(restore ? "MSB3668" : "MSB4019");
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void RestoreCapturesPlatformSDKDiscoveryForEachFramework()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WritePlatformSDKProject(directory);
        string contents = File.ReadAllText(project)
            .Replace("<MSBuildPlatformSDKRestoreFile>$(MSBuildThisFileDirectory)sdk.props</MSBuildPlatformSDKRestoreFile>", "")
            .Replace("<Target Name=\"Restore\" />", """
                <Target Name="Restore">
                  <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="_GenerateProjectRestoreGraphPerFramework"
                           Properties="TargetFramework=first" />
                  <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="_GenerateProjectRestoreGraphPerFramework"
                           Properties="TargetFramework=second" />
                </Target>
                <Target Name="_GenerateProjectRestoreGraphPerFramework" />
                """);
        File.WriteAllText(project, contents);

        RestorePlatformSDK(project).ShouldBeTrue();

        Directory.GetFiles(Path.Combine(directory, "obj"), "*.platform-sdk.props", SearchOption.AllDirectories)
            .Length.ShouldBe(3);
        Generate(project, Path.Combine(directory, "first.ninja"), "-p:TargetFramework=first").ShouldBeTrue();
        Generate(project, Path.Combine(directory, "second.ninja"), "-p:TargetFramework=second").ShouldBeTrue();
    }

    [UnixOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NinjaCompilesAndRunsHelloWorldWithoutTheEngine(bool invalidSource)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder(subfolder: "compiler $ space'quote").Path;
        string source = Path.Combine(directory, "Program.cs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestResources", "Program.cs"), source);
        if (invalidSource)
        {
            File.WriteAllText(source, "System.Console.WriteLine(DoesNotExist);");
        }

        string dotnet = RunnerUtilities.BootstrapDotnetHostPath;
        string dotnetDirectory = Path.GetDirectoryName(dotnet)!;
        string framework = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string runtimeDirectory = Directory.GetDirectories(
            Path.Combine(dotnetDirectory, "shared", "Microsoft.NETCore.App"),
            framework["net".Length..] + ".*").ShouldHaveSingleItem();
        string runtimeVersion = Path.GetFileName(runtimeDirectory);
        string compilerDirectory = Path.Combine(dotnetDirectory, "sdk", RunnerUtilities.BootstrapSdkVersion, "Roslyn", "bincore");
        string referencePack = Directory.GetDirectories(
            Path.Combine(dotnetDirectory, "packs", "Microsoft.NETCore.App.Ref"),
            framework["net".Length..] + ".*").ShouldHaveSingleItem();
        string references = Path.Combine(referencePack, "ref", framework);
        Directory.Exists(references).ShouldBeTrue();
        string project = Path.Combine(AppContext.BaseDirectory, "TestResources", "HelloWorld.proj");
        string graph = Path.Combine(directory, "build.ninja");
        string assembly = Path.Combine(directory, "HelloWorld.dll");
        string runtimeConfig = Path.Combine(directory, "HelloWorld.runtimeconfig.json");
        string arguments = $"-p:CompilerTaskAssembly=\"{typeof(GraphCSharpCompileTask).Assembly.Location}\""
            + $" -p:CompilerDirectory=\"{compilerDirectory}\""
            + $" -p:ReferenceAssemblyDirectory=\"{references}\""
            + $" -p:RuntimeDirectory=\"{runtimeDirectory}\""
            + $" -p:RuntimeVersion=\"{runtimeVersion}\""
            + $" -p:HelloWorldSource=\"{source}\""
            + $" -p:OutputDirectory=\"{directory}\"";

        Generate(project, graph, arguments).ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(assembly).ShouldBeFalse();
        File.Exists(runtimeConfig).ShouldBeFalse();

        string compilation = Ninja(graph, out bool success);
        if (invalidSource)
        {
            success.ShouldBeFalse();
            compilation.ShouldContain("CS0103");
            File.Exists(assembly).ShouldBeFalse();
            File.Exists(runtimeConfig).ShouldBeFalse();
            return;
        }

        success.ShouldBeTrue();
        string result = RunnerUtilities.RunProcessAndGetOutput(
            dotnet, $"\"{assembly}\"", out success, outputHelper: _output,
            attachProcessId: false, environmentVariables: RunnerUtilities.GetBootstrapMSBuildEnvironmentVariables());
        success.ShouldBeTrue();
        result.Trim().ShouldBe("Hello, World!");
        DateTime assemblyWrite = File.GetLastWriteTimeUtc(assembly);
        DateTime configWrite = File.GetLastWriteTimeUtc(runtimeConfig);

        string noOp = Ninja(graph, out success);
        success.ShouldBeTrue();
        noOp.ShouldContain("no work to do");
        File.GetLastWriteTimeUtc(assembly).ShouldBe(assemblyWrite);
        File.GetLastWriteTimeUtc(runtimeConfig).ShouldBe(configWrite);

        File.WriteAllText(source, """System.Console.WriteLine("Hello again!");""");
        File.SetLastWriteTimeUtc(source, assemblyWrite.AddSeconds(2));
        Ninja(graph, out success);
        success.ShouldBeTrue();
        result = RunnerUtilities.RunProcessAndGetOutput(
            dotnet, $"\"{assembly}\"", out success, outputHelper: _output,
            attachProcessId: false, environmentVariables: RunnerUtilities.GetBootstrapMSBuildEnvironmentVariables());
        success.ShouldBeTrue();
        result.Trim().ShouldBe("Hello again!");
        File.GetLastWriteTimeUtc(runtimeConfig).ShouldBe(configWrite);
    }

    [UnixOnlyTheory]
    [InlineData("windows", "WINDOWS", "True")]
    [InlineData("linux", "OsX", "False")]
    [InlineData("osx", "oSx", "True")]
    [InlineData("freebsd", "WINDOWS", "False")]
    [InlineData("LINUX", "linux", "True")]
    public void UsesCapturedHostOSDuringEvaluationAndConstruction(string hostOS, string query, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("<Target Name=\"Build\"", $$"""
            <PropertyGroup>
              <MSBuildGraphHostOS>ignored-project-value</MSBuildGraphHostOS>
              <RequestedOS>{{query}}</RequestedOS>
              <Evaluated>$([MSBuild]::IsOSPlatform('$(RequestedOS)'))</Evaluated>
            </PropertyGroup>
            <Target Name="Build"
            """);
        contents = contents.Replace("<GraphValueTask", """
            <PropertyGroup>
              <Constructed>$([MSBuild]::IsOSPlatform('$(RequestedOS)'))</Constructed>
            </PropertyGroup>
            <GraphValueTask
            """);
        contents = contents.Replace("</GraphValueTask>", """
            </GraphValueTask>
            <ItemGroup>
              <Values>
                <Tag>$(Evaluated)|$(Constructed)|$(MSBuildGraphHostOS)</Tag>
              </Values>
            </ItemGroup>
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, $"-p:MSBuildGraphHostOS={hostOS}").ShouldBeTrue();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.ReadAllText(graph).ShouldContain("msbuild_construction_host_os = " + hostOS.ToLowerInvariant());
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe($"literal%3B;value|{expected}|{expected}|{hostOS}");
    }

    [UnixOnlyFact]
    public void CapturedHostOSParticipatesInGraphIdentity()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        string detectedHostOS = OperatingSystem.IsMacOS() ? "osx" :
            OperatingSystem.IsFreeBSD() ? "freebsd" : "linux";
        File.ReadAllText(graph).ShouldContain("msbuild_construction_host_os = " + detectedHostOS);
        Generate(project, graph, "-p:MSBuildGraphHostOS=osx").ShouldBeTrue();
        string firstGraph = File.ReadAllText(graph);
        firstGraph.ShouldContain("msbuild_construction_host_os = osx");
        Generate(project, graph, "-p:MSBuildGraphHostOS=windows").ShouldBeTrue();

        string secondGraph = File.ReadAllText(graph);
        secondGraph.ShouldNotBe(firstGraph);
        secondGraph.ShouldContain("msbuild_construction_host_os = windows");
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyTheory]
    [InlineData("Empty")]
    [InlineData("Unknown")]
    [InlineData("TreatAsLocal")]
    [InlineData("TargetProperty")]
    [InlineData("TaskOutput")]
    [InlineData("NoArguments")]
    [InlineData("EmptyArgument")]
    [InlineData("ExtraArgument")]
    public void RejectsInvalidOrMutableHostConfiguration(string invalid)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = invalid switch
        {
            "TreatAsLocal" => contents.Replace("<Project>", "<Project TreatAsLocalProperty=\"msbuildgraphhostos\">"),
            "TargetProperty" => contents.Replace("<GraphValueTask", "<PropertyGroup><MSBuildGraphHostOS>windows</MSBuildGraphHostOS></PropertyGroup><GraphValueTask"),
            "TaskOutput" => contents.Replace("ItemName=\"Values\"", "PropertyName=\"MSBuildGraphHostOS\""),
            "NoArguments" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform())</Bad></PropertyGroup><Target Name=\"Build\""),
            "EmptyArgument" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform(''))</Bad></PropertyGroup><Target Name=\"Build\""),
            "ExtraArgument" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([MSBuild]::IsOSPlatform('osx', 'linux'))</Bad></PropertyGroup><Target Name=\"Build\""),
            _ => contents,
        };
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");
        string arguments = invalid switch
        {
            "Empty" => "-p:MSBuildGraphHostOS=",
            "Unknown" => "-p:MSBuildGraphHostOS=unknown",
            _ => "-p:MSBuildGraphHostOS=osx",
        };

        Generate(project, graph, arguments).ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void OrdinaryEvaluationStillQueriesTheActualHostOS()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string project = env.CreateFile(".proj").Path;
        File.WriteAllText(project, """
            <Project>
              <PropertyGroup>
                <ActualHostWindows>$([MSBuild]::IsOSPlatform('WINDOWS'))</ActualHostWindows>
              </PropertyGroup>
            </Project>
            """);

        string output = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" --hardened-graph:false -p:MSBuildGraphHostOS=windows -getProperty:ActualHostWindows -nr:false",
            out bool success, outputHelper: _output);

        success.ShouldBeTrue();
        output.ShouldContain("False");
    }

    [UnixOnlyTheory]
    [InlineData("$([MSBuild]::GetTargetFrameworkIdentifier('net11.0'))", ".NETCoreApp")]
    [InlineData("$([MSBuild]::GetTargetFrameworkVersion('net11.0'))", "11.0")]
    [InlineData("$([MSBuild]::IsTargetFrameworkCompatible('net11.0', 'netstandard2.0'))", "True")]
    [InlineData("$([MSBuild]::GetTargetPlatformIdentifier('net11.0-windows10.0'))", "windows")]
    [InlineData("$([MSBuild]::GetTargetPlatformVersion('net11.0-windows10.0'))", "10.0")]
    [InlineData("$([MSBuild]::FilterTargetFrameworks('net11.0;netstandard2.0', 'net11.0'))", "net11.0")]
    [InlineData("$(Parts.Split('.')[0])", "alpha")]
    [InlineData("$(Parts.Split('.')[$(ArrayIndex)])", "beta")]
    [InlineData("$(Parts.Split('.')[2].ToUpperInvariant())", "GAMMA")]
    [InlineData("$(Parts.ToCharArray()[0])", "a")]
    [InlineData("$([MSBuild]::Add(9, $([MSBuild]::Subtract($(_TargetFrameworkVersionWithoutV.Split('.')[0]), 5)))).0", "15.0")]
    public void AllowsPureExpressionsOverDeclaredValues(string expression, string expected)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project);
        contents = contents.Replace("<Target Name=\"Build\"",
            $"""
            <PropertyGroup>
              <Parts>alpha.beta.gamma</Parts>
              <ArrayIndex>1</ArrayIndex>
              <_TargetFrameworkVersionWithoutV>11.0</_TargetFrameworkVersionWithoutV>
              <Parsed>{expression}</Parsed>
            </PropertyGroup>
            <Target Name="Build"
            """);
        contents = contents.Replace("<GraphValueTask",
            $"<PropertyGroup><Constructed>{expression}</Constructed></PropertyGroup><GraphValueTask");
        contents = contents.Replace("</GraphValueTask>", """
            </GraphValueTask>
            <ItemGroup>
              <Values>
                <Tag>$(Parsed)|$(Constructed)</Tag>
              </Values>
            </ItemGroup>
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe($"literal%3B;value|{expected}|{expected}");
    }

    [UnixOnlyTheory]
    [InlineData("$(Parts.Split('.')[3])", "MSB4184")]
    [InlineData("$(Parts.Split('.')[-1])", "MSB4184")]
    [InlineData("$(Parts.Split('.').SetValue('changed', 0))", "MSB4287")]
    [InlineData("$(Parts.Split('.').Clone())", "MSB4287")]
    public void RejectsInvalidArrayIndicesAndUnapprovedArrayMethods(string expression, string errorCode)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("<Target Name=\"Build\"",
            $"""
            <PropertyGroup>
              <Parts>alpha.beta.gamma</Parts>
              <Parsed>{expression}</Parsed>
            </PropertyGroup>
            <Target Name="Build"
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, out string log).ShouldBeFalse();

        log.ShouldContain(errorCode);
        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
    }

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
        foreach (string manifest in manifests)
        {
            using JsonDocument invocation = JsonDocument.Parse(File.ReadAllText(manifest));
            if (invocation.RootElement.GetProperty("parameters").TryGetProperty("Text", out JsonElement text))
            {
                text[0].GetProperty("metadata").GetProperty("ConstructionDirectory").GetString().ShouldBe(directory);
            }
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
    [InlineData("MissingZeroDeclaration")]
    [InlineData("ConflictingOutputs")]
    [InlineData("Exists")]
    [InlineData("PropertyIO")]
    [InlineData("EvaluationIO")]
    [InlineData("ContinueOnError")]
    [InlineData("OnError")]
    [InlineData("Status")]
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
            "MissingZeroDeclaration" => contents.Replace("<GraphValueTask DeclaredInputs=\"\" DeclaredOutputs=\"\">", "<GraphValueTask DeclaredInputs=\"\">"),
            "ConflictingOutputs" => contents.Replace("DeclaredOutputs=\"second.txt\"", "DeclaredOutputs=\"first.txt\""),
            "Exists" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"Exists('first.txt')\""),
            "PropertyIO" => contents.Replace("<Target Name=\"Build\"", "<Target Name=\"Build\" Condition=\"$([System.IO.File]::Exists('first.txt'))\""),
            "EvaluationIO" => contents.Replace("<Target Name=\"Build\"", "<PropertyGroup><Bad>$([System.IO.File]::WriteAllText('$(MSBuildProjectDirectory)/forbidden.txt', 'bad'))</Bad></PropertyGroup><Target Name=\"Build\""),
            "ContinueOnError" => contents.Replace("Text=\"@(Values)\"", "Text=\"@(Values)\" ContinueOnError=\"true\""),
            "OnError" => contents.Replace("</Target>", "<OnError ExecuteTargets=\"Failure\" /></Target><Target Name=\"Failure\" />"),
            "Status" => contents.Replace("Source=\"first.txt\"", "Source=\"first.txt\" Condition=\"'$(MSBuildLastTaskResult)' == 'true'\""),
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

    [UnixOnlyTheory]
    [InlineData("--hardened-graph")]
    [InlineData("--hardened-graph -p:MSBuildIsRestoring=true")]
    [InlineData("--hardened-graph -restore")]
    public void HardenedModeRequiresGraphEmissionWithoutExecutingTasks(string arguments)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", """
            <Target Name="Restore">
              <WriteLinesToFile File="forbidden.txt" Lines="restored" Overwrite="true" />
            </Target>
            </Project>
            """));

        string log = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" {arguments} -nr:false -v:minimal",
            out bool success, outputHelper: _output);

        success.ShouldBeFalse();
        log.ShouldContain("MSB4286");
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "second.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void RejectsCombinedRestoreAndConstructionWithGraphConfiguration()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", """
            <Target Name="Restore">
              <WriteLinesToFile File="forbidden.txt" Lines="restored" Overwrite="true" />
            </Target>
            </Project>
            """));
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-restore").ShouldBeFalse();

        File.Exists(graph).ShouldBeFalse();
        Directory.Exists(graph + ".actions").ShouldBeFalse();
        File.Exists(Path.Combine(directory, "forbidden.txt")).ShouldBeFalse();
    }

    [UnixOnlyFact]
    public void MigratedZeroIOTaskUsesProjectContextAndSingleNode()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = env.CreateFolder().Path;
        string project = WriteProject(directory);
        string contents = File.ReadAllText(project).Replace("Text=\"@(Values)\"", "Text=\"@(Assigned)\"");
        contents = contents.Replace("<GraphTransformTask File=\"first.txt\"", """
            <ItemGroup>
              <Input Include="folder/file.txt" />
            </ItemGroup>
            <AssignTargetPathWithProjectDirectory
                Files="@(Input)" RootFolder="$(MSBuildProjectDirectory)"
                DeclaredInputs="" DeclaredOutputs="">
              <Output TaskParameter="AssignedFiles" ItemName="Assigned" />
            </AssignTargetPathWithProjectDirectory>
            <ItemGroup>
              <Assigned>
                <Tag>%(Assigned.TargetPath)|$(MSBuildNodeCount)</Tag>
              </Assigned>
            </ItemGroup>
            <GraphTransformTask File="first.txt"
            """);
        File.WriteAllText(project, contents);
        string graph = Path.Combine(directory, "build.ninja");

        Generate(project, graph, "-m:2").ShouldBeTrue();
        Directory.GetFiles(graph + ".actions", "*.json").Length.ShouldBe(2);
        File.Exists(Path.Combine(directory, "first.txt")).ShouldBeFalse();

        Ninja(graph, out bool success);

        success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, "first.txt")).ShouldBe("folder/file.txt|folder/file.txt|1");
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

    private static string WritePlatformSDKProject(string directory, string sdkRoot = "")
    {
        string project = Path.Combine(directory, "build.proj");
        File.WriteAllText(project, $$"""
            <Project DefaultTargets="Build">
              <UsingTask TaskName="GraphTransformTask" AssemblyFile="{{SecurityElement.Escape(typeof(GraphTransformTask).Assembly.Location)}}" />
              <PropertyGroup>
                <TargetPlatformIdentifier>GraphMissingSDK</TargetPlatformIdentifier>
                <TargetPlatformVersion>1.0</TargetPlatformVersion>
                <TargetPlatformSdkRootOverride>{{SecurityElement.Escape(sdkRoot)}}</TargetPlatformSdkRootOverride>
                <MSBuildPlatformSDKRestoreFile>$(MSBuildThisFileDirectory)sdk.props</MSBuildPlatformSDKRestoreFile>
              </PropertyGroup>
              <Import Project="$(MSBuildToolsPath)/Microsoft.Common.PlatformSDK.targets" />
              <Target Name="Restore" />
              <Target Name="Build">
                <GraphTransformTask File="result.txt"
                                    Text="$(TargetPlatformSdkMetadataLocation)|$(TargetPlatformWinMDLocation)|$(TargetPlatformMoniker)|$(TargetPlatformDisplayName)"
                                    DeclaredInputs="" DeclaredOutputs="result.txt" />
              </Target>
            </Project>
            """);
        return project;
    }

    private bool RestorePlatformSDK(string project, string restoreProperty = "MSBuildRestoreGraphInputs")
    {
        RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" -t:Restore -p:{restoreProperty}=true -nr:false -v:minimal",
            out bool succeeded, outputHelper: _output);
        return succeeded;
    }

    private static string WriteReferenceSearchProject(string directory, bool sdkProfile, bool configExists, string? searchPaths = null)
    {
        string sdk = Path.Combine(Path.GetDirectoryName(RunnerUtilities.BootstrapDotnetHostPath)!,
            "sdk", RunnerUtilities.BootstrapSdkVersion);
        XElement common = XDocument.Load(Path.Combine(sdk, "Microsoft.Common.CurrentVersion.targets")).Root!;
        XElement props = XDocument.Load(Path.Combine(sdk, "Current", "Microsoft.Common.props")).Root!;
        string project = WriteProject(directory);
        XDocument contents = XDocument.Load(project);
        XElement root = contents.Root!;
        XElement build = root.Elements("Target").ShouldHaveSingleItem();
        root.SetAttributeValue("DefaultTargets", "Build");
        build.SetAttributeValue("DependsOnTargets", "$(ResolveAssemblyReferencesDependsOn)");
        build.Element("GraphValueTask")!.AddAfterSelf(new XElement("ItemGroup",
            new XElement("Values", new XElement("Tag", "$(AssemblySearchPaths)|$(AssemblyFoldersConfigFileSearchPath)"))));
        XElement inputs = new("PropertyGroup", new XElement("AssemblySearchPaths", searchPaths ?? ""));
        if (configExists)
        {
            string config = Path.Combine(directory, "AssemblyFolders.config");
            File.WriteAllText(config, "<installation-specific-assembly-search />");
            inputs.Add(new XElement("AssemblyFoldersConfigFile", config));
        }

        root.AddFirst(inputs,
            new XElement(props.Elements("PropertyGroup").Elements("AssemblySearchPath_UseGAC").ShouldHaveSingleItem().Parent!));
        if (sdkProfile)
        {
            XElement sdkProps = XDocument.Load(Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "targets", "Microsoft.NET.Sdk.props")).Root!;
            XNamespace ns = sdkProps.Name.Namespace;
            XElement defaults = new(sdkProps.Elements(ns + "PropertyGroup")
                .Where(group => group.Elements(ns + "AssemblySearchPaths").Any()).ShouldHaveSingleItem());
            foreach (XElement element in defaults.DescendantsAndSelf())
            {
                element.Name = element.Name.LocalName;
            }

            root.Add(defaults);
        }

        root.Add(
            new XElement(common.Elements("PropertyGroup").Elements("AssemblyFoldersConfigFile").ShouldHaveSingleItem().Parent!),
            new XElement(common.Elements("PropertyGroup").Where(group => group.Elements("AssemblySearchPaths").Any()).ShouldHaveSingleItem()),
            new XElement(common.Elements("PropertyGroup")
                .Where(group => group.Element("ResolveAssemblyReferencesDependsOn") is not null && group.Attribute("Condition") is not null)
                .ShouldHaveSingleItem()),
            new XElement(common.Elements("Target")
                .Where(target => target.Attribute("Name")?.Value == "_ValidateHardenedAssemblySearchPaths").ShouldHaveSingleItem()));
        File.WriteAllText(project, contents.ToString());
        return project;
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

    private bool Generate(string project, string graph, string arguments = "")
        => Generate(project, graph, out _, arguments);

    private bool Generate(string project, string graph, out string log, string arguments = "")
    {
        string testDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string framework = Path.GetFileName(testDirectory);
        string configuration = Path.GetFileName(Path.GetDirectoryName(testDirectory))!;
        string host = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..",
            "MSBuild.ActionTaskHost", configuration, framework, "MSBuild.ActionTaskHost.dll"));
        string dotnet = RunnerUtilities.BootstrapDotnetHostPath;
        log = RunnerUtilities.ExecBootstrapedMSBuild(
            $"\"{project}\" --hardened-graph -nr:false -v:minimal {arguments}"
                + $" -p:MSBuildEmitActionGraph=\"{graph}\" -p:MSBuildActionTaskHost=\"{host}\""
                + $" -p:MSBuildActionDotNetHost=\"{dotnet}\"",
            out bool succeeded, outputHelper: _output);
        return succeeded;
    }

    private string Ninja(string graph, out bool success)
        => RunnerUtilities.RunProcessAndGetOutput("ninja", $"-f \"{graph}\" -j1", out success, outputHelper: _output);
}

[MSBuildDeclaredIOTask]
public sealed class GraphCSharpCompileTask : Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    [Required]
    public ITaskItem[] References { get; set; } = [];

    [Required]
    public string CompilerAssembly { get; set; } = "";

    [Required]
    public string DotNetHost { get; set; } = "";

    [Required]
    public string OutputAssembly { get; set; } = "";

    public override bool Execute()
    {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "Microsoft.Build")
            {
                throw new InvalidOperationException("Compilation actions must run without the engine.");
            }
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> inputs = new(comparer);
        foreach (ITaskItem input in DeclaredInputs)
        {
            inputs.Add(Absolute(input.ItemSpec));
        }

        RequireInput(CompilerAssembly);
        RequireInput(DotNetHost);
        foreach (ITaskItem source in Sources)
        {
            RequireInput(source.ItemSpec);
        }

        foreach (ITaskItem reference in References)
        {
            RequireInput(reference.ItemSpec);
        }

        if (Sources.Length == 0 || References.Length == 0 ||
            DeclaredOutputs.Length != 1 || !comparer.Equals(Absolute(DeclaredOutputs[0].ItemSpec), Absolute(OutputAssembly)))
        {
            throw new InvalidOperationException("The compiler fixture requires sources, references, and its exact declared assembly output.");
        }

        ProcessStartInfo start = TaskEnvironment.GetProcessStartInfo();
        start.FileName = Absolute(DotNetHost);
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.ArgumentList.Add(Absolute(CompilerAssembly));
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-noconfig");
        start.ArgumentList.Add("-nostdlib+");
        start.ArgumentList.Add("-target:exe");
        start.ArgumentList.Add("-deterministic+");
        start.ArgumentList.Add("-debug-");
        start.ArgumentList.Add("-out:" + Absolute(OutputAssembly));
        foreach (ITaskItem reference in References)
        {
            start.ArgumentList.Add("-reference:" + Absolute(reference.ItemSpec));
        }

        foreach (ITaskItem source in Sources)
        {
            start.ArgumentList.Add(Absolute(source.ItemSpec));
        }

        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The compiler process did not start.");
        }

        System.Threading.Tasks.Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Log.LogMessage(MessageImportance.High, "{0}", stdout.GetAwaiter().GetResult());
        Log.LogMessage(MessageImportance.High, "{0}", stderr.GetAwaiter().GetResult());
        return process.ExitCode == 0;

        string Absolute(string path) => TaskEnvironment.GetAbsolutePath(path).GetCanonicalForm().Value;

        void RequireInput(string path)
        {
            if (!inputs.Contains(Absolute(path)))
            {
                throw new InvalidOperationException($"Compiler input '{path}' was not declared.");
            }
        }
    }
}

[MSBuildDeclaredIOTask]
public sealed class GraphValueTask : Utilities.Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
    public ITaskItem[] DeclaredInputs { get; set; } = [];
    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    [Output]
    public ITaskItem[] Values { get; private set; } = [];

    public override bool Execute()
    {
        Utilities.TaskItem value = new("literal%253B%3Bvalue", treatAsFilePath: false);
        ((ITaskItem2)value).SetMetadataValueLiteral("Tag", "metadata%25");
        ((ITaskItem2)value).SetMetadataValueLiteral("ConstructionDirectory", TaskEnvironment.ProjectDirectory);
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
