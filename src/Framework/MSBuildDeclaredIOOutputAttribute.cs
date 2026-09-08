// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace Microsoft.Build.Framework;

/// <summary>
/// Declares that a task parameter contains file paths written by an
/// <see cref="MSBuildDeclaredIOTaskAttribute"/> task.
/// </summary>
/// <remarks>
/// The parameter value supplied to the task must identify the exact output paths. If the parameter
/// is also a task output, a successful invocation must return the same path values.
///
/// MSBuild detects this attribute by its namespace and name only, ignoring the defining assembly.
/// Compatible definitions must use the same constructor and specify <c>Inherited = false</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MSBuildDeclaredIOOutputAttribute(string parameterName) : Attribute
{
    /// <summary>
    /// Gets the task parameter containing the declared output paths.
    /// </summary>
    public string ParameterName { get; } = parameterName;
}
