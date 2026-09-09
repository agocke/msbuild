// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Tasks.UnitTests
{
    public sealed class ReconcileCleanFileWrites_Tests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        [Fact]
        public void RemovesDeletedFilesAndDeduplicatesInExistingOrder()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "files.txt");

            ReconcileCleanFileWrites task = CreateTask(
                cleanFile,
                priorFileWrites: ["prior.dll", "duplicate.dll"],
                currentFileWrites: ["DUPLICATE.dll", "current.dll"],
                deletedFiles: ["PRIOR.dll"]);

            task.Execute().ShouldBeTrue();

            File.ReadAllLines(cleanFile).ShouldBe(["duplicate.dll", "current.dll"]);
        }

        [Fact]
        public void WritesEmptyLedgerWhenAllFilesWereDeleted()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "files.txt");

            ReconcileCleanFileWrites task = CreateTask(
                cleanFile,
                priorFileWrites: ["prior.dll"],
                deletedFiles: ["prior.dll"]);

            task.Execute().ShouldBeTrue();

            File.ReadAllText(cleanFile).ShouldBeEmpty();
        }

        [Fact]
        public void DoesNotRewriteUnchangedLedger()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "files.txt");
            File.WriteAllLines(cleanFile, ["prior.dll"]);
            ReconcileCleanFileWrites task = CreateTask(cleanFile, priorFileWrites: ["prior.dll"]);

            task.Execute().ShouldBeTrue();
            DateTime firstWrite = File.GetLastWriteTimeUtc(cleanFile);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            task.Execute().ShouldBeTrue();

            File.GetLastWriteTimeUtc(cleanFile).ShouldBe(firstWrite);
        }

        [Fact]
        public void QuestionModeFailsWhenLedgerWouldChange()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cleanFile = Path.Combine(folder.Path, "files.txt");
            File.WriteAllLines(cleanFile, ["prior.dll"]);
            ReconcileCleanFileWrites task = CreateTask(
                cleanFile,
                priorFileWrites: ["prior.dll"],
                deletedFiles: ["prior.dll"]);
            task.FailIfNotIncremental = true;

            task.Execute().ShouldBeFalse();

            File.ReadAllLines(cleanFile).ShouldBe(["prior.dll"]);
        }

        private ReconcileCleanFileWrites CreateTask(
            string cleanFile,
            string[] priorFileWrites = null,
            string[] currentFileWrites = null,
            string[] deletedFiles = null)
        {
            return new ReconcileCleanFileWrites
            {
                BuildEngine = new MockEngine(_output),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                CleanFile = new TaskItem(cleanFile),
                PriorFileWrites = CreateItems(priorFileWrites),
                CurrentFileWrites = CreateItems(currentFileWrites),
                DeletedFiles = CreateItems(deletedFiles),
                DeclaredInputs = [new TaskItem(cleanFile)],
                DeclaredOutputs = [new TaskItem(cleanFile)],
            };
        }

        private static ITaskItem[] CreateItems(string[] items)
        {
            return items is null
                ? []
                : Array.ConvertAll(items, static item => (ITaskItem)new TaskItem(item));
        }
    }
}
