// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Microsoft.Build.Framework;

/// <summary>
/// Provides a Pure task with the immutable execution environment that is part of its input.
/// </summary>
/// <remarks>
/// Implementing this interface does not classify a task as Pure. The concrete task type must
/// also have <see cref="MSBuildPureTaskAttribute"/>.
/// </remarks>
public interface IPureTask : ITask
{
    /// <summary>
    /// Gets or sets the immutable execution environment supplied by MSBuild.
    /// </summary>
    /// <remarks>
    /// MSBuild sets this property before executing the task. Task implementations should not
    /// replace it during execution.
    /// </remarks>
    PureTaskEnvironment PureTaskEnvironment { get; set; }
}
