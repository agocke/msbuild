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
    public sealed class WriteFileFingerprint_Tests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        [Fact]
        public void DoesNotRewriteMarkerWhenFileStateIsUnchanged()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string input = Path.Combine(folder.Path, "input.dll");
            string marker = Path.Combine(folder.Path, "marker");
            File.WriteAllText(input, "contents");
            WriteFileFingerprint task = CreateTask([input], marker);

            task.Execute().ShouldBeTrue();
            DateTime firstWrite = File.GetLastWriteTimeUtc(marker);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            task.Execute().ShouldBeTrue();

            File.GetLastWriteTimeUtc(marker).ShouldBe(firstWrite);
        }

        [Fact]
        public void RewritesMarkerWhenFileContentsChange()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string input = Path.Combine(folder.Path, "input.dll");
            string marker = Path.Combine(folder.Path, "marker");
            File.WriteAllText(input, "first");
            WriteFileFingerprint task = CreateTask([input], marker);

            task.Execute().ShouldBeTrue();
            DateTime firstWrite = File.GetLastWriteTimeUtc(marker);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            File.WriteAllText(input, "second");
            task.Execute().ShouldBeTrue();

            File.GetLastWriteTimeUtc(marker).ShouldBeGreaterThan(firstWrite);
        }

        [Fact]
        public void FingerprintIncludesThePathSetButNotInputOrder()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string first = Path.Combine(folder.Path, "first.dll");
            string second = Path.Combine(folder.Path, "second.dll");
            string sameStateMarker = Path.Combine(folder.Path, "same.marker");
            string changedSetMarker = Path.Combine(folder.Path, "changed.marker");
            File.WriteAllText(first, "same");
            File.WriteAllText(second, "same");

            CreateTask([first, second], sameStateMarker).Execute().ShouldBeTrue();
            string expected = File.ReadAllText(sameStateMarker);

            CreateTask([second, first], changedSetMarker).Execute().ShouldBeTrue();
            File.ReadAllText(changedSetMarker).ShouldBe(expected);

            CreateTask([first], changedSetMarker).Execute().ShouldBeTrue();
            File.ReadAllText(changedSetMarker).ShouldNotBe(expected);
        }

        [Fact]
        public void MissingInputFailsWithoutWritingMarker()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string marker = Path.Combine(folder.Path, "marker");
            WriteFileFingerprint task = CreateTask([Path.Combine(folder.Path, "missing.dll")], marker);

            task.Execute().ShouldBeFalse();

            File.Exists(marker).ShouldBeFalse();
            ((MockEngine)task.BuildEngine).Log.ShouldContain("MSB3954");
        }

        private WriteFileFingerprint CreateTask(string[] files, string marker)
        {
            ITaskItem[] fileItems = Array.ConvertAll(files, static file => (ITaskItem)new TaskItem(file));
            return new WriteFileFingerprint
            {
                BuildEngine = new MockEngine(_output),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                Files = fileItems,
                File = new TaskItem(marker),
                DeclaredInputs = [.. fileItems, new TaskItem(marker)],
                DeclaredOutputs = [new TaskItem(marker)],
            };
        }
    }
}
