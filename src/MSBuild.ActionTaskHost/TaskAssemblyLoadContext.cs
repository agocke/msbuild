// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal sealed class TaskAssemblyLoadContext(string taskAssembly) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(taskAssembly);
    private readonly string _directory = Path.GetDirectoryName(taskAssembly)!;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, "Microsoft.Build", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(HostResources.Format("EngineDependencyNotSupported", taskAssembly));
        }

        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(ITask).Assembly.GetName()))
        {
            return typeof(ITask).Assembly;
        }

        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(TaskItem).Assembly.GetName()))
        {
            return typeof(TaskItem).Assembly;
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            string adjacentPath = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(adjacentPath))
            {
                path = adjacentPath;
            }
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
