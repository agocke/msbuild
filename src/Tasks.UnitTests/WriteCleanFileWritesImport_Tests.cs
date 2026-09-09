// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Tasks.UnitTests
{
    public sealed class WriteCleanFileWritesImport_Tests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        [Fact]
        public void WritesEscapedPriorFileWritesAsImportedItems()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "prior.txt");
            string importFile = Path.Combine(folder.Path, "prior.props");
            string[] expected =
            [
                Path.Combine(folder.Path, "a&b.dll"),
                Path.Combine(folder.Path, "semi;colon.dll"),
                Path.Combine(folder.Path, "quote'file.dll"),
            ];
            File.WriteAllLines(cleanFile, ["", $"  {expected[0]}  ", expected[1], expected[2]]);

            CreateTask(cleanFile, importFile).Execute().ShouldBeTrue();

            XDocument document = XDocument.Load(importFile);
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
            document
                .Descendants(ns + "_CleanUnfilteredPriorFileWrites")
                .Select(element => element.Attribute("Include")?.Value)
                .ShouldBe(expected);
        }

        [Fact]
        public void MissingCleanFileWritesEmptyImport()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string importFile = Path.Combine(folder.Path, "prior.props");

            CreateTask(Path.Combine(folder.Path, "missing.txt"), importFile).Execute().ShouldBeTrue();

            XDocument document = XDocument.Load(importFile);
            document.Root.ShouldNotBeNull();
            document.Root.Elements().Single().Elements().ShouldBeEmpty();
        }

        [Fact]
        public void DoesNotRewriteUnchangedImport()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "prior.txt");
            string importFile = Path.Combine(folder.Path, "prior.props");
            File.WriteAllLines(cleanFile, [Path.Combine(folder.Path, "output.dll")]);
            WriteCleanFileWritesImport task = CreateTask(cleanFile, importFile);

            task.Execute().ShouldBeTrue();
            DateTime firstWrite = File.GetLastWriteTimeUtc(importFile);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            task.Execute().ShouldBeTrue();

            File.GetLastWriteTimeUtc(importFile).ShouldBe(firstWrite);
        }

        private WriteCleanFileWritesImport CreateTask(string cleanFile, string importFile)
        {
            return new WriteCleanFileWritesImport
            {
                BuildEngine = new MockEngine(_output),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                CleanFile = new TaskItem(cleanFile),
                ImportFile = new TaskItem(importFile),
                DeclaredInputs = [new TaskItem(cleanFile)],
                DeclaredOutputs = [new TaskItem(importFile)],
            };
        }
    }
}
