// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.Tasks.UnitTests;

public sealed class ValidateHardenedAssemblySearchPaths_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("{CandidateAssemblyFiles}")]
    [InlineData("{hintpathfromitem}")]
    [InlineData("{TARGETFRAMEWORKDIRECTORY}")]
    public void AcceptsExplicitReferenceSourcesWithoutProbingDirectories(string token)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string directory = Path.Combine(env.CreateFolder().Path, "nonexistent-reference-pack");
        ValidateHardenedAssemblySearchPaths task = new()
        {
            BuildEngine = new MockEngine(_output),
            SearchPaths = [token, directory]
        };

        task.Execute().ShouldBeTrue();
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{gac}")]
    [InlineData("{AssemblyFolders}")]
    [InlineData("{registry:Software\\Microsoft\\.NETFramework,v4.8,AssemblyFoldersEx}")]
    [InlineData("{AssemblyFoldersFromConfig:machine.config,v4.8}")]
    [InlineData("{RawFileName}")]
    [InlineData("{UnknownResolver}")]
    [InlineData("relative-reference-directory")]
    [InlineData(@"C:drive-relative")]
    [InlineData(@"\root-relative")]
    public void RejectsAmbientOrUnknownReferenceSources(string searchPath)
    {
        MockEngine engine = new(_output);
        ValidateHardenedAssemblySearchPaths task = new()
        {
            BuildEngine = engine,
            SearchPaths = ["{HintPathFromItem}", searchPath]
        };

        task.Execute().ShouldBeFalse();
        engine.Log.ShouldContain("MSB3669");
        engine.Log.ShouldContain(searchPath);
    }
}
