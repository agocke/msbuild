// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Graph;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

namespace Microsoft.Build.Engine.UnitTests.Graph
{
    public sealed class PureEvaluationGraph_Tests : IDisposable
    {
        private readonly TestEnvironment _env = TestEnvironment.Create();

        public void Dispose()
        {
            _env.Dispose();
        }

        [Fact]
        public void PureModeAppliesToEveryConfiguredProject()
        {
            string child = _env.CreateFile(
                "child.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>$([System.DateTime]::UtcNow)</Value>
                  </PropertyGroup>
                </Project>
                """).Path;

            string root = _env.CreateFile(
                "root.proj",
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="child.proj" />
                  </ItemGroup>
                </Project>
                """).Path;

            ProjectGraphOptions options = new()
            {
                EntryPoints = [new ProjectGraphEntryPoint(root)],
                EvaluationMode = ProjectEvaluationMode.Pure,
            };

            AggregateException aggregate = Should.Throw<AggregateException>(
                () => new ProjectGraph(options));
            InvalidProjectFileException exception = aggregate
                .Flatten()
                .InnerExceptions
                .ShouldHaveSingleItem()
                .ShouldBeOfType<InvalidProjectFileException>();

            exception.ErrorCode.ShouldBe("MSB4286");
            exception.ProjectFile.ShouldBe(child);
        }
    }
}
