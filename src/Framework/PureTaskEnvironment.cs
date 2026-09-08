// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.ComponentModel;

namespace Microsoft.Build.Framework;

/// <summary>
/// Immutable engine-supplied inputs that a Pure task may observe in addition to its parameters.
/// </summary>
public sealed class PureTaskEnvironment
{
    private PureTaskEnvironment(AbsolutePath projectDirectory, bool usesWindowsPathSemantics)
    {
        ProjectDirectory = projectDirectory;
        UsesWindowsPathSemantics = usesWindowsPathSemantics;
    }

    /// <summary>
    /// Gets an environment snapshot for a task instantiated outside the MSBuild engine.
    /// </summary>
    public static PureTaskEnvironment Fallback
        => Create(TaskEnvironment.Fallback.ProjectDirectory, NativeMethods.IsWindows);

    /// <summary>
    /// Gets the project directory against which relative task paths are resolved.
    /// </summary>
    public AbsolutePath ProjectDirectory { get; }

    /// <summary>
    /// Gets a value indicating whether local source paths use Windows path semantics.
    /// </summary>
    public bool UsesWindowsPathSemantics { get; }

    /// <summary>
    /// Creates an immutable Pure task environment for testing tasks outside the MSBuild engine.
    /// </summary>
    /// <returns>An immutable Pure task environment using the current host's path semantics.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static PureTaskEnvironment CreateWithProjectDirectory(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectDirectory);

        return Create(
            new AbsolutePath(projectDirectory).GetCanonicalForm(),
            NativeMethods.IsWindows);
    }

    /// <summary>
    /// Converts a relative or absolute path to an absolute path resolved against
    /// <see cref="ProjectDirectory"/>.
    /// </summary>
    /// <returns>The absolute path.</returns>
    public AbsolutePath GetAbsolutePath(string path)
    {
        return new AbsolutePath(path, ProjectDirectory);
    }

    internal static PureTaskEnvironment Create(TaskEnvironment taskEnvironment)
    {
        ArgumentNullException.ThrowIfNull(taskEnvironment);

        return Create(taskEnvironment.ProjectDirectory);
    }

    internal static PureTaskEnvironment Create(AbsolutePath projectDirectory)
        => Create(projectDirectory, NativeMethods.IsWindows);

    internal static PureTaskEnvironment Create(
        AbsolutePath projectDirectory,
        bool usesWindowsPathSemantics)
    {
        return new PureTaskEnvironment(projectDirectory, usesWindowsPathSemantics);
    }
}
