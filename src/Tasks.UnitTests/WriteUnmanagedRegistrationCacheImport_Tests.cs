// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.Tasks.UnitTests
{
    public sealed class WriteUnmanagedRegistrationCacheImport_Tests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        [Fact]
        public void ExistingCacheWritesPresenceProperty()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cacheFile = Path.Combine(folder.Path, "registration.cache");
            string importFile = Path.Combine(folder.Path, "registration.props");
            File.WriteAllText(cacheFile, "cache");

            CreateTask(cacheFile, importFile).Execute().ShouldBeTrue();

            XDocument document = XDocument.Load(importFile);
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
            document
                .Descendants(ns + "_HardenedUnmanagedRegistrationCacheExists")
                .Single()
                .Value
                .ShouldBe("true");
        }

        [Fact]
        public void MissingCacheWritesEmptyImport()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string importFile = Path.Combine(folder.Path, "registration.props");

            CreateTask(Path.Combine(folder.Path, "missing.cache"), importFile).Execute().ShouldBeTrue();

            XDocument document = XDocument.Load(importFile);
            document.Root.ShouldNotBeNull();
            document.Root.Elements().ShouldBeEmpty();
        }

        [Fact]
        public void DoesNotRewriteUnchangedImport()
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFolder folder = env.CreateFolder(createFolder: true);
            string cacheFile = Path.Combine(folder.Path, "registration.cache");
            string importFile = Path.Combine(folder.Path, "registration.props");
            WriteUnmanagedRegistrationCacheImport task = CreateTask(cacheFile, importFile);

            task.Execute().ShouldBeTrue();
            DateTime firstWrite = File.GetLastWriteTimeUtc(importFile);

            Thread.Sleep(TimeSpan.FromSeconds(1));
            task.Execute().ShouldBeTrue();

            File.GetLastWriteTimeUtc(importFile).ShouldBe(firstWrite);
        }

        private WriteUnmanagedRegistrationCacheImport CreateTask(string cacheFile, string importFile)
        {
            return new WriteUnmanagedRegistrationCacheImport
            {
                BuildEngine = new MockEngine(_output),
                TaskEnvironment = TaskEnvironmentHelper.CreateForTest(),
                CacheFile = new TaskItem(cacheFile),
                ImportFile = new TaskItem(importFile),
                DeclaredInputs = [new TaskItem(cacheFile)],
                DeclaredOutputs = [new TaskItem(importFile)],
            };
        }
    }
}
