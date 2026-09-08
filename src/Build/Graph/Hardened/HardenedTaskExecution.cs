// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

internal delegate HardenedTaskDescriptor HardenedTaskClassifier(
    ProjectTaskInstance task,
    TaskHostParameters taskIdentityParameters);

internal sealed class HardenedTaskDescriptor
{
    internal static readonly HardenedTaskDescriptor Pure =
        new(HardenedTaskClassification.Pure);

    internal static readonly HardenedTaskDescriptor Unaudited =
        new(HardenedTaskClassification.Unaudited);

    internal HardenedTaskDescriptor(
        HardenedTaskClassification classification,
        IReadOnlyList<string> requiredUnsetParameters = null,
        IReadOnlyList<string> inputPathParameters = null,
        IReadOnlyList<string> outputPathParameters = null)
    {
        Classification = classification;
        RequiredUnsetParameters = requiredUnsetParameters ?? [];
        InputPathParameters = inputPathParameters ?? [];
        OutputPathParameters = outputPathParameters ?? [];
    }

    internal HardenedTaskClassification Classification { get; }

    internal IReadOnlyList<string> RequiredUnsetParameters { get; }

    internal IReadOnlyList<string> InputPathParameters { get; }

    internal IReadOnlyList<string> OutputPathParameters { get; }

    internal static HardenedTaskDescriptor FromClassification(HardenedTaskClassification classification)
        => classification switch
        {
            HardenedTaskClassification.Pure => Pure,
            HardenedTaskClassification.Unaudited => Unaudited,
            _ => new HardenedTaskDescriptor(classification),
        };
}

internal static class HardenedTaskClassificationResolver
{
    private static readonly ConditionalWeakTable<LoadedType, HardenedTaskDescriptor> s_descriptors = new();

    internal static HardenedTaskDescriptor Classify(
        ProjectInstance project,
        LoggingContext loggingContext,
        ProjectTaskInstance task,
        TaskHostParameters taskIdentityParameters)
    {
        LoadedType loadedType;
        if (TaskClassRegistry.TryGetRegistration(task.Name, out TaskClassRegistration registration))
        {
            return registration.TryGetLoadedTypeWithoutConstruction(out loadedType)
                ? Describe(loadedType)
                : HardenedTaskDescriptor.Unaudited;
        }

        if (!FeatureSwitches.EnableReflectiveTaskExecution)
        {
            return HardenedTaskDescriptor.Unaudited;
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

        return resolved && loadedType is not null
            ? Describe(loadedType)
            : HardenedTaskDescriptor.Unaudited;
    }

    private static HardenedTaskDescriptor Describe(LoadedType loadedType)
        => s_descriptors.GetValue(loadedType, CreateDescriptor);

    private static HardenedTaskDescriptor CreateDescriptor(LoadedType loadedType)
    {
        if (loadedType.HasMSBuildPureTaskAttribute)
        {
            return HardenedTaskDescriptor.Pure;
        }

        if (!loadedType.HasMSBuildDeclaredIOTaskAttribute ||
            !loadedType.HasValidMSBuildDeclaredIOAttributes)
        {
            return HardenedTaskDescriptor.Unaudited;
        }

        IReadOnlyList<DeclaredIOPathParameter> loadedInputs =
            loadedType.DeclaredIOInputPathParameters;
        IReadOnlyList<DeclaredIOPathParameter> loadedOutputs =
            loadedType.DeclaredIOOutputPathParameters;
        if (loadedInputs.Count == 0 &&
            loadedOutputs.Count == 0 &&
            loadedType.DeclaredIORequiredUnsetParameters.Count == 0)
        {
            return new HardenedTaskDescriptor(HardenedTaskClassification.DeclaredIO);
        }

        var inputs = new string[loadedInputs.Count];
        for (int i = 0; i < loadedInputs.Count; i++)
        {
            inputs[i] = loadedInputs[i].ParameterName;
        }

        var outputs = new string[loadedOutputs.Count];
        for (int i = 0; i < loadedOutputs.Count; i++)
        {
            outputs[i] = loadedOutputs[i].ParameterName;
        }

        return new HardenedTaskDescriptor(
            HardenedTaskClassification.DeclaredIO,
            loadedType.DeclaredIORequiredUnsetParameters,
            inputs,
            outputs);
    }
}
