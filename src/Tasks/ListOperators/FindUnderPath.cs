// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

using Microsoft.NET.StringTools;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Given a list of items, determine which are in the cone of the folder passed in and which aren't.
    /// </summary>
    [MSBuildMultiThreadableTask]
    public class FindUnderPath : TaskExtension, IMultiThreadableTask
    {
        /// <summary>
        /// Gets or sets the task execution environment for thread-safe path resolution.
        /// </summary>
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// Filter based on whether items fall under this path or not.
        /// </summary>
        [Required]
        public ITaskItem Path { get; set; }

        /// <summary>
        /// Files to consider.
        /// </summary>
        public ITaskItem[] Files { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>
        /// Set to true if the paths of the output items should be updated to be absolute
        /// </summary>
        public bool UpdateToAbsolutePaths { get; set; }

        /// <summary>
        /// Files that were inside of Path.
        /// </summary>
        [Output]
        public ITaskItem[] InPath { get; set; }

        /// <summary>
        /// Files that were outside of Path.
        /// </summary>
        [Output]
        public ITaskItem[] OutOfPath { get; set; }

        /// <summary>
        /// Execute the task.
        /// </summary>
        public override bool Execute()
        {
            var inPathList = new List<ITaskItem>();
            var outOfPathList = new List<ITaskItem>();
            bool useCanonicalPathSemantics = UseCanonicalPathSemantics;

            string conePath;

            try
            {
                AbsolutePath absoluteConePath =
                    new(FileUtilities.FixFilePath(Path.ItemSpec), GetProjectDirectory());
                conePath =
                    Strings.WeakIntern(
                        useCanonicalPathSemantics
                            ? absoluteConePath.GetCanonicalForm()
                            :
#pragma warning disable MSBuildTask0002 // Path is already absolute from TaskEnvironment.GetAbsolutePath; GetFullPath only canonicalizes. Guarded by ChangeWave.
                            System.IO.Path.GetFullPath(absoluteConePath));
#pragma warning restore MSBuildTask0002

                conePath = FileUtilities.EnsureTrailingSlash(conePath);
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources(null, "", 0, 0, 0, 0,
                    "FindUnderPath.InvalidParameter", "Path", Path.ItemSpec, e.Message);
                return false;
            }

            int conePathLength = conePath.Length;

            Log.LogMessageFromResources(MessageImportance.Low, "FindUnderPath.ComparisonPath", Path.ItemSpec);

            foreach (ITaskItem item in Files)
            {
                string fullPath;
                try
                {
                    AbsolutePath absolutePath =
                        new(FileUtilities.FixFilePath(item.ItemSpec), GetProjectDirectory());
                    fullPath =
                        Strings.WeakIntern(
                            useCanonicalPathSemantics
                                ? absolutePath.GetCanonicalForm()
                                :
#pragma warning disable MSBuildTask0002 // Path is already absolute from TaskEnvironment.GetAbsolutePath; GetFullPath only canonicalizes. Guarded by ChangeWave.
                                System.IO.Path.GetFullPath(absolutePath));
#pragma warning restore MSBuildTask0002
                }
                catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                {
                    Log.LogErrorWithCodeFromResources(null, "", 0, 0, 0, 0,
                        "FindUnderPath.InvalidParameter", "Files", item.ItemSpec, e.Message);
                    return false;
                }

                // Compare the left side of both strings to see if they're equal.
                ITaskItem outputItem = CreateOutputItem(item);
                if (String.Compare(conePath, 0, fullPath, 0, conePathLength, PathComparison) == 0)
                {
                    // If we should use the absolute path, update the item contents
                    // Since ItemSpec, which fullPath comes from, is unescaped, re-escape when setting
                    // item.ItemSpec, since the setter for ItemSpec expects an escaped value.
                    if (UpdateToAbsolutePaths)
                    {
                        outputItem.ItemSpec = EscapingUtilities.Escape(fullPath);
                    }

                    inPathList.Add(outputItem);
                }
                else
                {
                    outOfPathList.Add(outputItem);
                }
            }

            InPath = inPathList.ToArray();
            OutOfPath = outOfPathList.ToArray();
            return true;
        }

        private protected virtual AbsolutePath GetProjectDirectory()
        {
            return TaskEnvironment.ProjectDirectory;
        }

        private protected virtual bool UseCanonicalPathSemantics
            => ChangeWaves.AreFeaturesEnabled(ChangeWaves.Wave18_5);

        private protected virtual StringComparison PathComparison
            => StringComparison.OrdinalIgnoreCase;

        private protected virtual ITaskItem CreateOutputItem(ITaskItem item)
        {
            return item;
        }
    }

    /// <summary>
    /// Finds items under a path using the engine-supplied Pure task environment and host path semantics.
    /// </summary>
    [MSBuildMultiThreadableTask]
    [MSBuildPureTask]
    public sealed class FindUnderPathWithDeterministicSemantics : FindUnderPath, IPureTask
    {
        private PureTaskEnvironment _pureTaskEnvironment;

        /// <inheritdoc />
        public PureTaskEnvironment PureTaskEnvironment
        {
            get => _pureTaskEnvironment ??= Microsoft.Build.Framework.PureTaskEnvironment.Fallback;
            set => _pureTaskEnvironment = value;
        }

        private protected override AbsolutePath GetProjectDirectory()
        {
            return PureTaskEnvironment.ProjectDirectory;
        }

        private protected override bool UseCanonicalPathSemantics => true;

        private protected override StringComparison PathComparison
            => PureTaskEnvironment.UsesWindowsPathSemantics
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private protected override ITaskItem CreateOutputItem(ITaskItem item)
        {
            return new TaskItem(item);
        }
    }
}
