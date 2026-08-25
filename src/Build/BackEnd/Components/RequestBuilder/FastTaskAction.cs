// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
#if NET
using System.Runtime.CompilerServices;
#endif
using System.Threading;
using Microsoft.Build.BackEnd.Components.RequestBuilder;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// A complete residual program for an ordinary, in-process, single-batch task with no outputs.
    /// </summary>
    internal sealed class FastTaskAction
    {
        private readonly TaskActionTemplate _template;
        private readonly TaskFactoryWrapper _taskFactoryWrapper;
        private readonly LoadedType _loadedType;
        private readonly FastTaskInputOperation[] _inputs;
        private readonly string[] _requiredParameterNames;
        private readonly ulong _allRequiredParameters;
        private readonly ContinueOnError _continueOnError;

        private FastTaskAction(
            TaskActionTemplate template,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType,
            FastTaskInputOperation[] inputs,
            string[] requiredParameterNames,
            ulong allRequiredParameters,
            ContinueOnError continueOnError)
        {
            _template = template;
            _taskFactoryWrapper = taskFactoryWrapper;
            _loadedType = loadedType;
            _inputs = inputs;
            _requiredParameterNames = requiredParameterNames;
            _allRequiredParameters = allRequiredParameters;
            _continueOnError = continueOnError;
        }

        internal Type TaskType => _loadedType.Type;

        internal static FastTaskAction TryCreate(
            TaskActionTemplate template,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType,
            TaskActionTypeMetadata metadata,
            BoundTaskParameter[] parameters,
            string[] requiredParameterNames,
            ulong allRequiredParameters)
        {
            if (!string.IsNullOrEmpty(template.Condition) ||
                !TryCompileContinueOnError(template, out ContinueOnError continueOnError))
            {
                return null;
            }

            var inputs = new FastTaskInputOperation[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                BoundTaskParameter parameter = parameters[i];
                if (ExpressionShredder.ContainsMetadataExpressionOutsideTransform(parameter.Source.Value))
                {
                    return null;
                }

                inputs[i] = FastTaskInputOperation.TryCreate(
                    parameter,
                    metadata.GetProperty(parameter.PropertyIndex));
                if (inputs[i] == null)
                {
                    return null;
                }
            }

            return new FastTaskAction(
                template,
                taskFactoryWrapper,
                loadedType,
                inputs,
                requiredParameterNames,
                allRequiredParameters,
                continueOnError);
        }

        internal bool CanExecute(FastTaskExecutionFrame frame)
        {
            return frame.RequestEntry.Request.HostServices == null &&
                !frame.Host.BuildParameters.LogTaskInputs &&
                !Traits.Instance.EscapeHatches.LogTaskInputs &&
                (!frame.Host.BuildParameters.MultiThreaded ||
                    !TaskRouter.NeedsTaskHostInMultiThreadedMode(TaskType));
        }

        internal WorkUnitResult Execute(FastTaskExecutionFrame frame)
        {
            string projectFullPath = frame.RequestEntry.RequestConfiguration.Project.FullPath;
            TaskLoggingContext taskLoggingContext = frame.TargetLoggingContext.LogTaskBatchStarted(
                projectFullPath,
                frame.TaskInstance,
                _loadedType.Path);
            MSBuildEventSource.Log.ExecuteTaskStart(_template.Name, taskLoggingContext.BuildEventContext.TaskId);

            using var taskMeasurement = BuildExecutionInstrumentation.Measure(
                BuildExecutionMetric.Task,
                _template.Name,
                frame.TargetLoggingContext.Target.Name);

            if (frame.Host.BuildParameters.IsTelemetryEnabled)
            {
                _taskFactoryWrapper.Statistics?.ExecutionStarted();
            }

            frame.RequestEntry.Request.CurrentTaskContext = taskLoggingContext.BuildEventContext;
            WorkUnitResult result = new(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            bool allowWarnAndContinueCoercion = true;

            try
            {
                result = ExecuteCore(frame, taskLoggingContext);
            }
            catch (InvalidProjectFileException e)
            {
                taskLoggingContext.LogInvalidProjectFileError(e);
                result = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, e);
                allowWarnAndContinueCoercion = false;
            }
            finally
            {
                frame.RequestEntry.Request.CurrentTaskContext = null;
                taskLoggingContext.LogTaskBatchFinished(
                    projectFullPath,
                    result.ResultCode == WorkUnitResultCode.Success || result.ResultCode == WorkUnitResultCode.Skipped);

                if (frame.Host.BuildParameters.IsTelemetryEnabled)
                {
                    _taskFactoryWrapper.Statistics?.ExecutionStopped();
                }

                if (result.ResultCode == WorkUnitResultCode.Failed &&
                    allowWarnAndContinueCoercion &&
                    _continueOnError == ContinueOnError.WarnAndContinue)
                {
                    result = new WorkUnitResult(WorkUnitResultCode.Success, result.ActionCode, result.Exception);
                }

                MSBuildEventSource.Log.ExecuteTaskStop(_template.Name, taskLoggingContext.BuildEventContext.TaskId);
            }

            return result;
        }

        private WorkUnitResult ExecuteCore(FastTaskExecutionFrame frame, TaskLoggingContext taskLoggingContext)
        {
            if (frame.Host.BuildParameters.SaveOperatingEnvironment)
            {
                frame.RequestEntry.TaskEnvironment.ProjectDirectory =
                    new AbsolutePath(frame.RequestEntry.ProjectRootDirectory, ignoreRootedCheck: true);
            }

            var taskHost = new TaskHost(
                frame.Host,
                frame.RequestEntry,
                _template.Location,
                frame.TargetBuilderCallback)
            {
                LoggingContext = taskLoggingContext,
                ContinueOnError = _continueOnError != ContinueOnError.ErrorAndStop,
                ConvertErrorsToWarnings = _continueOnError == ContinueOnError.WarnAndContinue,
            };

            ITask task = null;
            try
            {
                task = CreateTask(frame, taskLoggingContext);
                if (task == null)
                {
                    ProjectErrorUtilities.ThrowInvalidProject(
                        _template.Location,
                        "TaskDeclarationOrUsageError",
                        _template.Name);
                }

                frame.SetCurrentTask(task, taskLoggingContext, _template);

                task.BuildEngine = taskHost;
                task.HostObject = null;
                if (task is IMultiThreadableTask multiThreadableTask)
                {
                    multiThreadableTask.TaskEnvironment = frame.RequestEntry.TaskEnvironment;
                }

                if (task is IIncrementalTask incrementalTask)
                {
                    incrementalTask.FailIfNotIncremental = frame.Host.BuildParameters.Question;
                }

                using var assemblyLoadsTracker = AssemblyLoadsTracker.StartTracking(
                    taskLoggingContext,
                    AssemblyLoadingContext.TaskRun,
                    task.GetType());

                SetInputs(frame, task, taskLoggingContext);
                bool taskResult = ExecuteBody(frame, task, taskLoggingContext, out bool taskReturned);

                if (taskReturned)
                {
                    frame.Lookup.SetProperty(
                        ProjectPropertyInstance.Create(
                            ReservedPropertyNames.lastTaskResult,
                            taskResult ? "true" : "false",
                            mayBeReserved: true,
                            frame.RequestEntry.RequestConfiguration.Project.IsImmutable));
                }

                if (taskReturned &&
                    !taskResult &&
                    !taskLoggingContext.HasLoggedErrors &&
                    (task.BuildEngine is TaskHost returnedTaskHost && returnedTaskHost.BuildRequestsSucceeded) &&
                    !frame.CancellationToken.IsCancellationRequested)
                {
                    if (task.BuildEngine is IBuildEngine7 buildEngine7 && buildEngine7.AllowFailureWithoutError)
                    {
                        taskLoggingContext.LogComment(
                            MessageImportance.Normal,
                            "TaskReturnedFalseButDidNotLogError",
                            _template.Name);
                    }
                    else if (_continueOnError == ContinueOnError.WarnAndContinue)
                    {
                        taskLoggingContext.LogWarning(
                            null,
                            new BuildEventFileInfo(_template.Location),
                            "TaskReturnedFalseButDidNotLogError",
                            _template.Name);
                        taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
                    }
                    else
                    {
                        taskLoggingContext.LogError(
                            new BuildEventFileInfo(_template.Location),
                            "TaskReturnedFalseButDidNotLogError",
                            _template.Name);
                    }
                }

                WorkUnitResultCode resultCode =
                    taskResult ? WorkUnitResultCode.Success : WorkUnitResultCode.Failed;
                WorkUnitActionCode actionCode = WorkUnitActionCode.Continue;

                if (!taskResult)
                {
                    if (_continueOnError == ContinueOnError.ErrorAndStop)
                    {
                        actionCode = WorkUnitActionCode.Stop;
                    }
                    else
                    {
                        taskLoggingContext.LogComment(
                            MessageImportance.Normal,
                            "TaskContinuedDueToContinueOnError",
                            "ContinueOnError",
                            _template.Name,
                            _template.ContinueOnError);
                    }
                }

                return new WorkUnitResult(resultCode, actionCode, null);
            }
            finally
            {
                frame.ClearCurrentTask(task);
                if (task != null)
                {
                    ((AssemblyTaskFactory)_taskFactoryWrapper.TaskFactory).CleanupTask(task);
                }

                taskHost.MarkAsInactive();
            }
        }

        private ITask CreateTask(FastTaskExecutionFrame frame, TaskLoggingContext taskLoggingContext)
        {
            var assemblyTaskFactory = (AssemblyTaskFactory)_taskFactoryWrapper.TaskFactory;
            assemblyTaskFactory.RecordTaskExecutionTelemetry(taskLoggingContext, isTaskHost: false);

            try
            {
                ITask task = _loadedType.CreateInstance(frame.RequestEntry.TaskEnvironment);
#if NET
                if (task != null && RuntimeFeature.IsDynamicCodeSupported)
#else
                if (task != null)
#endif
                {
                    string realTaskAssemblyLocation = task.GetType().Assembly.Location;
                    if (!string.IsNullOrWhiteSpace(realTaskAssemblyLocation) &&
                        realTaskAssemblyLocation != _loadedType.Path)
                    {
                        taskLoggingContext.LogComment(
                            MessageImportance.Normal,
                            "TaskAssemblyLocationMismatch",
                            realTaskAssemblyLocation,
                            _loadedType.Path);
                    }

                    assemblyTaskFactory.RecordTaskSubclassingTelemetry(taskLoggingContext);
                }

                return task;
            }
            catch (InvalidCastException e)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_template.Location),
                    "TaskInstantiationFailureErrorInvalidCast",
                    _template.Name,
                    _taskFactoryWrapper.TaskFactory.FactoryName,
                    e.Message);
            }
            catch (TargetInvocationException e)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_template.Location),
                    "TaskInstantiationFailureError",
                    _template.Name,
                    _taskFactoryWrapper.TaskFactory.FactoryName,
                    Environment.NewLine + e.InnerException);
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_template.Location),
                    "TaskInstantiationFailureError",
                    _template.Name,
                    _taskFactoryWrapper.TaskFactory.FactoryName,
                    e.Message);
            }

            return null;
        }

        private void SetInputs(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext)
        {
            ulong requiredSet = 0;
            for (int i = 0; i < _inputs.Length; i++)
            {
                if (!_inputs[i].Apply(frame, task, taskLoggingContext, ref requiredSet))
                {
                    ProjectErrorUtilities.ThrowInvalidProject(
                        _template.Location,
                        "TaskParametersError",
                        _template.Name,
                        string.Empty);
                }
            }

            if (requiredSet != _allRequiredParameters)
            {
                for (int i = 0; i < _requiredParameterNames.Length; i++)
                {
                    if ((requiredSet & (1UL << i)) == 0)
                    {
                        ProjectErrorUtilities.ThrowInvalidProject(
                            _template.Location,
                            "RequiredPropertyNotSetError",
                            _template.Name,
                            _requiredParameterNames[i]);
                    }
                }
            }
        }

        private bool ExecuteBody(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            out bool taskReturned)
        {
            bool taskResult = false;
            Exception taskException = null;
            taskReturned = false;

            if (frame.IsCancellationRequested)
            {
                taskReturned = true;
                return false;
            }

            long taskBodyStart = BuildExecutionInstrumentation.StartTimestamp();
            try
            {
#if FEATURE_FILE_TRACKER
                using (FullTracking.Track(
                    frame.TargetLoggingContext.Target.Name,
                    _template.Name,
                    frame.RequestEntry.ProjectRootDirectory,
                    frame.RequestEntry.RequestConfiguration.Project.PropertiesToBuildWith))
#endif
                {
                    taskResult = task.Execute();
                }
            }
            catch (Exception ex)
            {
                if (ExceptionHandling.IsCriticalException(ex) ||
                    Environment.GetEnvironmentVariable("MSBUILDDONOTCATCHTASKEXCEPTIONS") == "1")
                {
                    taskLoggingContext.LogFatalTaskError(
                        ex,
                        new BuildEventFileInfo(_template.Location),
                        _template.Name);
                    throw new CriticalTaskException(ex);
                }

                taskException = ex;
            }
            finally
            {
                BuildExecutionInstrumentation.RecordSince(
                    BuildExecutionMetric.TaskBody,
                    taskBodyStart,
                    BuildExecutionInstrumentation.DetailsEnabled ? _template.Name : null,
                    frame.TargetLoggingContext.Target.Name);
            }

            if (taskException == null)
            {
                taskReturned = true;
            }
            else
            {
                HandleTaskException(taskException, taskLoggingContext);
            }

            return taskResult;
        }

        private void HandleTaskException(Exception taskException, TaskLoggingContext taskLoggingContext)
        {
            Type type = taskException.GetType();
            if (type == typeof(LoggerException))
            {
                throw new LoggerException(taskException.Message, taskException);
            }

            if (type == typeof(InternalLoggerException))
            {
                var exception = (InternalLoggerException)taskException;
                throw new InternalLoggerException(
                    taskException.Message,
                    taskException,
                    exception.BuildEventArgs,
                    exception.ErrorCode,
                    exception.HelpKeyword,
                    exception.InitializationException);
            }

            if (type == typeof(ThreadAbortException))
            {
#if !NET
                Thread.ResetAbort();
#endif
                throw taskException;
            }

            if (type == typeof(BuildAbortedException))
            {
                throw new BuildAbortedException(taskException.Message, (BuildAbortedException)taskException);
            }

            if (type == typeof(CircularDependencyException))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    taskLoggingContext.Task.Location,
                    "CircularDependency",
                    taskLoggingContext.TargetLoggingContext.Target.Name);
            }

            if (type == typeof(InvalidProjectFileException))
            {
                var invalidProject = (InvalidProjectFileException)taskException;
                invalidProject.HasBeenLogged = false;
                if (_continueOnError != ContinueOnError.ErrorAndStop)
                {
                    taskLoggingContext.LogInvalidProjectFileError(invalidProject);
                    taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
                }
                else
                {
                    throw new InvalidProjectFileException(invalidProject.Message, invalidProject);
                }

                return;
            }

            Exception exceptionToLog =
                taskException is TargetInvocationException invocationException
                    ? invocationException.InnerException
                    : taskException;

            if (_continueOnError == ContinueOnError.WarnAndContinue)
            {
                taskLoggingContext.LogTaskWarningFromException(
                    exceptionToLog,
                    new BuildEventFileInfo(_template.Location),
                    _template.Name);
                taskLoggingContext.LogComment(MessageImportance.Normal, "ErrorConvertedIntoWarning");
            }
            else
            {
                taskLoggingContext.LogFatalTaskError(
                    exceptionToLog,
                    new BuildEventFileInfo(_template.Location),
                    _template.Name);
            }
        }

        private static bool TryCompileContinueOnError(
            TaskActionTemplate template,
            out ContinueOnError continueOnError)
        {
            continueOnError = ContinueOnError.ErrorAndStop;
            if (template.ContinueOnErrorLocation == null)
            {
                return true;
            }

            string value = template.ContinueOnError;
            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.errorAndContinue,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continueOnError = ContinueOnError.ErrorAndContinue;
                return true;
            }

            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.warnAndContinue,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continueOnError = ContinueOnError.WarnAndContinue;
                return true;
            }

            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.errorAndStop,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ConversionUtilities.TryConvertStringToBool(value, out bool boolValue))
            {
                continueOnError =
                    boolValue ? ContinueOnError.WarnAndContinue : ContinueOnError.ErrorAndStop;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// One prebound input expression, conversion, and setter call.
    /// </summary>
    internal sealed class FastTaskInputOperation
    {
        private readonly BoundTaskParameter _binding;
        private readonly TaskActionPropertyMetadata _property;

        private FastTaskInputOperation(
            BoundTaskParameter binding,
            TaskActionPropertyMetadata property)
        {
            _binding = binding;
            _property = property;
        }

        internal static FastTaskInputOperation TryCreate(
            BoundTaskParameter binding,
            TaskActionPropertyMetadata property)
        {
            Type parameterType = property.ParameterType;
            if (property.Setter == null ||
                TaskParameterTypeVerifier.TryGetSupportedTaskItemValueType(parameterType, out _) ||
                (parameterType.IsArray &&
                    TaskParameterTypeVerifier.TryGetSupportedTaskItemValueType(
                        parameterType.GetElementType(),
                        out _)))
            {
                return null;
            }

            return new FastTaskInputOperation(binding, property);
        }

        internal bool Apply(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            ref ulong requiredSet)
        {
            bool parameterSet;
            bool success;
            try
            {
                success = _property.ParameterType.IsArray
                    ? ApplyVector(frame, task, taskLoggingContext, out parameterSet)
                    : ApplyScalar(frame, task, taskLoggingContext, out parameterSet);
            }
            catch (Exception ex)
                when (ex is InvalidCastException ||
                    ex is ArgumentException ||
                    ex is FormatException ||
                    ex is OverflowException)
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    _binding.Source.Location,
                    "InvalidTaskParameterValueError",
                    frame.Expander.ExpandIntoStringAndUnescape(
                        _binding.Source.Value,
                        ExpanderOptions.ExpandAll,
                        _binding.Source.Location),
                    _binding.Property.Name,
                    _property.ParameterType.FullName,
                    frame.TaskInstance.Name);
                return false;
            }

            if (!success)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_binding.Source.Location),
                    "InvalidTaskAttributeError",
                    _binding.Source.Name,
                    _binding.Source.Value,
                    frame.TaskInstance.Name);
                return false;
            }

            if (parameterSet)
            {
                requiredSet |= _binding.RequiredBit;
            }

            return true;
        }

        private bool ApplyScalar(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            out bool parameterSet)
        {
            parameterSet = false;
            Type parameterType = _property.ParameterType;

            if (parameterType == typeof(ITaskItem))
            {
                IList<TaskItem> items = frame.Expander.ExpandIntoTaskItemsLeaveEscaped(
                    _binding.Source.Value,
                    ExpanderOptions.ExpandAll,
                    _binding.Source.Location);

                if (items.Count == 0)
                {
                    return true;
                }

                if (items.Count != 1)
                {
                    ProjectErrorUtilities.ThrowInvalidProject(
                        _binding.Source.Location,
                        "CannotPassMultipleItemsIntoScalarParameter",
                        frame.Expander.ExpandIntoStringAndUnescape(
                            _binding.Source.Value,
                            ExpanderOptions.ExpandAll,
                            _binding.Source.Location),
                        _binding.Property.Name,
                        parameterType.FullName,
                        frame.TaskInstance.Name);
                }

                parameterSet = true;
                return SetValue(task, items[0], taskLoggingContext, frame);
            }

            string expandedValue = frame.Expander.ExpandIntoStringAndUnescape(
                _binding.Source.Value,
                ExpanderOptions.ExpandAll,
                _binding.Source.Location);
            if (expandedValue.Length == 0)
            {
                return true;
            }

            parameterSet = true;
            return SetValue(
                task,
                ConvertStringToValue(expandedValue, parameterType, frame.RequestEntry.TaskEnvironment),
                taskLoggingContext,
                frame);
        }

        private bool ApplyVector(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            out bool parameterSet)
        {
            IList<TaskItem> items = frame.Expander.ExpandIntoTaskItemsLeaveEscaped(
                _binding.Source.Value,
                ExpanderOptions.ExpandAll,
                _binding.Source.Location);

            parameterSet = items.Count > 0 || _binding.RequiredBit != 0;
            if (!parameterSet)
            {
                return true;
            }

            Type parameterType = _property.ParameterType;
            object value;
            if (parameterType == typeof(ITaskItem[]))
            {
                var values = new ITaskItem[items.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = items[i];
                }

                value = values;
            }
            else if (parameterType == typeof(string[]))
            {
                var values = new string[items.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = items[i].ItemSpec;
                }

                value = values;
            }
            else if (parameterType == typeof(bool[]))
            {
                var values = new bool[items.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = ConversionUtilities.ConvertStringToBool(items[i].ItemSpec);
                }

                value = values;
            }
            else
            {
#if NET
                Array values = Array.CreateInstanceFromArrayType(parameterType, items.Count);
#else
                Array values = Array.CreateInstance(parameterType.GetElementType(), items.Count);
#endif
                Type elementType = parameterType.GetElementType();
                for (int i = 0; i < values.Length; i++)
                {
                    values.SetValue(
                        ConvertStringToValue(
                            items[i].ItemSpec,
                            elementType,
                            frame.RequestEntry.TaskEnvironment),
                        i);
                }

                value = values;
            }

            return SetValue(task, value, taskLoggingContext, frame);
        }

        private bool SetValue(
            ITask task,
            object value,
            TaskLoggingContext taskLoggingContext,
            FastTaskExecutionFrame frame)
        {
            try
            {
                _property.Setter(task, value);
                return true;
            }
            catch (TargetInvocationException e)
            {
                taskLoggingContext.LogFatalTaskError(
                    e.InnerException,
                    new BuildEventFileInfo(_binding.Source.Location),
                    frame.TaskInstance.Name);
            }
            catch (Exception e)
            {
                taskLoggingContext.LogFatalTaskError(
                    e,
                    new BuildEventFileInfo(_binding.Source.Location),
                    frame.TaskInstance.Name);
            }

            return false;
        }

        private static object ConvertStringToValue(
            string value,
            Type targetType,
            TaskEnvironment taskEnvironment)
        {
            if (targetType == typeof(AbsolutePath))
            {
                return taskEnvironment.GetAbsolutePath(value);
            }

            if (targetType == typeof(FileInfo))
            {
                return new FileInfo(taskEnvironment.GetAbsolutePath(value).Value);
            }

            if (targetType == typeof(DirectoryInfo))
            {
                return new DirectoryInfo(taskEnvironment.GetAbsolutePath(value).Value);
            }

            return ValueTypeParser.Parse(value, targetType);
        }
    }

    /// <summary>
    /// Reused dynamic state for all fast actions in one target bucket.
    /// </summary>
    internal sealed class FastTaskExecutionFrame : IDisposable
    {
        private readonly FastTaskCancellationState _cancellationState;

        internal FastTaskExecutionFrame(
            IBuildComponentHost host,
            BuildRequestEntry requestEntry,
            ITargetBuilderCallback targetBuilderCallback,
            TargetLoggingContext targetLoggingContext,
            ProjectTaskInstance taskInstance,
            Lookup lookup,
            CancellationToken cancellationToken)
        {
            Host = host;
            RequestEntry = requestEntry;
            TargetBuilderCallback = targetBuilderCallback;
            TargetLoggingContext = targetLoggingContext;
            TaskInstance = taskInstance;
            Lookup = lookup;
            CancellationToken = cancellationToken;
            Expander = new Expander<ProjectPropertyInstance, ProjectItemInstance>(
                lookup,
                lookup,
                new StringMetadataTable(metadata: null),
                FileSystems.Default,
                targetLoggingContext);
            _cancellationState = new FastTaskCancellationState(cancellationToken);
        }

        internal IBuildComponentHost Host { get; }

        internal BuildRequestEntry RequestEntry { get; }

        internal ITargetBuilderCallback TargetBuilderCallback { get; }

        internal TargetLoggingContext TargetLoggingContext { get; }

        internal ProjectTaskInstance TaskInstance { get; private set; }

        internal Lookup Lookup { get; }

        internal CancellationToken CancellationToken { get; }

        internal bool IsCancellationRequested => _cancellationState.IsCancellationRequested;

        internal Expander<ProjectPropertyInstance, ProjectItemInstance> Expander { get; }

        internal void SetTaskInstance(ProjectTaskInstance taskInstance) => TaskInstance = taskInstance;

        internal void SetCurrentTask(
            ITask task,
            TaskLoggingContext taskLoggingContext,
            TaskActionTemplate template) =>
            _cancellationState.SetCurrentTask(task, taskLoggingContext, template);

        internal void ClearCurrentTask(ITask task) => _cancellationState.ClearCurrentTask(task);

        public void Dispose() => _cancellationState.Dispose();
    }

    /// <summary>
    /// One cancellation registration shared by all task invocations in a target bucket.
    /// </summary>
    internal sealed class FastTaskCancellationState : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private int _cancelled;
        private ITask _currentTask;
        private ITask _taskCancellationIssued;
        private TaskLoggingContext _currentTaskLoggingContext;
        private TaskActionTemplate _currentTemplate;

        internal FastTaskCancellationState(CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(
                static state => ((FastTaskCancellationState)state).CancelCurrentTask(),
                this);
        }

        internal void SetCurrentTask(
            ITask task,
            TaskLoggingContext taskLoggingContext,
            TaskActionTemplate template)
        {
            _currentTaskLoggingContext = taskLoggingContext;
            _currentTemplate = template;
            Volatile.Write(ref _taskCancellationIssued, null);
            Volatile.Write(ref _currentTask, task);

            if (IsCancellationRequested)
            {
                CancelCurrentTask();
            }
        }

        internal void ClearCurrentTask(ITask task)
        {
            Interlocked.CompareExchange(ref _currentTask, null, task);
            _currentTaskLoggingContext = null;
            _currentTemplate = null;
        }

        public void Dispose() => _registration.Dispose();

        internal bool IsCancellationRequested => Volatile.Read(ref _cancelled) != 0;

        private void CancelCurrentTask()
        {
            Volatile.Write(ref _cancelled, 1);

            ITask currentTask = Volatile.Read(ref _currentTask);
            if (currentTask is not ICancelableTask cancelableTask ||
                Interlocked.CompareExchange(ref _taskCancellationIssued, currentTask, null) != null)
            {
                return;
            }

            try
            {
                cancelableTask.Cancel();
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                TaskLoggingContext loggingContext = _currentTaskLoggingContext;
                TaskActionTemplate template = _currentTemplate;
                if (loggingContext?.IsValid == true && template != null)
                {
                    loggingContext.LogFatalTaskError(
                        e,
                        new BuildEventFileInfo(template.Location),
                        template.Name);
                }
            }
        }
    }
}
