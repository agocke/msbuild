// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Build.Internal;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// Default implementation of <see cref="ITaskEnvironmentDriver"/> that directly interacts with the file system
    /// and environment variables. Used in multi-process mode of execution.
    /// </summary>
    /// <remarks>
    /// Implemented as a singleton since it has no instance state.
    /// </remarks>
    internal sealed class MultiProcessTaskEnvironmentDriver : ITaskEnvironmentDriver
    {
        private readonly AsyncLocal<string?> _projectDirectoryOverride = new();

        /// <summary>
        /// The singleton instance.
        /// </summary>
        private static readonly MultiProcessTaskEnvironmentDriver s_instance = new MultiProcessTaskEnvironmentDriver();

        /// <summary>
        /// Gets the singleton instance of <see cref="MultiProcessTaskEnvironmentDriver"/>.
        /// </summary>
        public static MultiProcessTaskEnvironmentDriver Instance => s_instance;

        private MultiProcessTaskEnvironmentDriver() { }

        /// <inheritdoc/>
        public AbsolutePath ProjectDirectory
        {
            get => new AbsolutePath(
                _projectDirectoryOverride.Value ?? Environment.CurrentDirectory,
                ignoreRootedCheck: true);
            set => NativeMethods.SetCurrentDirectory(value.Value);
        }

        /// <inheritdoc/>
        public AbsolutePath GetAbsolutePath(string path)
        {
            return new AbsolutePath(path, ProjectDirectory);
        }

        /// <inheritdoc/>
        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name);
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, string> GetEnvironmentVariables()
        {
            return CommunicationsUtilities.GetEnvironmentVariables();
        }

        /// <inheritdoc/>
        public void SetEnvironmentVariable(string name, string? value)
        {
            CommunicationsUtilities.SetEnvironmentVariable(name, value);
        }

        /// <inheritdoc/>
        public void SetEnvironment(IDictionary<string, string> newEnvironment)
        {
            CommunicationsUtilities.SetEnvironment(newEnvironment);
        }

        /// <inheritdoc/>
        public ProcessStartInfo GetProcessStartInfo()
        {
            string? projectDirectory = _projectDirectoryOverride.Value;
            return projectDirectory is null
                ? new ProcessStartInfo()
                : new ProcessStartInfo
                {
                    WorkingDirectory = projectDirectory,
                };
        }

        internal IDisposable EnterProjectDirectoryScope(AbsolutePath projectDirectory)
        {
            string? previousProjectDirectory = _projectDirectoryOverride.Value;
            _projectDirectoryOverride.Value = projectDirectory.GetCanonicalForm().Value;
            return new ProjectDirectoryScope(this, previousProjectDirectory);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            // Singleton instance, no cleanup needed.
        }

        private sealed class ProjectDirectoryScope : IDisposable
        {
            private MultiProcessTaskEnvironmentDriver? _driver;
            private readonly string? _previousProjectDirectory;

            internal ProjectDirectoryScope(
                MultiProcessTaskEnvironmentDriver driver,
                string? previousProjectDirectory)
            {
                _driver = driver;
                _previousProjectDirectory = previousProjectDirectory;
            }

            public void Dispose()
            {
                MultiProcessTaskEnvironmentDriver? driver =
                    Interlocked.Exchange(ref _driver, null);
                if (driver is not null)
                {
                    driver._projectDirectoryOverride.Value =
                        _previousProjectDirectory;
                }
            }
        }
    }
}
