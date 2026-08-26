// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
#if NET
using System.Runtime.CompilerServices;
#endif
using System.Threading;
using Microsoft.Build.BackEnd.Components.RequestBuilder;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
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
    internal readonly struct FastTaskInvocation
    {
        private readonly FastTaskAction _action;
        private readonly TaskFactoryWrapper _taskFactoryWrapper;

        internal FastTaskInvocation(
            FastTaskAction action,
            TaskFactoryWrapper taskFactoryWrapper)
        {
            _action = action;
            _taskFactoryWrapper = taskFactoryWrapper;
        }

        internal bool IsValid =>
            _action != null && _taskFactoryWrapper != null;

        internal bool CanExecute(FastTaskExecutionFrame frame) =>
            _action.CanExecute(frame);

        internal WorkUnitResult Execute(FastTaskExecutionFrame frame) =>
            _action.Execute(frame, _taskFactoryWrapper);
    }

    /// <summary>
    /// A complete residual program for an ordinary, in-process, single-batch task.
    /// </summary>
    internal sealed class FastTaskAction
    {
        private readonly CompiledTaskSourceProgram _template;
        private readonly LoadedType _loadedType;
        private readonly CompiledConditionProgram _condition;
        private readonly CompiledScalarProgram _conditionDisplay;
        private readonly CompiledScalarProgram _continueOnError;
        private readonly FastTaskInputOperation[] _inputs;
        private readonly FastTaskOutputOperation[] _outputs;
        private readonly string[] _requiredParameterNames;
        private readonly ulong _allRequiredParameters;
        private readonly bool _requiresEagerTaskEnvironmentSetup;

        private FastTaskAction(
            CompiledTaskSourceProgram program,
            LoadedType loadedType,
            CompiledConditionProgram condition,
            CompiledScalarProgram conditionDisplay,
            CompiledScalarProgram continueOnError,
            FastTaskInputOperation[] inputs,
            FastTaskOutputOperation[] outputs,
            string[] requiredParameterNames,
            ulong allRequiredParameters)
        {
            _template = program;
            _loadedType = loadedType;
            _condition = condition;
            _conditionDisplay = conditionDisplay;
            _continueOnError = continueOnError;
            _inputs = inputs;
            _outputs = outputs;
            _requiredParameterNames = requiredParameterNames;
            _allRequiredParameters = allRequiredParameters;
            _requiresEagerTaskEnvironmentSetup =
                !TaskRouter.HasMultiThreadableTaskAttribute(loadedType.Type) ||
                typeof(IMultiThreadableTask).IsAssignableFrom(loadedType.Type) ||
                loadedType.RequiresTaskEnvironmentForConstruction ||
                inputs.Any(static input => input.RequiresTaskEnvironment);
        }

        internal Type TaskType => _loadedType.Type;

        internal static FastTaskAction TryGetOrCreate(
            CompiledTaskSourceProgram program,
            ResolvedTaskRegistration registration)
        {
            TaskFactoryWrapper taskFactoryWrapper =
                registration.TaskFactoryWrapper;
            if (!program.HasStaticCurrentProcessIdentity ||
                registration.Requirements != TaskRequirements.None ||
                taskFactoryWrapper?.TaskFactory is not AssemblyTaskFactory ||
                !taskFactoryWrapper.FactoryIdentityParameters.IsEmpty)
            {
                return null;
            }

            LoadedType loadedType =
                taskFactoryWrapper.TaskFactoryLoadedType;
            if (loadedType?.Type == null ||
                loadedType.LoadedViaMetadataLoadContext ||
                typeof(IGeneratedTask).IsAssignableFrom(loadedType.Type) ||
                typeof(MSBuild).IsAssignableFrom(loadedType.Type) ||
                typeof(CallTarget).IsAssignableFrom(loadedType.Type) ||
                typeof(TaskHostTask).IsAssignableFrom(loadedType.Type))
            {
                return null;
            }

#if FEATURE_APPDOMAIN
            if (loadedType.IsMarshalByRef ||
                loadedType.HasLoadInSeparateAppDomainAttribute)
            {
                return null;
            }
#endif

            TaskActionTypeMetadata metadata =
                TaskActionTypeMetadata.GetOrCreate(loadedType);
            return metadata.GetOrCreateFastAction(
                program,
                taskFactoryWrapper,
                loadedType);
        }

        internal static FastTaskAction TryCreate(
            CompiledTaskSourceProgram program,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType,
            TaskActionTypeMetadata metadata)
        {
            if (!program.SupportsFastExecution)
            {
                return null;
            }

            IReadOnlyDictionary<string, string> requiredParameters =
                taskFactoryWrapper.GetNamesOfPropertiesWithRequiredAttribute;
            if (requiredParameters.Count > 64)
            {
                return null;
            }

            string[] requiredParameterNames =
                requiredParameters.Keys.ToArray();
            var inputs =
                new FastTaskInputOperation[program.Parameters.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                CompiledTaskParameterProgram parameter =
                    program.Parameters[i];
                try
                {
                    if (taskFactoryWrapper.GetProperty(parameter.Name) == null)
                    {
                        return null;
                    }
                }
                catch (AmbiguousMatchException)
                {
                    return null;
                }

                int propertyIndex = BoundTaskAction.FindPropertyIndex(
                    loadedType,
                    parameter.Name);
                if (propertyIndex < 0)
                {
                    return null;
                }

                ulong requiredBit = 0;
                for (int requiredIndex = 0;
                     requiredIndex < requiredParameterNames.Length;
                     requiredIndex++)
                {
                    if (requiredParameterNames[requiredIndex].Equals(
                            parameter.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        requiredBit = 1UL << requiredIndex;
                        break;
                    }
                }

                inputs[i] = FastTaskInputOperation.TryCreate(
                    parameter,
                    metadata.GetProperty(propertyIndex),
                    requiredBit);
                if (!inputs[i].IsValid)
                {
                    return null;
                }
            }

            var outputs =
                new FastTaskOutputOperation[program.Outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                outputs[i] = FastTaskOutputOperation.TryCreate(
                    program.Outputs[i],
                    taskFactoryWrapper,
                    loadedType,
                    metadata);
                if (!outputs[i].IsValid)
                {
                    return null;
                }
            }

            ulong allRequiredParameters =
                requiredParameterNames.Length == 64
                    ? ulong.MaxValue
                    : (1UL << requiredParameterNames.Length) - 1;

            return new FastTaskAction(
                program,
                loadedType,
                program.ConditionProgram,
                program.ConditionDisplayProgram,
                program.ContinueOnErrorProgram,
                inputs,
                outputs,
                requiredParameterNames,
                allRequiredParameters);
        }

        internal bool CanExecute(FastTaskExecutionFrame frame)
        {
            return frame.RequestEntry.Request.HostServices == null &&
                !frame.Host.BuildParameters.LogTaskInputs &&
                !Traits.Instance.EscapeHatches.LogTaskInputs &&
                (!frame.Host.BuildParameters.MultiThreaded ||
                    !TaskRouter.NeedsTaskHostInMultiThreadedMode(TaskType));
        }

        internal WorkUnitResult Execute(
            FastTaskExecutionFrame frame,
            TaskFactoryWrapper taskFactoryWrapper)
        {
            using var actionMeasurement =
                BuildExecutionInstrumentation.MeasureFastTaskDetail(
                    BuildExecutionMetric.FastTaskAction,
                    _template.Name,
                    frame.TargetLoggingContext.Target.Name);

            bool conditionResult = true;
            if (_condition != null)
            {
                using var conditionMeasurement =
                    BuildExecutionInstrumentation.MeasureFastTaskDetail(
                        BuildExecutionMetric.FastTaskCondition,
                        _template.Name,
                        frame.TargetLoggingContext.Target.Name);
                conditionResult =
                    _condition.Evaluate(
                        frame,
                        _template.ConditionLocation);
            }

            if (!conditionResult)
            {
                if (frame.TargetLoggingContext.LoggingService.MinimumRequiredMessageImportance >
                        MessageImportance.Low &&
                    !frame.TargetLoggingContext.LoggingService.OnlyLogCriticalEvents)
                {
                    frame.TargetLoggingContext.LogComment(
                        MessageImportance.Low,
                        "TaskSkippedFalseCondition",
                        _template.Name,
                        _template.Condition,
                        _conditionDisplay.Evaluate(
                            frame,
                            _template.ConditionLocation));
                }

                return new WorkUnitResult(
                    WorkUnitResultCode.Skipped,
                    WorkUnitActionCode.Continue,
                    null);
            }

            string projectFullPath = frame.RequestEntry.RequestConfiguration.Project.FullPath;
            TaskLoggingContext taskLoggingContext;
            using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                       BuildExecutionMetric.FastTaskLoggingStart,
                       _template.Name,
                       frame.TargetLoggingContext.Target.Name))
            {
                taskLoggingContext = frame.TargetLoggingContext.LogTaskBatchStarted(
                    projectFullPath,
                    frame.TaskInstance,
                    _loadedType.Path);
                MSBuildEventSource.Log.ExecuteTaskStart(
                    _template.Name,
                    taskLoggingContext.BuildEventContext.TaskId);
            }

            using var taskMeasurement = BuildExecutionInstrumentation.Measure(
                BuildExecutionMetric.Task,
                _template.Name,
                frame.TargetLoggingContext.Target.Name);

            if (frame.Host.BuildParameters.IsTelemetryEnabled)
            {
                taskFactoryWrapper.Statistics?.ExecutionStarted();
            }

            frame.RequestEntry.Request.CurrentTaskContext = taskLoggingContext.BuildEventContext;
            WorkUnitResult result = new(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, null);
            bool allowWarnAndContinueCoercion = true;
            ContinueOnError continueOnError = ContinueOnError.ErrorAndStop;

            try
            {
                result = ExecuteCore(
                    frame,
                    taskLoggingContext,
                    taskFactoryWrapper,
                    out continueOnError);
            }
            catch (InvalidProjectFileException e)
            {
                taskLoggingContext.LogInvalidProjectFileError(e);
                result = new WorkUnitResult(WorkUnitResultCode.Failed, WorkUnitActionCode.Stop, e);
                allowWarnAndContinueCoercion = false;
            }
            finally
            {
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskLoggingFinish,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    frame.RequestEntry.Request.CurrentTaskContext = null;
                    taskLoggingContext.LogTaskBatchFinished(
                        projectFullPath,
                        result.ResultCode == WorkUnitResultCode.Success || result.ResultCode == WorkUnitResultCode.Skipped);

                    if (frame.Host.BuildParameters.IsTelemetryEnabled)
                    {
                        taskFactoryWrapper.Statistics?.ExecutionStopped();
                    }

                    if (result.ResultCode == WorkUnitResultCode.Failed &&
                        allowWarnAndContinueCoercion &&
                        continueOnError == ContinueOnError.WarnAndContinue)
                    {
                        result = new WorkUnitResult(WorkUnitResultCode.Success, result.ActionCode, result.Exception);
                    }

                    MSBuildEventSource.Log.ExecuteTaskStop(
                        _template.Name,
                        taskLoggingContext.BuildEventContext.TaskId);
                }
            }

            return result;
        }

        private WorkUnitResult ExecuteCore(
            FastTaskExecutionFrame frame,
            TaskLoggingContext taskLoggingContext,
            TaskFactoryWrapper taskFactoryWrapper,
            out ContinueOnError continueOnError)
        {
            string continueOnErrorValue;
            using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                       BuildExecutionMetric.FastTaskContinueOnError,
                       _template.Name,
                       frame.TargetLoggingContext.Target.Name))
            {
                continueOnError = EvaluateContinueOnError(
                    frame,
                    out continueOnErrorValue);
            }

            ResidualTaskExecutionContext executionContext;
            using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                       BuildExecutionMetric.FastTaskHost,
                       _template.Name,
                       frame.TargetLoggingContext.Target.Name))
            {
                executionContext = new ResidualTaskExecutionContext(
                    frame.Host,
                    frame.RequestEntry,
                    _template.Location,
                    frame.TargetBuilderCallback,
                    taskLoggingContext,
                    continueOnError != ContinueOnError.ErrorAndStop,
                    continueOnError == ContinueOnError.WarnAndContinue,
                    _template.Name,
                    frame.TargetLoggingContext.Target.Name);
            }

            if (_requiresEagerTaskEnvironmentSetup)
            {
                executionContext.EnsureTaskEnvironmentInitialized();
            }

            ITask task = null;
            try
            {
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskCreate,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    task = CreateTask(
                        frame,
                        taskLoggingContext,
                        taskFactoryWrapper);
                }

                if (task == null)
                {
                    ProjectErrorUtilities.ThrowInvalidProject(
                        _template.Location,
                        "TaskDeclarationOrUsageError",
                        _template.Name);
                }

                IDisposable assemblyLoadsTracker;
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskSetup,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    frame.SetCurrentTask(task, taskLoggingContext, _template);

                    task.BuildEngine = executionContext;
                    task.HostObject = null;
                    if (task is IMultiThreadableTask multiThreadableTask)
                    {
                        multiThreadableTask.TaskEnvironment = frame.RequestEntry.TaskEnvironment;
                    }

                    if (task is IIncrementalTask incrementalTask)
                    {
                        incrementalTask.FailIfNotIncremental = frame.Host.BuildParameters.Question;
                    }

                    assemblyLoadsTracker = AssemblyLoadsTracker.StartTracking(
                        taskLoggingContext,
                        AssemblyLoadingContext.TaskRun,
                        task.GetType());
                }

                using var assemblyLoadsTrackerScope = assemblyLoadsTracker;
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskInputs,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    SetInputs(frame, task, taskLoggingContext);
                }

                bool taskResult = ExecuteBody(
                    frame,
                    task,
                    taskLoggingContext,
                    continueOnError,
                    out bool taskReturned);

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
                    (task.BuildEngine is ResidualTaskExecutionContext returnedContext &&
                        returnedContext.BuildRequestsSucceeded) &&
                    !frame.CancellationToken.IsCancellationRequested)
                {
                    if (task.BuildEngine is IBuildEngine7 buildEngine7 && buildEngine7.AllowFailureWithoutError)
                    {
                        taskLoggingContext.LogComment(
                            MessageImportance.Normal,
                            "TaskReturnedFalseButDidNotLogError",
                            _template.Name);
                    }
                    else if (continueOnError == ContinueOnError.WarnAndContinue)
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

                if (taskReturned && _outputs.Length != 0)
                {
                    using var outputMeasurement =
                        BuildExecutionInstrumentation.Measure(
                            BuildExecutionMetric.FastTaskOutputs,
                            _template.Name,
                            frame.TargetLoggingContext.Target.Name);
                    taskResult =
                        GatherOutputs(frame, task, taskLoggingContext) &&
                        taskResult;
                }

                WorkUnitResultCode resultCode =
                    taskResult ? WorkUnitResultCode.Success : WorkUnitResultCode.Failed;
                WorkUnitActionCode actionCode = WorkUnitActionCode.Continue;

                if (!taskResult)
                {
                    if (continueOnError == ContinueOnError.ErrorAndStop)
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
                            continueOnErrorValue);
                    }
                }

                return new WorkUnitResult(resultCode, actionCode, null);
            }
            finally
            {
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskCleanup,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    frame.ClearCurrentTask(task);
                    if (task != null)
                    {
                        ((AssemblyTaskFactory)taskFactoryWrapper.TaskFactory)
                            .CleanupTask(task);
                    }

                    executionContext.MarkAsInactive();
                }
            }
        }

        private ITask CreateTask(
            FastTaskExecutionFrame frame,
            TaskLoggingContext taskLoggingContext,
            TaskFactoryWrapper taskFactoryWrapper)
        {
            var assemblyTaskFactory =
                (AssemblyTaskFactory)taskFactoryWrapper.TaskFactory;
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
                    taskFactoryWrapper.TaskFactory.FactoryName,
                    e.Message);
            }
            catch (TargetInvocationException e)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_template.Location),
                    "TaskInstantiationFailureError",
                    _template.Name,
                    taskFactoryWrapper.TaskFactory.FactoryName,
                    Environment.NewLine + e.InnerException);
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_template.Location),
                    "TaskInstantiationFailureError",
                    _template.Name,
                    taskFactoryWrapper.TaskFactory.FactoryName,
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

        private ContinueOnError EvaluateContinueOnError(
            FastTaskExecutionFrame frame,
            out string expandedValue)
        {
            if (_continueOnError == null)
            {
                expandedValue = "false";
                return ContinueOnError.ErrorAndStop;
            }

            expandedValue = _continueOnError.Evaluate(
                frame,
                _template.ContinueOnErrorLocation);
            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.errorAndContinue,
                    expandedValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContinueOnError.ErrorAndContinue;
            }

            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.warnAndContinue,
                    expandedValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContinueOnError.WarnAndContinue;
            }

            if (string.Equals(
                    XMakeAttributes.ContinueOnErrorValues.errorAndStop,
                    expandedValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ContinueOnError.ErrorAndStop;
            }

            try
            {
                return ConversionUtilities.ConvertStringToBool(expandedValue)
                    ? ContinueOnError.WarnAndContinue
                    : ContinueOnError.ErrorAndStop;
            }
            catch (ArgumentException e)
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    _template.ContinueOnErrorLocation,
                    "InvalidContinueOnErrorAttribute",
                    _template.Name,
                    e.Message);
                return ContinueOnError.ErrorAndStop;
            }
        }

        private bool GatherOutputs(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext)
        {
            for (int i = 0; i < _outputs.Length; i++)
            {
                if (!_outputs[i].Apply(
                        frame,
                        task,
                        taskLoggingContext))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ExecuteBody(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            ContinueOnError continueOnError,
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
                HandleTaskException(
                    taskException,
                    taskLoggingContext,
                    continueOnError);
            }

            return taskResult;
        }

        private void HandleTaskException(
            Exception taskException,
            TaskLoggingContext taskLoggingContext,
            ContinueOnError continueOnError)
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
                if (continueOnError != ContinueOnError.ErrorAndStop)
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

            if (continueOnError == ContinueOnError.WarnAndContinue)
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
    }

    /// <summary>
    /// One prebound input expression, conversion, and setter call.
    /// </summary>
    internal readonly struct FastTaskInputOperation
    {
        private readonly CompiledTaskParameterProgram _source;
        private readonly TaskActionPropertyMetadata _property;
        private readonly ulong _requiredBit;

        private FastTaskInputOperation(
            CompiledTaskParameterProgram source,
            TaskActionPropertyMetadata property,
            ulong requiredBit)
        {
            _source = source;
            _property = property;
            _requiredBit = requiredBit;
        }

        internal bool IsValid => _property != null;

        internal bool RequiresTaskEnvironment
        {
            get
            {
                Type parameterType = _property.ParameterType;
                if (parameterType.IsArray)
                {
                    parameterType = parameterType.GetElementType();
                }

                return parameterType == typeof(AbsolutePath) ||
                    parameterType == typeof(FileInfo) ||
                    parameterType == typeof(DirectoryInfo);
            }
        }

        internal static FastTaskInputOperation TryCreate(
            CompiledTaskParameterProgram source,
            TaskActionPropertyMetadata property,
            ulong requiredBit)
        {
            Type parameterType = property.ParameterType;
            bool validParameterType = parameterType.IsArray
                ? TaskParameterTypeVerifier.IsValidVectorInputParameter(
                    parameterType)
                : TaskParameterTypeVerifier.IsValidScalarInputParameter(
                    parameterType);
            if (property.Setter == null ||
                !validParameterType ||
                TaskParameterTypeVerifier.TryGetSupportedTaskItemValueType(parameterType, out _) ||
                (parameterType.IsArray &&
                    TaskParameterTypeVerifier.TryGetSupportedTaskItemValueType(
                        parameterType.GetElementType(),
                        out _)))
            {
                return default;
            }

            if ((parameterType.IsArray &&
                    source.Kind != CompiledTaskValueKind.ItemVector) ||
                (parameterType == typeof(ITaskItem) &&
                    source.Kind != CompiledTaskValueKind.ItemVector) ||
                (!parameterType.IsArray &&
                    source.Kind == CompiledTaskValueKind.ItemVector &&
                    parameterType != typeof(ITaskItem)))
            {
                return default;
            }

            return new FastTaskInputOperation(
                source,
                property,
                requiredBit);
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
                    _source.Location,
                    "InvalidTaskParameterValueError",
                    GetDisplayValue(frame),
                    _source.Name,
                    _property.ParameterType.FullName,
                    frame.TaskInstance.Name);
                return false;
            }

            if (!success)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_source.Location),
                    "InvalidTaskAttributeError",
                    _source.Name,
                    _source.Value,
                    frame.TaskInstance.Name);
                return false;
            }

            if (parameterSet)
            {
                requiredSet |= _requiredBit;
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
                ICollection<ProjectItemInstance> items =
                    frame.Lookup.GetItems(_source.ItemType);

                if (items == null || items.Count == 0)
                {
                    return true;
                }

                if (items.Count != 1)
                {
                    ProjectErrorUtilities.ThrowInvalidProject(
                        _source.Location,
                        "CannotPassMultipleItemsIntoScalarParameter",
                        _source.Value,
                        _source.Name,
                        parameterType.FullName,
                        frame.TaskInstance.Name);
                }

                ProjectItemInstance item = null;
                foreach (ProjectItemInstance candidate in items)
                {
                    item = candidate;
                    break;
                }

                parameterSet = true;
                return SetValue(
                    task,
                    new TaskItem(item),
                    taskLoggingContext,
                    frame);
            }

            string expandedValue = _source.ScalarProgram.Evaluate(
                frame,
                _source.Location);
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
            ICollection<ProjectItemInstance> items =
                frame.Lookup.GetItems(_source.ItemType);
            int itemCount = items?.Count ?? 0;
            IEnumerable<ProjectItemInstance> visibleItems =
                items ?? Array.Empty<ProjectItemInstance>();

            parameterSet = itemCount > 0 || _requiredBit != 0;
            if (!parameterSet)
            {
                return true;
            }

            Type parameterType = _property.ParameterType;
            object value;
            if (parameterType == typeof(ITaskItem[]))
            {
                var values = new ITaskItem[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in visibleItems)
                {
                    values[index++] = new TaskItem(item);
                }

                value = values;
            }
            else if (parameterType == typeof(string[]))
            {
                var values = new string[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in visibleItems)
                {
                    values[index++] = item.EvaluatedInclude;
                }

                value = values;
            }
            else if (parameterType == typeof(bool[]))
            {
                var values = new bool[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in visibleItems)
                {
                    values[index++] =
                        ConversionUtilities.ConvertStringToBool(
                            item.EvaluatedInclude);
                }

                value = values;
            }
            else
            {
#if NET
                Array values =
                    Array.CreateInstanceFromArrayType(
                        parameterType,
                        itemCount);
#else
                Array values =
                    Array.CreateInstance(
                        parameterType.GetElementType(),
                        itemCount);
#endif
                Type elementType = parameterType.GetElementType();
                int index = 0;
                foreach (ProjectItemInstance item in visibleItems)
                {
                    values.SetValue(
                        ConvertStringToValue(
                            item.EvaluatedInclude,
                            elementType,
                            frame.RequestEntry.TaskEnvironment),
                        index++);
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
                    new BuildEventFileInfo(_source.Location),
                    frame.TaskInstance.Name);
            }
            catch (Exception e)
            {
                taskLoggingContext.LogFatalTaskError(
                    e,
                    new BuildEventFileInfo(_source.Location),
                    frame.TaskInstance.Name);
            }

            return false;
        }

        private string GetDisplayValue(
            FastTaskExecutionFrame frame) =>
            _source.Kind == CompiledTaskValueKind.Scalar
                ? _source.ScalarProgram.Evaluate(
                    frame,
                    _source.Location)
                : _source.Value;

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
    /// One prebound task getter and item lookup publication.
    /// </summary>
    internal readonly struct FastTaskOutputOperation
    {
        private readonly CompiledTaskOutputProgram _source;
        private readonly TaskActionPropertyMetadata _property;
        private readonly string _destinationName;

        private FastTaskOutputOperation(
            CompiledTaskOutputProgram source,
            TaskActionPropertyMetadata property,
            string destinationName)
        {
            _source = source;
            _property = property;
            _destinationName = destinationName;
        }

        internal bool IsValid => _property != null;

        internal static FastTaskOutputOperation TryCreate(
            CompiledTaskOutputProgram source,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType,
            TaskActionTypeMetadata metadata)
        {
            if (!source.IsItem ||
                !string.IsNullOrEmpty(source.Condition) ||
                ContainsExpansion(source.TaskParameter) ||
                ContainsExpansion(source.DestinationName))
            {
                return default;
            }

            string destinationName =
                EscapingUtilities.UnescapeAll(source.DestinationName);
            if (!XmlUtilities.IsValidElementName(destinationName))
            {
                return default;
            }

            try
            {
                if (taskFactoryWrapper.GetProperty(
                        source.TaskParameter) == null)
                {
                    return default;
                }
            }
            catch (AmbiguousMatchException)
            {
                return default;
            }

            int propertyIndex = BoundTaskAction.FindPropertyIndex(
                loadedType,
                source.TaskParameter);
            if (propertyIndex < 0 ||
                !taskFactoryWrapper.GetNamesOfPropertiesWithOutputAttribute.ContainsKey(
                    source.TaskParameter))
            {
                return default;
            }

            TaskActionPropertyMetadata property =
                metadata.GetProperty(propertyIndex);
            Type parameterType = property.ParameterType;
            if (property.Getter == null ||
                !typeof(ITaskItem[]).IsAssignableFrom(parameterType))
            {
                return default;
            }

            return new FastTaskOutputOperation(
                source,
                property,
                destinationName);
        }

        internal bool Apply(
            FastTaskExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext)
        {
            try
            {
                var outputs = (ITaskItem[])_property.Getter(task);
                if (outputs == null)
                {
                    return true;
                }

                ProjectInstance project =
                    frame.RequestEntry.RequestConfiguration.Project;
                string locationEscaped = EscapingUtilities.Escape(
                    _source.Location.File,
                    cache: true);
                for (int i = 0; i < outputs.Length; i++)
                {
                    ITaskItem output = outputs[i];
                    if (output != null)
                    {
                        frame.Lookup.AddNewItem(
                            CreateOutputItem(
                                project,
                                output,
                                locationEscaped));
                    }
                }

                return true;
            }
            catch (InvalidOperationException e)
            {
                taskLoggingContext.LogError(
                    new BuildEventFileInfo(_source.Location),
                    "InvalidTaskItemsInTaskOutputs",
                    frame.TaskInstance.Name,
                    _source.TaskParameter,
                    e.Message);
                return false;
            }
            catch (TargetInvocationException e)
            {
                taskLoggingContext.LogFatalTaskError(
                    e.InnerException,
                    new BuildEventFileInfo(_source.Location),
                    frame.TaskInstance.Name);
                ProjectErrorUtilities.ThrowInvalidProject(
                    _source.Location,
                    "FailedToRetrieveTaskOutputs",
                    frame.TaskInstance.Name,
                    _source.TaskParameter,
                    e.InnerException?.Message);
                return false;
            }
            catch (Exception e)
                when (!ExceptionHandling.NotExpectedReflectionException(e))
            {
                ProjectErrorUtilities.ThrowInvalidProject(
                    _source.Location,
                    "FailedToRetrieveTaskOutputs",
                    frame.TaskInstance.Name,
                    _source.TaskParameter,
                    e.Message);
                return false;
            }
        }

        private ProjectItemInstance CreateOutputItem(
            ProjectInstance project,
            ITaskItem output,
            string locationEscaped)
        {
            ProjectItemInstance newItem;
            if (output is TaskItem outputAsProjectItem)
            {
                newItem = new ProjectItemInstance(
                    project,
                    _destinationName,
                    outputAsProjectItem.IncludeEscaped,
                    locationEscaped);
                newItem.SetMetadata(outputAsProjectItem.MetadataCollection);
                return newItem;
            }

            if (output is ITaskItem2 outputAsTaskItem2)
            {
                newItem = new ProjectItemInstance(
                    project,
                    _destinationName,
                    outputAsTaskItem2.EvaluatedIncludeEscaped,
                    locationEscaped);
                SerializableMetadata backingMetadata =
                    (output as IMetadataContainer)?.BackingMetadata ?? default;
                newItem.SetMetadataOnTaskOutput(
                    backingMetadata.HasValue
                        ? backingMetadata.Dictionary
                        : outputAsTaskItem2
                            .CloneCustomMetadataEscaped()
                            .Cast<KeyValuePair<string, string>>());
                return newItem;
            }

            newItem = new ProjectItemInstance(
                project,
                _destinationName,
                EscapingUtilities.Escape(output.ItemSpec),
                locationEscaped);
            newItem.SetMetadataOnTaskOutput(
                EnumerateMetadata(output.CloneCustomMetadata()));
            return newItem;
        }

        private static IEnumerable<KeyValuePair<string, string>> EnumerateMetadata(
            IDictionary metadata)
        {
            if (metadata is CopyOnWriteDictionary<string> copyOnWriteDictionary)
            {
                foreach (KeyValuePair<string, string> pair in copyOnWriteDictionary)
                {
                    yield return new KeyValuePair<string, string>(
                        pair.Key,
                        EscapingUtilities.Escape(pair.Value));
                }
            }
            else if (metadata is Dictionary<string, string> dictionary)
            {
                foreach (KeyValuePair<string, string> pair in dictionary)
                {
                    yield return new KeyValuePair<string, string>(
                        pair.Key,
                        EscapingUtilities.Escape(pair.Value));
                }
            }
            else
            {
                foreach (DictionaryEntry entry in metadata)
                {
                    yield return new KeyValuePair<string, string>(
                        (string)entry.Key,
                        EscapingUtilities.Escape((string)entry.Value));
                }
            }
        }

        private static bool ContainsExpansion(string value) =>
            value.Contains("$(", StringComparison.Ordinal) ||
            value.Contains("@(", StringComparison.Ordinal) ||
            value.Contains("%(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reused dynamic state for all fast actions in one target bucket.
    /// </summary>
    internal sealed class FastTaskExecutionFrame :
        IDisposable,
        ICompiledExpressionEnvironment
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

        string ICompiledExpressionEnvironment.GetEscapedPropertyValue(
            string propertyName,
            IElementLocation location)
        {
            ProjectPropertyInstance property = Lookup.GetProperty(propertyName);
            return property == null
                ? string.Empty
                : ((IProperty)property).GetEvaluatedValueEscaped(location);
        }

        internal void SetTaskInstance(ProjectTaskInstance taskInstance) => TaskInstance = taskInstance;

        internal void SetCurrentTask(
            ITask task,
            TaskLoggingContext taskLoggingContext,
            CompiledTaskSourceProgram template) =>
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
        private CompiledTaskSourceProgram _currentTemplate;

        internal FastTaskCancellationState(CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(
                static state => ((FastTaskCancellationState)state).CancelCurrentTask(),
                this);
        }

        internal void SetCurrentTask(
            ITask task,
            TaskLoggingContext taskLoggingContext,
            CompiledTaskSourceProgram template)
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
                CompiledTaskSourceProgram template = _currentTemplate;
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
