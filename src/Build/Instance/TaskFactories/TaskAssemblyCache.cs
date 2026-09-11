// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Shares immutable task-type metadata for ordinary in-process assembly-file registrations.
    /// </summary>
    internal static class TaskAssemblyCache
    {
        private static readonly ConcurrentDictionary<string, AssemblyEntry> s_assemblies =
            new(FileUtilities.PathComparer);

        [RequiresUnreferencedCode("Loads task assemblies discovered at runtime, which is incompatible with trimming.")]
        internal static LoadedType GetOrLoad(
            TypeLoader typeLoader,
            string taskName,
            AssemblyLoadInfo assemblyLoadInfo,
            bool useTaskHost,
            bool taskHostParamsMatchCurrentProc,
            TypeLoader.LogWarningDelegate logWarning)
        {
            ArgumentNullException.ThrowIfNull(typeLoader);
            ArgumentNullException.ThrowIfNull(assemblyLoadInfo);

            if (assemblyLoadInfo.AssemblyFile is null ||
                assemblyLoadInfo.IsInlineTask ||
                useTaskHost ||
                !taskHostParamsMatchCurrentProc)
            {
                return typeLoader.Load(taskName, assemblyLoadInfo, logWarning, useTaskHost, taskHostParamsMatchCurrentProc);
            }

            string normalizedPath = FileUtilities.NormalizePath(assemblyLoadInfo.AssemblyFile);
            AssemblyEntry entry = s_assemblies.GetOrAdd(normalizedPath, static path => new AssemblyEntry(path));
            return entry.GetOrLoad(typeLoader, taskName, logWarning);
        }

        private sealed class AssemblyEntry
        {
            private readonly AssemblyLoadInfo _assemblyLoadInfo;
            private readonly ConcurrentDictionary<string, LoadedType> _taskNames =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly ConcurrentDictionary<Type, LoadedType> _taskTypes = new();

            internal AssemblyEntry(string normalizedPath)
            {
                _assemblyLoadInfo = AssemblyLoadInfo.Create(assemblyName: null, normalizedPath);
            }

            [RequiresUnreferencedCode("Loads task assemblies discovered at runtime, which is incompatible with trimming.")]
            internal LoadedType GetOrLoad(
                TypeLoader typeLoader,
                string taskName,
                TypeLoader.LogWarningDelegate logWarning)
            {
                if (_taskNames.TryGetValue(taskName, out LoadedType cached))
                {
                    return cached;
                }

                LoadedType loaded = typeLoader.Load(taskName, _assemblyLoadInfo, logWarning);
                if (loaded is null ||
                    loaded.LoadedViaMetadataLoadContext ||
                    typeof(IGeneratedTask).IsAssignableFrom(loaded.Type))
                {
                    return loaded;
                }

                LoadedType canonical = _taskTypes.GetOrAdd(loaded.Type, loaded);
                _taskNames.TryAdd(taskName, canonical);
                return canonical;
            }
        }
    }
}
