// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace Microsoft.Build.Framework;

/// <summary>
/// Marks a task class whose outputs and observable behavior are determined entirely by its parameters
/// and, for an <see cref="IPureTask"/>, its <see cref="IPureTask.PureTaskEnvironment"/>.
/// </summary>
/// <remarks>
/// MSBuild detects this attribute by its namespace and name only, ignoring the defining assembly.
/// This allows task authors to define a compatible attribute alongside tasks that target older versions
/// of Microsoft.Build.Framework. Compatible definitions must also specify <c>Inherited = false</c>;
/// purity does not automatically extend to derived task classes.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MSBuildPureTaskAttribute : Attribute;
