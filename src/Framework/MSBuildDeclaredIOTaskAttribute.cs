// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;

namespace Microsoft.Build.Framework;

/// <summary>
/// Marks a task class whose external filesystem effects are completely described by its declared-I/O annotations.
/// </summary>
/// <remarks>
/// Input and output paths are described with <see cref="MSBuildDeclaredIOInputAttribute"/> and
/// <see cref="MSBuildDeclaredIOOutputAttribute"/>.
/// Invocation constraints are described with <see cref="MSBuildDeclaredIORequiresUnsetAttribute"/>.
/// MSBuild detects this attribute by its namespace and name only, ignoring the defining assembly.
/// This allows task authors to define a compatible attribute alongside tasks that target older versions
/// of Microsoft.Build.Framework. Compatible definitions must also specify <c>Inherited = false</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MSBuildDeclaredIOTaskAttribute : Attribute;
