// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

using Microsoft.Build.Framework;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests
{
    public class AttributeTests
    {
        /// <summary>
        /// Test RequiredRuntimeAttribute
        /// </summary>
        [Fact]
        public void RequiredRuntimeAttribute()
        {
            RequiredRuntimeAttribute attribute =
                typeof(X).GetCustomAttribute<RequiredRuntimeAttribute>();

            attribute.RuntimeVersion.ShouldBe("v5");
        }

        [Fact]
        public void OutputAttribute()
        {
            OutputAttribute attribute =
                typeof(X).GetMember("TestValue2", BindingFlags.NonPublic | BindingFlags.Static)[0].GetCustomAttribute<OutputAttribute>();
            attribute.ShouldNotBeNull();
        }

        [Fact]
        public void RequiredAttribute()
        {
            RequiredAttribute attribute =
                typeof(X).GetMember("TestValue", BindingFlags.NonPublic | BindingFlags.Static)[0].GetCustomAttribute<RequiredAttribute>();
            attribute.ShouldNotBeNull();
        }

        [Fact]
        public void MSBuildPureTaskAttributeIsNotInherited()
        {
            typeof(PureTask).GetCustomAttribute<MSBuildPureTaskAttribute>().ShouldNotBeNull();
            typeof(DerivedTask).GetCustomAttribute<MSBuildPureTaskAttribute>().ShouldBeNull();
        }

        [Fact]
        public void MSBuildDeclaredIOAttributesDescribePaths()
        {
            MSBuildDeclaredIOTaskAttribute task =
                typeof(DeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOTaskAttribute>();
            MSBuildDeclaredIORequiresUnsetAttribute requiresUnset =
                typeof(DeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIORequiresUnsetAttribute>();
            MSBuildDeclaredIOInputAttribute input =
                typeof(DeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOInputAttribute>();
            MSBuildDeclaredIOOutputAttribute output =
                typeof(DeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOOutputAttribute>();

            task.ShouldNotBeNull();
            requiresUnset.ShouldNotBeNull();
            requiresUnset.ParameterName.ShouldBe("LegacyDirectory");
            input.ShouldNotBeNull();
            input.ParameterName.ShouldBe("Source");
            output.ShouldNotBeNull();
            output.ParameterName.ShouldBe("Destination");
            typeof(DerivedDeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOTaskAttribute>()
                .ShouldBeNull();
            typeof(DerivedDeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIORequiresUnsetAttribute>()
                .ShouldBeNull();
            typeof(DerivedDeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOInputAttribute>()
                .ShouldBeNull();
            typeof(DerivedDeclaredIOTask).GetCustomAttribute<MSBuildDeclaredIOOutputAttribute>()
                .ShouldBeNull();
        }
    }

    /// <summary>
    /// Sample class with RequiredRuntimeAttribute on it
    /// </summary>
    [RequiredRuntime("v5")]
    internal static class X
    {
        [Required]
        internal static bool TestValue
        {
            get
            {
                return true;
            }
        }

        [Output]
        internal static bool TestValue2
        {
            get
            {
                return true;
            }
        }
    }

    [MSBuildPureTask]
    internal class PureTask;

    internal sealed class DerivedTask : PureTask;

    [MSBuildDeclaredIOTask]
    [MSBuildDeclaredIORequiresUnset("LegacyDirectory")]
    [MSBuildDeclaredIOInput("Source")]
    [MSBuildDeclaredIOOutput("Destination")]
    internal class DeclaredIOTask;

    internal sealed class DerivedDeclaredIOTask : DeclaredIOTask;
}
