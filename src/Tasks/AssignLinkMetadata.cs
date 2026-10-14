// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Task to assign a reasonable "Link" metadata to the provided items.
    /// </summary>
    [MSBuildMultiThreadableTask]
    [MSBuildDeclaredIOTask]
    public class AssignLinkMetadata : TaskExtension, IMultiThreadableTask
    {
        /// <summary>
        /// Gets or sets the declared file inputs, which are empty for this zero-I/O task.
        /// </summary>
        public ITaskItem[] DeclaredInputs { get; set; } = [];

        /// <summary>
        /// Gets or sets the declared file outputs, which are empty for this zero-I/O task.
        /// </summary>
        public ITaskItem[] DeclaredOutputs { get; set; } = [];

        /// <summary>
        /// The set of items to assign metadata to
        /// </summary>
        public ITaskItem[] Items { get; set; }

        /// <summary>
        /// The set of items to which the Link metadata has been set
        /// </summary>
        [Output]
        public ITaskItem[] OutputItems { get; set; }

        /// <summary>
        /// Gets or sets the task execution environment for thread-safe path resolution.
        /// </summary>
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// Sets "Link" metadata on any item where the project file in which they
        /// were defined is different from the parent project file to a sane default:
        /// the relative directory compared to the defining file.
        ///
        /// Does NOT overwrite Link metadata if it's already defined.
        /// </summary>
        public override bool Execute()
        {
            var outputItems = new List<ITaskItem>();

            if (Items != null)
            {
                foreach (ITaskItem item in Items)
                {
                    try
                    {
                        string definingProject = item.GetMetadata(ItemSpecModifiers.DefiningProjectFullPath);
                        string definingProjectDirectory = GetDirectory(definingProject);
                        string fullPath = GetFullPath(item.ItemSpec);

                        if (
                                String.IsNullOrEmpty(item.GetMetadata("Link"))
                                && !String.IsNullOrEmpty(definingProject)
                                && fullPath.StartsWith(definingProjectDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            string link = fullPath.Substring(definingProjectDirectory.Length);
                            ITaskItem outputItem = new TaskItem(item);
                            outputItem.SetMetadata("Link", link);

                            outputItems.Add(outputItem);
                        }
                    }
                    catch (InvalidOperationException e)
                    {
                        // can happen if the item is not a proper path
                        Log.LogWarningFromException(e);
                    }
                }
            }

            OutputItems = outputItems.ToArray();
            return !Log.HasLoggedErrors;
        }

        private string GetDirectory(string path)
        {
            int separator = path.LastIndexOf('/');
            if (NativeMethodsShared.IsWindows)
            {
                separator = Math.Max(separator, path.LastIndexOf('\\'));
            }

            return separator < 0
                ? String.Empty
                : path.Substring(0, separator + 1);
        }

        private string GetFullPath(string path)
        {
            try
            {
                if (NativeMethodsShared.IsWindows)
                {
                    path = path.Replace('/', '\\');
                }

#pragma warning disable MSBuildTask0002 // TaskEnvironment resolves the path; GetFullPath only canonicalizes it.
                return Path.GetFullPath(TaskEnvironment.GetAbsolutePath(path));
#pragma warning restore MSBuildTask0002
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                throw new InvalidOperationException(e.Message, e);
            }
        }
    }
}
