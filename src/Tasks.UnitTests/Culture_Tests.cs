// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Tasks;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public sealed class Culture_Tests
    {
        /*
        * Method:   Basic
        *
        * Test the basic functionality.
        */
        [Fact]
        public void Basic()
        {
            Culture.ItemCultureInfo info = Culture.GetItemCultureInfo("MyResource.fr.resx", null);
            Assert.Equal("fr", info.culture);
            Assert.Equal("MyResource.resx", info.cultureNeutralFilename);
        }

        /*
        * Method:   NonCultureFile
        *
        * The item doesn't have a culture, and there isn't one embedded in the file name.
        */
        [Fact]
        public void NonCultureFile()
        {
            Culture.ItemCultureInfo info = Culture.GetItemCultureInfo("MyResource.resx", null);
            Assert.Null(info.culture);
            Assert.Equal("MyResource.resx", info.cultureNeutralFilename);
        }


        /*
        * Method:   BogusEmbeddedCulture
        *
        * The item has something that looks like an embedded culture, but isn't.
        */
        [Fact]
        [Trait("Category", "netcore-osx-failing")]
        [Trait("Category", "netcore-linux-failing")]
        public void BogusEmbeddedCulture()
        {
            Culture.ItemCultureInfo info = Culture.GetItemCultureInfo("MyResource.notalocale.resx", null);
            Assert.Null(info.culture);
            Assert.Equal("MyResource.notalocale.resx", info.cultureNeutralFilename);
        }

        [Theory]
        [InlineData("directory/MyResource.fr.resx", "directory/MyResource.resx")]
        [InlineData(@"directory\MyResource.fr.resx", @"directory\MyResource.resx")]
        [InlineData(@"directory\mixed/MyResource.fr.resx", @"directory\mixed/MyResource.resx")]
        public void DeterministicPathSemanticsPreserveSeparators(string name, string expected)
        {
            Culture.ItemCultureInfo info = Culture.GetItemCultureInfoDeterministic(name, null);

            info.culture.ShouldBe("fr");
            info.cultureNeutralFilename.ShouldBe(expected);
        }

        [Fact]
        public void DeterministicPathSemanticsDoNotValidateHostPathCharacters()
        {
            const string name = "directory:with*host?characters/MyResource.fr.resx";

            Culture.ItemCultureInfo info = Culture.GetItemCultureInfoDeterministic(name, null);

            info.culture.ShouldBe("fr");
            info.cultureNeutralFilename.ShouldBe("directory:with*host?characters/MyResource.resx");
        }

        [Fact]
        public void DeterministicPathSemanticsDoNotTreatDotFilePrefixAsCultureSeparator()
        {
            const string name = "directory/.fr.resx";

            Culture.ItemCultureInfo info = Culture.GetItemCultureInfoDeterministic(name, null);

            info.culture.ShouldBeNull();
            info.cultureNeutralFilename.ShouldBe(name);
        }

        [Theory]
        [InlineData("other/MyResource.fr.cs")]
        [InlineData(@"other\MyResource.fr.cs")]
        public void DeterministicDependentUponComparisonUsesBothSeparators(string dependentUpon)
        {
            const string name = @"directory\MyResource.fr.resx";

            Culture.ItemCultureInfo info =
                Culture.GetItemCultureInfoDeterministic(name, dependentUpon);

            info.culture.ShouldBeNull();
            info.cultureNeutralFilename.ShouldBe(name);
        }
    }
}
