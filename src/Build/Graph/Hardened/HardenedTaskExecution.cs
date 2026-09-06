// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using System.Collections.Generic;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Collections;
using Microsoft.Build.Execution;

#nullable disable

namespace Microsoft.Build.Graph.Hardened;

internal delegate void HardenedPureTaskExecutor(
    ProjectTargetInstance target,
    ProjectTaskInstance task,
    Lookup lookup);

internal static class HardenedTaskClassifications
{
    internal static FrozenDictionary<string, HardenedTaskClassification> BuiltIn { get; } =
        new Dictionary<string, HardenedTaskClassification>(MSBuildNameIgnoreCaseComparer.Default)
        {
            ["AssignTargetPathWithProjectDirectory"] = HardenedTaskClassification.Pure,
        }.ToFrozenDictionary(MSBuildNameIgnoreCaseComparer.Default);
}
