// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace Microsoft.Build.Framework;

/// <summary>
/// Declares that a task parameter contains file paths read by an
/// <see cref="MSBuildDeclaredIOTaskAttribute"/> task.
/// </summary>
/// <remarks>
/// MSBuild detects this attribute by its namespace and name only, ignoring the defining assembly.
/// Compatible definitions must use the same constructor and specify <c>Inherited = false</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class MSBuildDeclaredIOInputAttribute(string parameterName) : Attribute
{
    /// <summary>
    /// Gets the task parameter containing the declared input paths.
    /// </summary>
    public string ParameterName { get; } = parameterName;
}
