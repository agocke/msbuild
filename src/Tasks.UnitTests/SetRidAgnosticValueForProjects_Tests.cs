// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Tasks;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests
{
    public sealed class SetRidAgnosticValueForProjects_Tests
    {
        [Fact]
        public void IsMarkedPure()
        {
            Attribute.IsDefined(
                typeof(SetRidAgnosticValueForProjects),
                typeof(MSBuildPureTaskAttribute),
                inherit: false).ShouldBeTrue();
        }

        [Fact]
        public void EmptyProjectsProducesEmptyOutput()
        {
            SetRidAgnosticValueForProjects task = new();

            task.Execute().ShouldBeTrue();
            task.UpdatedProjects.ShouldBeEmpty();
        }
    }
}
