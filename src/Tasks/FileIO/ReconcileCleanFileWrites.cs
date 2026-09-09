// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Reconciles prior and current file writes with files deleted during clean.
    /// </summary>
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class ReconcileCleanFileWrites : TaskExtension, IIncrementalTask, IMultiThreadableTask
    {
        /// <inheritdoc />
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// File that stores the clean ledger.
        /// </summary>
        [Required]
        public ITaskItem CleanFile { get; set; }

        /// <summary>
        /// Files recorded by prior builds.
        /// </summary>
        public ITaskItem[] PriorFileWrites { get; set; } = [];

        /// <summary>
        /// Files produced by the current build.
        /// </summary>
        public ITaskItem[] CurrentFileWrites { get; set; } = [];

        /// <summary>
        /// Files successfully processed by the preceding delete operation.
        /// </summary>
        public ITaskItem[] DeletedFiles { get; set; } = [];

        /// <summary>
        /// Exact file paths whose pre-invocation state may affect this invocation.
        /// </summary>
        public ITaskItem[] DeclaredInputs { get; set; }

        /// <summary>
        /// Exact persistent file paths that this invocation may create or modify.
        /// </summary>
        public ITaskItem[] DeclaredOutputs { get; set; }

        /// <inheritdoc />
        public bool FailIfNotIncremental { get; set; }

        /// <inheritdoc cref="ITask.Execute" />
        public override bool Execute()
        {
            ArgumentNullException.ThrowIfNull(CleanFile);

            HashSet<string> deletedFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (ITaskItem deletedFile in DeletedFiles)
            {
                deletedFiles.Add(deletedFile.ItemSpec);
            }

            HashSet<string> seenFileWrites = new(StringComparer.OrdinalIgnoreCase);
            List<ITaskItem> remainingFileWrites = new(PriorFileWrites.Length + CurrentFileWrites.Length);
            AddRemainingFileWrites(PriorFileWrites, deletedFiles, seenFileWrites, remainingFileWrites);
            AddRemainingFileWrites(CurrentFileWrites, deletedFiles, seenFileWrites, remainingFileWrites);

            WriteLinesToFile writeCleanFile = new()
            {
                BuildEngine = BuildEngine,
                TaskEnvironment = TaskEnvironment,
                File = CleanFile,
                Lines = remainingFileWrites.ToArray(),
                Overwrite = true,
                WriteOnlyWhenDifferent = true,
                FailIfNotIncremental = FailIfNotIncremental,
            };

            return writeCleanFile.Execute();
        }

        private static void AddRemainingFileWrites(
            ITaskItem[] fileWrites,
            HashSet<string> deletedFiles,
            HashSet<string> seenFileWrites,
            List<ITaskItem> remainingFileWrites)
        {
            foreach (ITaskItem fileWrite in fileWrites)
            {
                if (!deletedFiles.Contains(fileWrite.ItemSpec) && seenFileWrites.Add(fileWrite.ItemSpec))
                {
                    remainingFileWrites.Add(fileWrite);
                }
            }
        }
    }
}
