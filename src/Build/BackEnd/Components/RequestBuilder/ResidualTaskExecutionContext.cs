// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Lightweight build-engine context for partially evaluated in-process tasks.
    /// The general task host is created only when a task calls an API that needs it.
    /// </summary>
    internal sealed class ResidualTaskExecutionContext : IBuildEngine10
    {
        private static readonly object s_inactive = new();
        private static readonly bool s_breakOnLogAfterTaskReturns =
            Environment.GetEnvironmentVariable("MSBUILDBREAKONLOGAFTERTASKRETURNS") == "1";

        private IBuildComponentHost _host;
        private BuildRequestEntry _requestEntry;
        private ITargetBuilderCallback _targetBuilderCallback;
        private TaskLoggingContext _loggingContext;
        private object _taskHostState;
        private int _allowFailureWithoutError;
        private int _taskHostMaterialized;
        private int _taskEnvironmentState;
        private bool _buildRequestsSucceeded = true;

        private readonly ElementLocation _taskLocation;
        private readonly bool _continueOnError;
        private readonly bool _convertErrorsToWarnings;
        private readonly string _taskName;
        private readonly string _targetName;

        internal static Action<ResidualTaskExecutionContext> TestOnlyHookOnCreate { get; set; }

        internal ResidualTaskExecutionContext(
            IBuildComponentHost host,
            BuildRequestEntry requestEntry,
            ElementLocation taskLocation,
            ITargetBuilderCallback targetBuilderCallback,
            TaskLoggingContext loggingContext,
            bool continueOnError,
            bool convertErrorsToWarnings,
            string taskName,
            string targetName)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(requestEntry);
            Assumed.NotNull(taskLocation);
            ArgumentNullException.ThrowIfNull(loggingContext);

            _host = host;
            _requestEntry = requestEntry;
            _taskLocation = taskLocation;
            _targetBuilderCallback = targetBuilderCallback;
            _loggingContext = loggingContext;
            _continueOnError = continueOnError;
            _convertErrorsToWarnings = convertErrorsToWarnings;
            _taskName = taskName;
            _targetName = targetName;
            TestOnlyHookOnCreate?.Invoke(this);
        }

        internal bool BuildRequestsSucceeded
        {
            get
            {
                object state = Volatile.Read(ref _taskHostState);
                return state is TaskHost taskHost
                    ? taskHost.BuildRequestsSucceeded
                    : _buildRequestsSucceeded;
            }
        }

        internal bool TaskHostMaterialized => Volatile.Read(ref _taskHostMaterialized) != 0;

        internal bool TaskEnvironmentInitialized => Volatile.Read(ref _taskEnvironmentState) == 2;

        internal void MarkTaskEnvironmentInitialized()
        {
            Volatile.Write(ref _taskEnvironmentState, 2);
        }

        internal void EnsureTaskEnvironmentInitialized()
        {
            if (Volatile.Read(ref _taskEnvironmentState) == 2)
            {
                return;
            }

            var spinWait = new SpinWait();
            while (Interlocked.CompareExchange(ref _taskEnvironmentState, 1, 0) != 0)
            {
                if (Volatile.Read(ref _taskEnvironmentState) == 2)
                {
                    return;
                }

                spinWait.SpinOnce();
            }

            try
            {
                VerifyActive();
                if (_host.BuildParameters.SaveOperatingEnvironment)
                {
                    using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                               BuildExecutionMetric.FastTaskEnvironment,
                               _taskName,
                               _targetName))
                    {
                        _requestEntry.TaskEnvironment.ProjectDirectory =
                            new AbsolutePath(_requestEntry.ProjectRootDirectory, ignoreRootedCheck: true);
                    }
                }

                Volatile.Write(ref _taskEnvironmentState, 2);
            }
            catch
            {
                Volatile.Write(ref _taskEnvironmentState, 0);
                throw;
            }
        }

        public bool ContinueOnError
        {
            get
            {
                VerifyActive();
                return _continueOnError;
            }
        }

        public int LineNumberOfTaskNode => _taskLocation.Line;

        public int ColumnNumberOfTaskNode => _taskLocation.Column;

        public string ProjectFileOfTaskNode => _taskLocation.File;

        public bool IsRunningMultipleNodes => GetTaskHost().IsRunningMultipleNodes;

        public bool AllowFailureWithoutError
        {
            get => Volatile.Read(ref _allowFailureWithoutError) != 0;
            set
            {
                Volatile.Write(ref _allowFailureWithoutError, value ? 1 : 0);
                if (Volatile.Read(ref _taskHostState) is TaskHost taskHost)
                {
                    taskHost.AllowFailureWithoutError = value;
                }
            }
        }

        public EngineServices EngineServices => GetTaskHost().EngineServices;

        public void LogErrorEvent(BuildErrorEventArgs e)
        {
            if (TryGetTaskHostForLogging(e?.Message, out TaskHost taskHost))
            {
                taskHost.LogErrorEvent(e);
            }
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
            if (TryGetTaskHostForLogging(e?.Message, out TaskHost taskHost))
            {
                taskHost.LogWarningEvent(e);
            }
        }

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
            if (TryGetTaskHostForLogging(e?.Message, out TaskHost taskHost))
            {
                taskHost.LogMessageEvent(e);
            }
        }

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
            if (TryGetTaskHostForLogging(e?.Message, out TaskHost taskHost))
            {
                taskHost.LogCustomEvent(e);
            }
        }

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs) =>
            GetTaskHost().BuildProjectFile(
                projectFileName,
                targetNames,
                globalProperties,
                targetOutputs);

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs,
            string toolsVersion) =>
            GetTaskHost().BuildProjectFile(
                projectFileName,
                targetNames,
                globalProperties,
                targetOutputs,
                toolsVersion);

        public bool BuildProjectFilesInParallel(
            string[] projectFileNames,
            string[] targetNames,
            IDictionary[] globalProperties,
            IDictionary[] targetOutputsPerProject,
            string[] toolsVersion,
            bool useResultsCache,
            bool unloadProjectsOnCompletion) =>
            GetTaskHost().BuildProjectFilesInParallel(
                projectFileNames,
                targetNames,
                globalProperties,
                targetOutputsPerProject,
                toolsVersion,
                useResultsCache,
                unloadProjectsOnCompletion);

        public BuildEngineResult BuildProjectFilesInParallel(
            string[] projectFileNames,
            string[] targetNames,
            IDictionary[] globalProperties,
            IList<string>[] undefineProperties,
            string[] toolsVersion,
            bool returnTargetOutputs) =>
            GetTaskHost().BuildProjectFilesInParallel(
                projectFileNames,
                targetNames,
                globalProperties,
                undefineProperties,
                toolsVersion,
                returnTargetOutputs);

        public void Yield() => GetTaskHost().Yield();

        public void Reacquire() => GetTaskHost().Reacquire();

        public void RegisterTaskObject(
            object key,
            object obj,
            RegisteredTaskObjectLifetime lifetime,
            bool allowEarlyCollection) =>
            GetTaskHost().RegisterTaskObject(key, obj, lifetime, allowEarlyCollection);

        public object GetRegisteredTaskObject(
            object key,
            RegisteredTaskObjectLifetime lifetime) =>
            GetTaskHost().GetRegisteredTaskObject(key, lifetime);

        public object UnregisterTaskObject(
            object key,
            RegisteredTaskObjectLifetime lifetime) =>
            GetTaskHost().UnregisterTaskObject(key, lifetime);

        public void LogTelemetry(
            string eventName,
            IDictionary<string, string> properties)
        {
            if (TryGetTaskHostForLogging(eventName, out TaskHost taskHost))
            {
                taskHost.LogTelemetry(eventName, properties);
            }
        }

        public IReadOnlyDictionary<string, string> GetGlobalProperties() =>
            GetTaskHost().GetGlobalProperties();

        public bool ShouldTreatWarningAsError(string warningCode) =>
            GetTaskHost().ShouldTreatWarningAsError(warningCode);

        public int RequestCores(int requestedCores) =>
            GetTaskHost().RequestCores(requestedCores);

        public void ReleaseCores(int coresToRelease) =>
            GetTaskHost().ReleaseCores(coresToRelease);

        internal void MarkAsInactive()
        {
            object state = Interlocked.Exchange(ref _taskHostState, s_inactive);
            Assumed.False(ReferenceEquals(state, s_inactive));

            if (state is TaskHost taskHost)
            {
                _buildRequestsSucceeded = taskHost.BuildRequestsSucceeded;
                taskHost.MarkAsInactive();
            }

            _host = null;
            _requestEntry = null;
            _targetBuilderCallback = null;
            _loggingContext = null;
        }

        private TaskHost GetTaskHost()
        {
            if (TryGetOrCreateTaskHost(out TaskHost taskHost))
            {
                return taskHost;
            }

            return InternalError.Throw<TaskHost>(
                "Attempted to use an inactive residual task execution context.");
        }

        private bool TryGetTaskHostForLogging(string message, out TaskHost taskHost)
        {
            if (TryGetOrCreateTaskHost(out taskHost))
            {
                return true;
            }

            if (s_breakOnLogAfterTaskReturns)
            {
                Trace.Fail(
                    string.Format(
                        CultureInfo.CurrentUICulture,
                        "Task at {0}, after already returning, attempted to log '{1}'",
                        _taskLocation,
                        message));
            }

            return false;
        }

        private bool TryGetOrCreateTaskHost(out TaskHost taskHost)
        {
            object state = Volatile.Read(ref _taskHostState);
            if (state is TaskHost existing)
            {
                taskHost = existing;
                return true;
            }

            if (ReferenceEquals(state, s_inactive))
            {
                taskHost = null;
                return false;
            }

            IBuildComponentHost host = _host;
            BuildRequestEntry requestEntry = _requestEntry;
            ITargetBuilderCallback targetBuilderCallback = _targetBuilderCallback;
            TaskLoggingContext loggingContext = _loggingContext;
            if (host == null || requestEntry == null || loggingContext == null)
            {
                taskHost = null;
                return false;
            }

            TaskHost candidate;
            using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                       BuildExecutionMetric.FastTaskHostMaterialization,
                       _taskName,
                       _targetName))
            {
                candidate = new TaskHost(
                    host,
                    requestEntry,
                    _taskLocation,
                    targetBuilderCallback)
                {
                    LoggingContext = loggingContext,
                    ContinueOnError = _continueOnError,
                    ConvertErrorsToWarnings = _convertErrorsToWarnings,
                    AllowFailureWithoutError =
                        Volatile.Read(ref _allowFailureWithoutError) != 0,
                };
            }

            state = Interlocked.CompareExchange(
                ref _taskHostState,
                candidate,
                comparand: null);
            if (state == null)
            {
                candidate.AllowFailureWithoutError =
                    Volatile.Read(ref _allowFailureWithoutError) != 0;
                Volatile.Write(ref _taskHostMaterialized, 1);
                taskHost = candidate;
                return true;
            }

            candidate.MarkAsInactive();
            if (state is TaskHost published)
            {
                taskHost = published;
                return true;
            }

            taskHost = null;
            return false;
        }

        private void VerifyActive()
        {
            Assumed.False(ReferenceEquals(Volatile.Read(ref _taskHostState), s_inactive));
        }
    }
}
