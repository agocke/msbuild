// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Graph.Hardened;

internal delegate void HardenedPureTaskExecutor(
    ProjectTargetInstance target,
    ProjectTaskInstance task,
    Lookup lookup);

internal delegate HardenedTaskClassification HardenedTaskClassifier(
    ProjectTaskInstance task,
    TaskHostParameters taskIdentityParameters);

internal static class HardenedTaskClassificationResolver
{
    internal static HardenedTaskClassification Classify(
        ProjectInstance project,
        LoggingContext loggingContext,
        ProjectTaskInstance task,
        TaskHostParameters taskIdentityParameters)
    {
        LoadedType loadedType;
        if (TaskClassRegistry.TryGetRegistration(task.Name, out TaskClassRegistration registration))
        {
            return registration.TryGetLoadedTypeWithoutConstruction(out loadedType) &&
                loadedType.HasMSBuildPureTaskAttribute
                    ? HardenedTaskClassification.Pure
                    : HardenedTaskClassification.Unaudited;
        }

        if (!FeatureSwitches.EnableReflectiveTaskExecution)
        {
            return HardenedTaskClassification.Unaudited;
        }

        bool resolved = project.TaskRegistry.TryGetRegisteredTaskTypeForMetadata(
            task.Name,
            taskIdentityParameters,
            exactMatchRequired: true,
            loggingContext,
            out loadedType);
        if (!resolved)
        {
            resolved = project.TaskRegistry.TryGetRegisteredTaskTypeForMetadata(
                task.Name,
                taskIdentityParameters,
                exactMatchRequired: false,
                loggingContext,
                out loadedType);
        }

        return resolved && loadedType?.HasMSBuildPureTaskAttribute == true
            ? HardenedTaskClassification.Pure
            : HardenedTaskClassification.Unaudited;
    }
}
