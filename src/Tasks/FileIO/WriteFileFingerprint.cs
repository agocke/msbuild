// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Writes a deterministic fingerprint of a set of files without exposing a task output.
    /// </summary>
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class WriteFileFingerprint : TaskExtension, ICancelableTask, IMultiThreadableTask
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>
        /// Files whose paths and contents form the fingerprint.
        /// </summary>
        [Required]
        public ITaskItem[] Files { get; set; }

        /// <summary>
        /// File that stores the fingerprint.
        /// </summary>
        [Required]
        public ITaskItem File { get; set; }

        /// <summary>
        /// Exact file paths whose pre-invocation state may affect this invocation.
        /// </summary>
        public ITaskItem[] DeclaredInputs { get; set; }

        /// <summary>
        /// Exact persistent file paths that this invocation may create or modify.
        /// </summary>
        public ITaskItem[] DeclaredOutputs { get; set; }

        /// <inheritdoc />
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <inheritdoc cref="ITask.Execute" />
        public override bool Execute()
        {
            ArgumentNullException.ThrowIfNull(Files);
            ArgumentNullException.ThrowIfNull(File);

            List<string> filePaths = new(Files.Length);
            HashSet<string> seenPaths = new(FileUtilities.PathComparer);
            foreach (ITaskItem file in Files)
            {
                string path = FileUtilities.NormalizePath(TaskEnvironment.GetAbsolutePath(file.ItemSpec)).Value;
                if (seenPaths.Add(path))
                {
                    filePaths.Add(path);
                }
            }

            filePaths.Sort(FileUtilities.PathComparer);

            string currentFile = null;
            try
            {
                StringBuilder fingerprintInputs = new(filePaths.Count * 128);
                foreach (string filePath in filePaths)
                {
                    currentFile = filePath;
                    if (!FileUtilities.FileExistsNoThrow(filePath))
                    {
                        Log.LogErrorWithCodeFromResources("FileHash.FileNotFound", filePath);
                        return false;
                    }

                    byte[] fileHash = GetFileHash.ComputeHash(
                        SHA256.Create,
                        new AbsolutePath(filePath),
                        _cancellationTokenSource.Token);
                    fingerprintInputs
                        .Append(filePath.Length)
                        .Append(':')
                        .Append(filePath)
                        .Append(':')
                        .Append(GetFileHash.EncodeHash(HashEncoding.Hex, fileHash))
                        .Append('\n');
                }

                byte[] fingerprintBytes;
                using (SHA256 sha = SHA256.Create())
                {
                    fingerprintBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprintInputs.ToString()));
                }

                string fingerprint = GetFileHash.EncodeHash(HashEncoding.Hex, fingerprintBytes);
                WriteLinesToFile writeFingerprint = new()
                {
                    BuildEngine = BuildEngine,
                    TaskEnvironment = TaskEnvironment,
                    File = File,
                    Lines = [new Microsoft.Build.Utilities.TaskItem(fingerprint)],
                    Overwrite = true,
                    WriteOnlyWhenDifferent = true,
                };

                return writeFingerprint.Execute();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources(
                    "ReadLinesFromFile.ErrorOrWarning",
                    currentFile ?? File.ItemSpec,
                    e.Message);
                return false;
            }
        }

        /// <inheritdoc />
        public void Cancel()
        {
            _cancellationTokenSource.Cancel();
        }
    }
}
