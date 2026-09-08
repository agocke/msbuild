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
    [MSBuildPureTask]
    public class AssignLinkMetadata : TaskExtension, IPureTask
    {
        private PureTaskEnvironment _pureTaskEnvironment;

        /// <summary>
        /// The set of items to assign metadata to
        /// </summary>
        public ITaskItem[] Items { get; set; }

        /// <summary>
        /// The set of items to which the Link metadata has been set
        /// </summary>
        [Output]
        public ITaskItem[] OutputItems { get; set; }

        /// <inheritdoc />
        public PureTaskEnvironment PureTaskEnvironment
        {
            get => _pureTaskEnvironment ??= Microsoft.Build.Framework.PureTaskEnvironment.Fallback;
            set => _pureTaskEnvironment = value;
        }

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
            if (PureTaskEnvironment.UsesWindowsPathSemantics)
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
                if (PureTaskEnvironment.UsesWindowsPathSemantics)
                {
                    path = path.Replace('/', '\\');
                }

                return Path.GetFullPath(PureTaskEnvironment.GetAbsolutePath(path));
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                throw new InvalidOperationException(e.Message, e);
            }
        }
    }
}
