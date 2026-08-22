// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.CommandLine.Experimental;
using Microsoft.Build.Evaluation;
using Shouldly;
using Xunit;

namespace Microsoft.Build.CommandLine.UnitTests
{
    public class CommandLineParserTests
    {
        [Fact]
        public void ParseReturnsInstance()
        {
            CommandLineParser parser = new CommandLineParser();
            CommandLineSwitchesAccessor result = parser.Parse(["/targets:targets.txt"]); // first parameter must be the executable name

            result.Targets.ShouldNotBeNull();
            result.Targets.ShouldBe(["targets.txt"]);
        }

        [Fact]
        public void ParseThrowsException()
        {
            CommandLineParser parser = new CommandLineParser();

            Should.Throw<CommandLineSwitchException>(() =>
            {
                // first parameter must be the executable name
                parser.Parse(["tempproject.proj", "tempproject.proj"]);
            });
        }

        [Fact]
        public void ParseRecognizesEvaluationMode()
        {
            CommandLineSwitchesAccessor result = new CommandLineParser().Parse(["-evaluationMode:Pure"]);

            result.EvaluationMode.ShouldBe(["Pure"]);
        }

        [Theory]
        [InlineData("Classic", ProjectEvaluationMode.Classic)]
        [InlineData("classic", ProjectEvaluationMode.Classic)]
        [InlineData("Pure", ProjectEvaluationMode.Pure)]
        [InlineData("pure", ProjectEvaluationMode.Pure)]
        public void EvaluationModeValuesAreCaseInsensitive(string value, ProjectEvaluationMode expected)
        {
            MSBuildApp.ProcessEvaluationModeSwitch([value]).ShouldBe(expected);
        }

        [Fact]
        public void InvalidEvaluationModeIsRejected()
        {
            Should.Throw<CommandLineSwitchException>(
                () => MSBuildApp.ProcessEvaluationModeSwitch(["tracked"]));
        }
    }
}
