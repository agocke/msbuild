// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework;
#if NETFRAMEWORK
using Path = Microsoft.IO.Path;
#else
using Path = System.IO.Path;
#endif

#nullable enable

namespace Microsoft.Build.Tasks;

[MSBuildMultiThreadableTask]
[MSBuildDeclaredIOTask]
public sealed class ValidateHardenedAssemblySearchPaths : TaskExtension, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public string[] SearchPaths { get; set; } = [];

    public override bool Execute()
    {
        foreach (string searchPath in SearchPaths)
        {
            if (string.Equals(searchPath, "{CandidateAssemblyFiles}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(searchPath, "{HintPathFromItem}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(searchPath, "{TargetFrameworkDirectory}", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathFullyQualified(searchPath))
            {
                continue;
            }

            Log.LogErrorWithCodeFromResources("CommonSdk.HardenedAssemblySearchPath", searchPath);
        }

        return !Log.HasLoggedErrors;
    }
}
