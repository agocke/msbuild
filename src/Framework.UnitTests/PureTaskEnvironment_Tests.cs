// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using Microsoft.Build.Framework;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public sealed class PureTaskEnvironment_Tests
{
    [Fact]
    public void CreateWithProjectDirectoryCapturesHostEnvironment()
    {
        string projectDirectory = Path.GetFullPath(Path.GetTempPath());

        PureTaskEnvironment environment =
            PureTaskEnvironment.CreateWithProjectDirectory(projectDirectory);

        environment.ProjectDirectory.Value.ShouldBe(projectDirectory);
        environment.UsesWindowsPathSemantics.ShouldBe(NativeMethodsShared.IsWindows);
        environment.GetAbsolutePath("subdirectory/file.txt").Value.ShouldBe(
            Path.Combine(projectDirectory, "subdirectory/file.txt"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InternalFactoryCapturesSpecifiedPathSemantics(bool usesWindowsPathSemantics)
    {
        PureTaskEnvironment environment = PureTaskEnvironment.Create(
            new AbsolutePath(Path.GetFullPath(Path.GetTempPath())),
            usesWindowsPathSemantics);

        environment.UsesWindowsPathSemantics.ShouldBe(usesWindowsPathSemantics);
    }
}
