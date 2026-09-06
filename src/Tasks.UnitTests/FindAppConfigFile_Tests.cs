// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public class FindAppConfigFile_Tests
    {
        [Fact]
        public void OnlyDeterministicVariantIsMarkedPure()
        {
            Attribute.IsDefined(
                typeof(FindAppConfigFileWithDeterministicSemantics),
                typeof(MSBuildPureTaskAttribute),
                inherit: false).ShouldBeTrue();
            Attribute.IsDefined(
                typeof(FindAppConfigFile),
                typeof(MSBuildPureTaskAttribute),
                inherit: false).ShouldBeFalse();
        }

        [Fact]
        public void FoundInFirstInProjectDirectory()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            f.PrimaryList = new ITaskItem[] { new TaskItem("app.config"), new TaskItem("xxx") };
            f.SecondaryList = System.Array.Empty<ITaskItem>();
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            Assert.Equal("app.config", f.AppConfigFile.ItemSpec);
            Assert.Equal("targetpath", f.AppConfigFile.GetMetadata("TargetPath"));
        }

        [Fact]
        public void FoundInSecondInProjectDirectory()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            f.PrimaryList = new ITaskItem[] { new TaskItem("yyy"), new TaskItem("xxx") };
            f.SecondaryList = new ITaskItem[] { new TaskItem("app.config"), new TaskItem("xxx") };
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            Assert.Equal("app.config", f.AppConfigFile.ItemSpec);
            Assert.Equal("targetpath", f.AppConfigFile.GetMetadata("TargetPath"));
        }

        [Fact]
        public void FoundInSecondBelowProjectDirectory()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            f.PrimaryList = new ITaskItem[] { new TaskItem("yyy"), new TaskItem("xxx") };
            f.SecondaryList = new ITaskItem[] { new TaskItem("foo\\app.config"), new TaskItem("xxx") };
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            Assert.Equal(FileUtilities.FixFilePath("foo\\app.config"), f.AppConfigFile.ItemSpec);
            Assert.Equal("targetpath", f.AppConfigFile.GetMetadata("TargetPath"));
        }

        [Fact]
        public void NotFound()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            f.PrimaryList = new ITaskItem[] { new TaskItem("yyy"), new TaskItem("xxx") };
            f.SecondaryList = new ITaskItem[] { new TaskItem("iii"), new TaskItem("xxx") };
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            Assert.Null(f.AppConfigFile);
        }

        [Fact]
        public void MatchFileNameOnlyWithAnInvalidPath()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            f.PrimaryList = new ITaskItem[] { new TaskItem("yyy"), new TaskItem("xxx") };
            f.SecondaryList = new ITaskItem[] { new TaskItem("|||"), new TaskItem(@"foo\\app.config"), new TaskItem(@"!@#$@$%|"), new TaskItem("uuu") };
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            // Should ignore the invalid paths
            Assert.Equal(FileUtilities.FixFilePath(@"foo\\app.config"), f.AppConfigFile.ItemSpec);
        }

        // For historical reasons, we should return the last one in the list
        [Fact]
        public void ReturnsLastOne()
        {
            FindAppConfigFile f = new FindAppConfigFile();
            f.BuildEngine = new MockEngine();
            ITaskItem item1 = new TaskItem("app.config");
            item1.SetMetadata("id", "1");
            ITaskItem item2 = new TaskItem("app.config");
            item2.SetMetadata("id", "2");
            f.PrimaryList = new ITaskItem[] { item1, item2 };
            f.SecondaryList = System.Array.Empty<ITaskItem>();
            f.TargetPath = "targetpath";
            Assert.True(f.Execute());
            Assert.Equal("app.config", f.AppConfigFile.ItemSpec);
            Assert.Equal(item2.GetMetadata("id"), f.AppConfigFile.GetMetadata("id"));
        }

        [Theory]
        [InlineData("foo/app.config", "Unix", true)]
        [InlineData("foo/app.config", "Windows_NT", true)]
        [InlineData(@"foo\app.config", "Unix", false)]
        [InlineData(@"foo\app.config", "Windows_NT", true)]
        public void DeterministicVariantUsesExplicitPathSemantics(
            string itemSpec,
            string hostOS,
            bool shouldFind)
        {
            FindAppConfigFileWithDeterministicSemantics task = new()
            {
                BuildEngine = new MockEngine(),
                PrimaryList = [new TaskItem(itemSpec, treatAsFilePath: false)],
                SecondaryList = [],
                TargetPath = "targetpath",
                HostOS = hostOS,
            };

            task.Execute().ShouldBeTrue();
            if (shouldFind)
            {
                task.AppConfigFile.ItemSpec.ShouldBe(itemSpec);
                task.AppConfigFile.GetMetadata("TargetPath").ShouldBe("targetpath");
            }
            else
            {
                task.AppConfigFile.ShouldBeNull();
            }
        }

        [Fact]
        public void DeterministicVariantClonesSelectedItem()
        {
            TaskItem input = new("app.config");
            input.SetMetadata("CustomMetadata", "value");
            FindAppConfigFileWithDeterministicSemantics task = new()
            {
                BuildEngine = new MockEngine(),
                PrimaryList = [input],
                SecondaryList = [],
                TargetPath = "targetpath",
                HostOS = "Unix",
            };

            task.Execute().ShouldBeTrue();
            task.AppConfigFile.ShouldNotBeSameAs(input);
            task.AppConfigFile.GetMetadata("CustomMetadata").ShouldBe("value");
            task.AppConfigFile.GetMetadata("OriginalItemSpec").ShouldBe(input.ItemSpec);
            task.AppConfigFile.GetMetadata("TargetPath").ShouldBe("targetpath");
            input.GetMetadata("TargetPath").ShouldBeEmpty();
        }

        [Fact]
        public void LegacyTaskMutatesSelectedItem()
        {
            TaskItem input = new("app.config");
            FindAppConfigFile task = new()
            {
                BuildEngine = new MockEngine(),
                PrimaryList = [input],
                SecondaryList = [],
                TargetPath = "targetpath",
            };

            task.Execute().ShouldBeTrue();
            task.AppConfigFile.ShouldBeSameAs(input);
            input.GetMetadata("TargetPath").ShouldBe("targetpath");
        }
    }
}
