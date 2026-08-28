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
using System.Threading.Tasks;
using Microsoft.Build.BackEnd.Components.RequestBuilder;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Eventing;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Experimental.BuildCheck.Infrastructure;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using EngineFileUtilities = Microsoft.Build.Internal.EngineFileUtilities;
using ProjectItemInstanceFactory = Microsoft.Build.Execution.ProjectItemInstance.TaskItem.ProjectItemInstanceFactory;
using TaskItem = Microsoft.Build.Execution.ProjectItemInstance.TaskItem;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    internal enum FastTaskEnvironmentMode : byte
    {
        None,
        ProjectRooted,
        AmbientProcess,
    }

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

        internal bool CanExecute(CompiledTargetExecutionFrame frame) =>
            _action.CanExecute(frame);

        internal WorkUnitResult Execute(CompiledTargetExecutionFrame frame) =>
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
        private readonly FastTaskEnvironmentMode _environmentMode;
        private readonly bool _requiresTaskEnvironment;

        private FastTaskAction(
            CompiledTaskSourceProgram program,
            LoadedType loadedType,
            CompiledConditionProgram condition,
            CompiledScalarProgram conditionDisplay,
            CompiledScalarProgram continueOnError,
            FastTaskInputOperation[] inputs,
            FastTaskOutputOperation[] outputs,
            string[] requiredParameterNames,
            ulong allRequiredParameters,
            FastTaskEnvironmentMode environmentMode,
            bool requiresTaskEnvironment)
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
            _environmentMode = environmentMode;
            _requiresTaskEnvironment = requiresTaskEnvironment;
        }

        internal Type TaskType => _loadedType.Type;

        internal FastTaskEnvironmentMode EnvironmentMode => _environmentMode;

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
            bool requiresProjectRootedPathConversion = false;
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

                requiresProjectRootedPathConversion |=
                    inputs[i].RequiresTaskEnvironment;
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

            bool requiresTaskEnvironment =
                typeof(IMultiThreadableTask).IsAssignableFrom(loadedType.Type) ||
                loadedType.RequiresTaskEnvironmentForConstruction;
            FastTaskEnvironmentMode environmentMode =
                !TaskRouter.IsMultiThreadableTask(loadedType.Type)
                    ? FastTaskEnvironmentMode.AmbientProcess
                    : requiresTaskEnvironment ||
                        requiresProjectRootedPathConversion
                        ? FastTaskEnvironmentMode.ProjectRooted
                        : FastTaskEnvironmentMode.None;

            return new FastTaskAction(
                program,
                loadedType,
                program.ConditionProgram,
                program.ConditionDisplayProgram,
                program.ContinueOnErrorProgram,
                inputs,
                outputs,
                requiredParameterNames,
                allRequiredParameters,
                environmentMode,
                requiresTaskEnvironment);
        }

        internal bool CanExecute(CompiledTargetExecutionFrame frame)
        {
            return frame.RequestEntry.Request.HostServices == null &&
                !frame.Host.BuildParameters.LogTaskInputs &&
                !Traits.Instance.EscapeHatches.LogTaskInputs &&
                (!frame.Host.BuildParameters.MultiThreaded ||
                    !TaskRouter.NeedsTaskHostInMultiThreadedMode(TaskType));
        }

        internal WorkUnitResult Execute(
            CompiledTargetExecutionFrame frame,
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
            CompiledTargetExecutionFrame frame,
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

            bool taskEnvironmentInitialized = false;
            TaskEnvironment taskEnvironment = null;
            AbsolutePath projectRootDirectory = default;
            IDisposable taskEnvironmentScope = null;
            FastTaskEnvironmentMode executionEnvironmentMode =
                !frame.Host.BuildParameters.SaveOperatingEnvironment &&
                _environmentMode != FastTaskEnvironmentMode.AmbientProcess
                    ? FastTaskEnvironmentMode.AmbientProcess
                    : _environmentMode;
            if (executionEnvironmentMode != FastTaskEnvironmentMode.None)
            {
                using (BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskEnvironment,
                           _template.Name,
                           frame.TargetLoggingContext.Target.Name))
                {
                    if (executionEnvironmentMode == FastTaskEnvironmentMode.AmbientProcess)
                    {
                        taskEnvironment = frame.RequestEntry.TaskEnvironment;
                        if (frame.Host.BuildParameters.SaveOperatingEnvironment)
                        {
                            taskEnvironment.ProjectDirectory =
                                new AbsolutePath(frame.RequestEntry.ProjectRootDirectory, ignoreRootedCheck: true);
                            taskEnvironmentInitialized = true;
                        }
                    }
                    else
                    {
                        taskEnvironment = frame.RequestEntry.TaskEnvironment;
                        projectRootDirectory =
                            new AbsolutePath(frame.RequestEntry.ProjectRootDirectory, ignoreRootedCheck: true);
                        if (_requiresTaskEnvironment)
                        {
                            if (!taskEnvironment.IsMultiThreaded)
                            {
                                taskEnvironmentScope =
                                    taskEnvironment.EnterProjectDirectoryScope(
                                            projectRootDirectory);
                            }
                        }
                    }
                }
            }

            using IDisposable taskEnvironmentScopeLifetime =
                taskEnvironmentScope;

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

            if (taskEnvironmentInitialized)
            {
                executionContext.MarkTaskEnvironmentInitialized();
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
                        taskFactoryWrapper,
                        taskEnvironment);
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
                        multiThreadableTask.TaskEnvironment = taskEnvironment;
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
                    SetInputs(
                        frame,
                        task,
                        taskLoggingContext,
                        executionEnvironmentMode,
                        taskEnvironment,
                        projectRootDirectory);
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
            CompiledTargetExecutionFrame frame,
            TaskLoggingContext taskLoggingContext,
            TaskFactoryWrapper taskFactoryWrapper,
            TaskEnvironment taskEnvironment)
        {
            var assemblyTaskFactory =
                (AssemblyTaskFactory)taskFactoryWrapper.TaskFactory;
            assemblyTaskFactory.RecordTaskExecutionTelemetry(taskLoggingContext, isTaskHost: false);

            try
            {
                ITask task = _loadedType.CreateInstance(taskEnvironment);
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
            CompiledTargetExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            FastTaskEnvironmentMode executionEnvironmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory)
        {
            ulong requiredSet = 0;
            for (int i = 0; i < _inputs.Length; i++)
            {
                if (!_inputs[i].Apply(
                        frame,
                        task,
                        taskLoggingContext,
                        executionEnvironmentMode,
                        taskEnvironment,
                        projectRootDirectory,
                        ref requiredSet))
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
            CompiledTargetExecutionFrame frame,
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
            CompiledTargetExecutionFrame frame,
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
            CompiledTargetExecutionFrame frame,
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
    internal enum FastTaskInputKind : byte
    {
        ScalarValue,
        ScalarTaskItem,
        VectorTaskItem,
        VectorString,
        VectorBoolean,
        VectorValue,
    }

    internal enum FastTaskValueConversionKind : byte
    {
        General,
        String,
        Boolean,
        AbsolutePath,
        FileInfo,
        DirectoryInfo,
    }

    internal readonly struct FastTaskInputOperation
    {
        private readonly CompiledTaskParameterProgram _source;
        private readonly TaskActionPropertyMetadata _property;
        private readonly ulong _requiredBit;
        private readonly FastTaskInputKind _kind;
        private readonly FastTaskValueConversionKind _conversionKind;
        private readonly string _constantScalarValue;
        private readonly object _emptyVector;

        private FastTaskInputOperation(
            CompiledTaskParameterProgram source,
            TaskActionPropertyMetadata property,
            ulong requiredBit,
            FastTaskInputKind kind,
            FastTaskValueConversionKind conversionKind,
            string constantScalarValue,
            object emptyVector)
        {
            _source = source;
            _property = property;
            _requiredBit = requiredBit;
            _kind = kind;
            _conversionKind = conversionKind;
            _constantScalarValue = constantScalarValue;
            _emptyVector = emptyVector;
        }

        internal bool IsValid => _property != null;

        internal bool RequiresTaskEnvironment =>
            _conversionKind == FastTaskValueConversionKind.AbsolutePath ||
            _conversionKind == FastTaskValueConversionKind.FileInfo ||
            _conversionKind == FastTaskValueConversionKind.DirectoryInfo;

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

            FastTaskInputKind kind;
            Type valueType;
            object emptyVector = null;
            if (parameterType.IsArray)
            {
                valueType = parameterType.GetElementType();
                kind = parameterType == typeof(ITaskItem[])
                    ? FastTaskInputKind.VectorTaskItem
                    : parameterType == typeof(string[])
                        ? FastTaskInputKind.VectorString
                        : parameterType == typeof(bool[])
                            ? FastTaskInputKind.VectorBoolean
                            : FastTaskInputKind.VectorValue;
#if NET
                emptyVector =
                    Array.CreateInstanceFromArrayType(parameterType, 0);
#else
                emptyVector = Array.CreateInstance(valueType, 0);
#endif
            }
            else
            {
                valueType = parameterType;
                kind = parameterType == typeof(ITaskItem)
                    ? FastTaskInputKind.ScalarTaskItem
                    : FastTaskInputKind.ScalarValue;
            }

            string constantScalarValue = null;
            source.ScalarProgram?.TryEvaluateConstant(
                source.Location,
                out constantScalarValue);

            return new FastTaskInputOperation(
                source,
                property,
                requiredBit,
                kind,
                GetConversionKind(valueType),
                constantScalarValue,
                emptyVector);
        }

        internal bool Apply(
            CompiledTargetExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory,
            ref ulong requiredSet)
        {
            bool parameterSet;
            bool success;
            try
            {
                success = _kind >= FastTaskInputKind.VectorTaskItem
                    ? ApplyVector(
                        frame,
                        task,
                        taskLoggingContext,
                        environmentMode,
                        taskEnvironment,
                        projectRootDirectory,
                        out parameterSet)
                    : ApplyScalar(
                        frame,
                        task,
                        taskLoggingContext,
                        environmentMode,
                        taskEnvironment,
                        projectRootDirectory,
                        out parameterSet);
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
                    GetDisplayValue(frame, environmentMode, taskEnvironment),
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
            CompiledTargetExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory,
            out bool parameterSet)
        {
            parameterSet = false;
            Type parameterType = _property.ParameterType;

            if (_kind == FastTaskInputKind.ScalarTaskItem)
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

            string expandedValue =
                _constantScalarValue ??
                _source.ScalarProgram.Evaluate(
                    frame,
                    _source.Location,
                    GetScalarBaseDirectory(
                        frame,
                        environmentMode,
                        taskEnvironment));
            if (expandedValue.Length == 0)
            {
                return true;
            }

            parameterSet = true;
            return SetValue(
                task,
                ConvertStringToValue(
                    expandedValue,
                    parameterType,
                    environmentMode,
                    taskEnvironment,
                    projectRootDirectory),
                taskLoggingContext,
                frame);
        }

        private bool ApplyVector(
            CompiledTargetExecutionFrame frame,
            ITask task,
            TaskLoggingContext taskLoggingContext,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory,
            out bool parameterSet)
        {
            ICollection<ProjectItemInstance> items =
                frame.Lookup.GetItems(_source.ItemType);
            int itemCount = items?.Count ?? 0;

            parameterSet = itemCount > 0 || _requiredBit != 0;
            if (!parameterSet)
            {
                return true;
            }

            if (itemCount == 0)
            {
                return SetValue(
                    task,
                    _emptyVector,
                    taskLoggingContext,
                    frame);
            }

            Type parameterType = _property.ParameterType;
            object value;
            if (_kind == FastTaskInputKind.VectorTaskItem)
            {
                var values = new ITaskItem[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in items)
                {
                    values[index++] = new TaskItem(item);
                }

                value = values;
            }
            else if (_kind == FastTaskInputKind.VectorString)
            {
                var values = new string[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in items)
                {
                    values[index++] = item.EvaluatedInclude;
                }

                value = values;
            }
            else if (_kind == FastTaskInputKind.VectorBoolean)
            {
                var values = new bool[itemCount];
                int index = 0;
                foreach (ProjectItemInstance item in items)
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
                foreach (ProjectItemInstance item in items)
                {
                    values.SetValue(
                        ConvertStringToValue(
                            item.EvaluatedInclude,
                            elementType,
                            environmentMode,
                            taskEnvironment,
                            projectRootDirectory),
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
            CompiledTargetExecutionFrame frame)
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
            CompiledTargetExecutionFrame frame,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment) =>
            _source.Kind == CompiledTaskValueKind.Scalar
                ? _source.ScalarProgram.Evaluate(
                    frame,
                    _source.Location,
                    GetScalarBaseDirectory(
                        frame,
                        environmentMode,
                        taskEnvironment))
                : _source.Value;

        private static string GetScalarBaseDirectory(
            CompiledTargetExecutionFrame frame,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment) =>
            environmentMode == FastTaskEnvironmentMode.AmbientProcess
                ? taskEnvironment.ProjectDirectory.Value
                : frame.RequestEntry.ProjectRootDirectory;

        private object ConvertStringToValue(
            string value,
            Type targetType,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory)
        {
            if (_conversionKind == FastTaskValueConversionKind.String)
            {
                return value;
            }

            if (_conversionKind == FastTaskValueConversionKind.Boolean)
            {
                return ConversionUtilities.ConvertStringToBool(value);
            }

            if (_conversionKind == FastTaskValueConversionKind.AbsolutePath)
            {
                return GetAbsolutePath(
                    value,
                    environmentMode,
                    taskEnvironment,
                    projectRootDirectory);
            }

            if (_conversionKind == FastTaskValueConversionKind.FileInfo)
            {
                return new FileInfo(
                    GetAbsolutePath(
                        value,
                        environmentMode,
                        taskEnvironment,
                        projectRootDirectory).Value);
            }

            if (_conversionKind == FastTaskValueConversionKind.DirectoryInfo)
            {
                return new DirectoryInfo(
                    GetAbsolutePath(
                        value,
                        environmentMode,
                        taskEnvironment,
                        projectRootDirectory).Value);
            }

            return ValueTypeParser.Parse(value, targetType);
        }

        private static AbsolutePath GetAbsolutePath(
            string value,
            FastTaskEnvironmentMode environmentMode,
            TaskEnvironment taskEnvironment,
            AbsolutePath projectRootDirectory) =>
            environmentMode == FastTaskEnvironmentMode.ProjectRooted
                ? new AbsolutePath(value, projectRootDirectory)
                : taskEnvironment.GetAbsolutePath(value);

        private static FastTaskValueConversionKind GetConversionKind(
            Type valueType)
        {
            if (valueType == typeof(string))
            {
                return FastTaskValueConversionKind.String;
            }

            if (valueType == typeof(bool))
            {
                return FastTaskValueConversionKind.Boolean;
            }

            if (valueType == typeof(AbsolutePath))
            {
                return FastTaskValueConversionKind.AbsolutePath;
            }

            if (valueType == typeof(FileInfo))
            {
                return FastTaskValueConversionKind.FileInfo;
            }

            if (valueType == typeof(DirectoryInfo))
            {
                return FastTaskValueConversionKind.DirectoryInfo;
            }

            return FastTaskValueConversionKind.General;
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
            CompiledTargetExecutionFrame frame,
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
    /// Reused dynamic state for all compiled actions in one target bucket.
    /// </summary>
    internal sealed class CompiledTargetExecutionFrame :
        IDisposable,
        ICompiledExpressionEnvironment
    {
        private FastTaskCancellationState _cancellationState;
        private Expander<
            ProjectPropertyInstance,
            ProjectItemInstance> _fastTaskExpander;
        private Expander<
            ProjectPropertyInstance,
            ProjectItemInstance> _executionConditionExpander;
        private Expander<
            ProjectPropertyInstance,
            ProjectItemInstance> _inferenceConditionExpander;
        private CompiledLookupExpressionEnvironment
            _executionExpressionEnvironment;
        private CompiledLookupExpressionEnvironment
            _inferenceExpressionEnvironment;

        internal CompiledTargetExecutionFrame(
            IBuildComponentHost host,
            BuildRequestEntry requestEntry,
            ITargetBuilderCallback targetBuilderCallback,
            TargetLoggingContext targetLoggingContext,
            ITaskBuilder taskBuilder,
            TaskExecutionMode mode,
            Lookup lookupForInference,
            Lookup lookupForExecution,
            CancellationToken cancellationToken)
        {
            Host = host;
            RequestEntry = requestEntry;
            TargetBuilderCallback = targetBuilderCallback;
            TargetLoggingContext = targetLoggingContext;
            TaskBuilder = taskBuilder;
            Mode = mode;
            LookupForInference = lookupForInference;
            LookupForExecution = lookupForExecution;
            Lookup = lookupForExecution;
            CancellationToken = cancellationToken;
        }

        internal IBuildComponentHost Host { get; }

        internal BuildRequestEntry RequestEntry { get; }

        internal ITargetBuilderCallback TargetBuilderCallback { get; }

        internal TargetLoggingContext TargetLoggingContext { get; }

        internal ITaskBuilder TaskBuilder { get; }

        internal TaskExecutionMode Mode { get; }

        internal ProjectTaskInstance TaskInstance { get; private set; }

        internal Lookup Lookup { get; }

        internal Lookup LookupForInference { get; }

        internal Lookup LookupForExecution { get; }

        internal CancellationToken CancellationToken { get; }

        internal bool IsCancellationRequested =>
            CancellationToken.IsCancellationRequested;

        internal Expander<ProjectPropertyInstance, ProjectItemInstance>
            Expander =>
            _fastTaskExpander ??= CreateExpander(LookupForExecution);

        string ICompiledExpressionEnvironment.GetEscapedPropertyValue(
            string propertyName,
            IElementLocation location) =>
            Expander.GetEscapedPropertyValue(propertyName, location);

        string ICompiledExpressionEnvironment.ExpandItems(
            string escapedValue,
            IElementLocation location) =>
            Expander.ExpandIntoStringLeaveEscaped(
                escapedValue,
                ExpanderOptions.ExpandItems,
                location);

        void ICompiledExpressionEnvironment.EnterConditionEvaluation(
            bool oneSideIsEmpty)
        {
            Expander.PropertiesUseTracker.PropertyReadContext =
                oneSideIsEmpty
                    ? PropertyReadContext
                        .ConditionEvaluationWithOneSideEmpty
                    : PropertyReadContext.ConditionEvaluation;
        }

        void ICompiledExpressionEnvironment.LeaveConditionEvaluation() =>
            Expander.PropertiesUseTracker.ResetPropertyReadContext();

        internal ValueTask<WorkUnitResult> ExecuteAsync(
            CompiledTargetActionRecord record)
        {
            if (record.Kind == CompiledTargetActionKind.PropertyGroup &&
                record.PropertyGroupAction != null)
            {
                return new ValueTask<WorkUnitResult>(
                    ExecuteCompiledPropertyGroupAction(record));
            }

            if (record.Kind == CompiledTargetActionKind.ItemGroup &&
                record.ItemGroupAction != null)
            {
                return new ValueTask<WorkUnitResult>(
                    ExecuteCompiledItemGroupAction(record));
            }

            if (record.Kind == CompiledTargetActionKind.PropertyGroup ||
                record.Kind == CompiledTargetActionKind.ItemGroup)
            {
                return new ValueTask<WorkUnitResult>(
                    ExecuteIntrinsicAction(record));
            }

            CompiledTaskAction action = record.TaskAction;
            ProjectTaskInstance taskInstance =
                record.Child as ProjectTaskInstance;
            string fastTaskName = taskInstance?.Name;
            long fastTaskSiteStart =
                action == null
                    ? 0
                    : BuildExecutionInstrumentation.StartTimestamp();
            FastTaskInvocation fastInvocation;
            using (action != null
                       ? BuildExecutionInstrumentation.MeasureFastTaskDetail(
                           BuildExecutionMetric.FastTaskLookup,
                           fastTaskName,
                           TargetLoggingContext.Target.Name)
                       : default)
            {
                fastInvocation = action == null
                    ? default
                    : action.GetFastInvocation();
            }

            if (fastInvocation.IsValid &&
                Mode == TaskExecutionMode.ExecuteTaskAndGatherOutputs &&
                taskInstance != null)
            {
                SetTaskInstance(taskInstance);
                if (fastInvocation.CanExecute(this))
                {
                    try
                    {
                        return new ValueTask<WorkUnitResult>(
                            fastInvocation.Execute(this));
                    }
                    finally
                    {
                        BuildExecutionInstrumentation.RecordSince(
                            BuildExecutionMetric.FastTaskSite,
                            fastTaskSiteStart,
                            taskInstance.Name,
                            TargetLoggingContext.Target.Name);
                    }
                }
            }

            return new ValueTask<WorkUnitResult>(
                TaskBuilder.ExecuteTask(
                    TargetLoggingContext,
                    RequestEntry,
                    TargetBuilderCallback,
                    record.Child,
                    action,
                    Mode,
                    LookupForInference,
                    LookupForExecution,
                    CancellationToken));
        }

        private WorkUnitResult ExecuteCompiledPropertyGroupAction(
            CompiledTargetActionRecord record)
        {
            WorkUnitResult result = new(
                WorkUnitResultCode.Failed,
                WorkUnitActionCode.Stop,
                null);

            if ((Mode & TaskExecutionMode.InferOutputsOnly) ==
                TaskExecutionMode.InferOutputsOnly)
            {
                result = ExecuteCompiledPropertyGroup(
                    record,
                    LookupForInference);
            }

            if ((Mode & TaskExecutionMode.ExecuteTaskAndGatherOutputs) ==
                TaskExecutionMode.ExecuteTaskAndGatherOutputs)
            {
                result = ExecuteCompiledPropertyGroup(
                    record,
                    LookupForExecution);
            }

            return result;
        }

        private WorkUnitResult ExecuteCompiledPropertyGroup(
            CompiledTargetActionRecord record,
            Lookup lookup)
        {
            CompiledPropertyGroupAction action =
                record.PropertyGroupAction;
            CompiledLookupExpressionEnvironment environment =
                GetExpressionEnvironment(lookup);
            if (!action.EvaluateCondition(environment))
            {
                return new WorkUnitResult(
                    WorkUnitResultCode.Skipped,
                    WorkUnitActionCode.Continue,
                    null);
            }

            using var intrinsicTaskMeasurement =
                BuildExecutionInstrumentation.Measure(
                    BuildExecutionMetric.IntrinsicTask,
                    BuildExecutionInstrumentation.DetailsEnabled
                        ? record.Child.GetType().Name
                        : null,
                    TargetLoggingContext.Target.Name);
            using var compiledPropertyGroupMeasurement =
                BuildExecutionInstrumentation.Measure(
                    BuildExecutionMetric.CompiledPropertyGroup,
                    parentName: TargetLoggingContext.Target.Name);
            try
            {
                bool logTaskInputs =
                    Host.BuildParameters.LogTaskInputs ||
                    Traits.Instance.EscapeHatches.LogTaskInputs;
                ProjectInstance project =
                    RequestEntry.RequestConfiguration.Project;
                PropertyTrackingSetting propertyTrackingSettings =
                    (PropertyTrackingSetting)
                    Traits.Instance.LogPropertyTracking;
                PropertiesUseTracker propertiesUseTracker =
                    environment.Expander.PropertiesUseTracker;

                for (int assignmentIndex = 0;
                     assignmentIndex < action.AssignmentCount;
                     assignmentIndex++)
                {
                    CompiledPropertyAssignment assignment =
                        action.GetAssignment(assignmentIndex);
                    ProjectPropertyGroupTaskPropertyInstance property =
                        assignment.Property;
                    if (assignment.Condition != null &&
                        !assignment.Condition.Evaluate(
                            environment,
                            property.ConditionLocation))
                    {
                        continue;
                    }

                    try
                    {
                        ProjectErrorUtilities.VerifyThrowInvalidProject(
                            !ReservedPropertyNames.IsReservedProperty(
                                property.Name),
                            property.Location,
                            "CannotModifyReservedProperty",
                            property.Name);

                        propertiesUseTracker
                                .CurrentlyEvaluatingPropertyElementName =
                            property.Name;
                        propertiesUseTracker.PropertyReadContext =
                            PropertyReadContext.PropertyEvaluation;

                        string evaluatedValue =
                            assignment.Value.EvaluateLeaveEscaped(
                                environment,
                                property.Location);
                        propertiesUseTracker.CheckPreexistingUndefinedUsage(
                            property,
                            evaluatedValue,
                            TargetLoggingContext);

                        PropertyTrackingUtils.LogPropertyAssignment(
                            propertyTrackingSettings,
                            property.Name,
                            evaluatedValue,
                            property.Location,
                            project.GetProperty(property.Name)
                                ?.EvaluatedValue,
                            TargetLoggingContext);

                        if (logTaskInputs &&
                            !TargetLoggingContext.LoggingService
                                .OnlyLogCriticalEvents)
                        {
                            TargetLoggingContext.LogComment(
                                MessageImportance.Low,
                                "PropertyGroupLogMessage",
                                property.Name,
                                evaluatedValue);
                        }

                        lookup.SetProperty(
                            ProjectPropertyInstance.Create(
                                property.Name,
                                evaluatedValue,
                                property.Location,
                                project.IsImmutable));
                        TargetLoggingContext.ProcessPropertyWrite(
                            new PropertyWriteInfo(
                                property.Name,
                                string.IsNullOrEmpty(evaluatedValue),
                                property.Location));
                    }
                    finally
                    {
                        propertiesUseTracker.ResetPropertyGroupAssignment();
                    }
                }

                return new WorkUnitResult(
                    WorkUnitResultCode.Success,
                    WorkUnitActionCode.Continue,
                    null);
            }
            catch (InvalidProjectFileException exception)
            {
                TargetLoggingContext.LogInvalidProjectFileError(exception);
                return new WorkUnitResult(
                    WorkUnitResultCode.Failed,
                    WorkUnitActionCode.Stop,
                    exception);
            }
        }

        private WorkUnitResult ExecuteIntrinsicAction(
            CompiledTargetActionRecord record)
        {
            WorkUnitResult result = new(
                WorkUnitResultCode.Failed,
                WorkUnitActionCode.Stop,
                null);

            if ((Mode & TaskExecutionMode.InferOutputsOnly) ==
                TaskExecutionMode.InferOutputsOnly)
            {
                result = ExecuteIntrinsic(
                    record,
                    LookupForInference);
            }

            if ((Mode & TaskExecutionMode.ExecuteTaskAndGatherOutputs) ==
                TaskExecutionMode.ExecuteTaskAndGatherOutputs)
            {
                result = ExecuteIntrinsic(
                    record,
                    LookupForExecution);
            }

            return result;
        }

        private WorkUnitResult ExecuteCompiledItemGroupAction(
            CompiledTargetActionRecord record)
        {
            WorkUnitResult result = new(
                WorkUnitResultCode.Failed,
                WorkUnitActionCode.Stop,
                null);

            if ((Mode & TaskExecutionMode.InferOutputsOnly) ==
                TaskExecutionMode.InferOutputsOnly)
            {
                result = ExecuteCompiledItemGroup(
                    record,
                    LookupForInference);
            }

            if ((Mode & TaskExecutionMode.ExecuteTaskAndGatherOutputs) ==
                TaskExecutionMode.ExecuteTaskAndGatherOutputs)
            {
                result = ExecuteCompiledItemGroup(
                    record,
                    LookupForExecution);
            }

            return result;
        }

        private WorkUnitResult ExecuteCompiledItemGroup(
            CompiledTargetActionRecord record,
            Lookup lookup)
        {
            CompiledItemGroupAction action = record.ItemGroupAction;
            CompiledLookupExpressionEnvironment environment =
                GetExpressionEnvironment(lookup);
            if (!action.EvaluateCondition(environment))
            {
                return new WorkUnitResult(
                    WorkUnitResultCode.Skipped,
                    WorkUnitActionCode.Continue,
                    null);
            }

            using var intrinsicTaskMeasurement =
                BuildExecutionInstrumentation.Measure(
                    BuildExecutionMetric.IntrinsicTask,
                    BuildExecutionInstrumentation.DetailsEnabled
                        ? record.Child.GetType().Name
                        : null,
                    TargetLoggingContext.Target.Name);
            using var compiledItemGroupMeasurement =
                BuildExecutionInstrumentation.Measure(
                    BuildExecutionMetric.CompiledItemGroup,
                    parentName: TargetLoggingContext.Target.Name);
            try
            {
                bool logTaskInputs =
                    Host.BuildParameters.LogTaskInputs ||
                    Traits.Instance.EscapeHatches.LogTaskInputs;
                ProjectInstance project =
                    RequestEntry.RequestConfiguration.Project;

                for (int operationIndex = 0;
                     operationIndex < action.OperationCount;
                     operationIndex++)
                {
                    CompiledItemOperation operation =
                        action.GetOperation(operationIndex);
                    ProjectItemGroupTaskItemInstance item =
                        operation.Item;
                    if (operation.Condition != null &&
                        !operation.Condition.EvaluateForItemGroup(
                            environment,
                            item.ConditionLocation))
                    {
                        continue;
                    }

                    switch (operation.Kind)
                    {
                        case CompiledItemOperationKind.Include:
                            ExecuteCompiledItemInclude(
                                operation,
                                environment,
                                lookup,
                                project,
                                logTaskInputs);
                            break;
                        case CompiledItemOperationKind.Remove:
                            ExecuteCompiledItemRemove(
                                operation,
                                environment,
                                lookup,
                                project,
                                logTaskInputs);
                            break;
                        case CompiledItemOperationKind.Modify:
                            ExecuteCompiledItemModify(
                                operation,
                                environment,
                                lookup);
                            break;
                        default:
                            throw new InternalErrorException(
                                "Unexpected compiled item operation.");
                    }
                }

                return new WorkUnitResult(
                    WorkUnitResultCode.Success,
                    WorkUnitActionCode.Continue,
                    null);
            }
            catch (InvalidProjectFileException exception)
            {
                TargetLoggingContext.LogInvalidProjectFileError(exception);
                return new WorkUnitResult(
                    WorkUnitResultCode.Failed,
                    WorkUnitActionCode.Stop,
                    exception);
            }
        }

        private void ExecuteCompiledItemInclude(
            CompiledItemOperation operation,
            CompiledLookupExpressionEnvironment environment,
            Lookup lookup,
            ProjectInstance project,
            bool logTaskInputs)
        {
            ProjectItemGroupTaskItemInstance item = operation.Item;
            HashSet<string> keepMetadata =
                EvaluateCompiledItemMetadataList(
                    operation.KeepMetadata,
                    environment,
                    item.KeepMetadataLocation);
            HashSet<string> removeMetadata =
                EvaluateCompiledItemMetadataList(
                    operation.RemoveMetadata,
                    environment,
                    item.RemoveMetadataLocation);
            ProjectErrorUtilities.VerifyThrowInvalidProject(
                !(keepMetadata != null && removeMetadata != null),
                item.KeepMetadataLocation,
                "KeepAndRemoveMetadataMutuallyExclusive");

            string evaluatedInclude =
                operation.Include?.EvaluateLeaveEscaped(
                    environment,
                    item.IncludeLocation) ?? string.Empty;

            List<string> excludes = null;
            if (evaluatedInclude.Length != 0 &&
                operation.Exclude != null)
            {
                string evaluatedExclude =
                    operation.Exclude.EvaluateLeaveEscaped(
                        environment,
                        item.ExcludeLocation);
                if (evaluatedExclude.Length != 0)
                {
                    excludes = environment.Expander
                        .ExpandIntoStringListLeaveEscaped(
                            evaluatedExclude,
                            ExpanderOptions.ExpandItems,
                            item.ExcludeLocation)
                        .ToList();
                }
            }

            var itemsToAdd = new List<ProjectItemInstance>();
            var itemFactory =
                new ProjectItemInstanceFactory(project, item.ItemType);
            bool expandedItemVector = false;

            if (evaluatedInclude.Length != 0)
            {
                foreach (string includeSplit
                    in ExpressionShredder.SplitSemiColonSeparatedList(
                        evaluatedInclude))
                {
                    IList<ProjectItemInstance> itemsFromSplit =
                        environment.Expander
                            .ExpandSingleItemVectorExpressionIntoItems(
                                includeSplit,
                                itemFactory,
                                ExpanderOptions.ExpandItems,
                                includeNullItems: false,
                                out _,
                                item.IncludeLocation);

                    if (itemsFromSplit != null)
                    {
                        itemsToAdd.AddRange(itemsFromSplit);
                        expandedItemVector = true;
                        continue;
                    }

                    string[] includeSplitFiles =
                        EngineFileUtilities.GetFileListEscaped(
                            project.Directory,
                            includeSplit,
                            excludes,
                            loggingMechanism: TargetLoggingContext,
                            includeLocation: item.IncludeLocation,
                            excludeLocation: item.ExcludeLocation,
                            disableExcludeDriveEnumerationWarning: true);
                    foreach (string includeSplitFile in includeSplitFiles)
                    {
                        itemsToAdd.Add(
                            new ProjectItemInstance(
                                project,
                                item.ItemType,
                                includeSplitFile,
                                includeSplit,
                                directMetadata: null,
                                itemDefinitions: null,
                                definingFileEscaped: item.Location.File,
                                useItemDefinitionsWithoutModification: false));
                    }
                }
            }

            if (expandedItemVector && excludes?.Count > 0)
            {
                HashSet<string> excludedPaths =
                    EvaluateCompiledExcludePaths(
                        excludes,
                        item.ExcludeLocation,
                        project);
                itemsToAdd.RemoveAll(
                    candidate =>
                        excludedPaths.Contains(
                            ((IItem)candidate).EvaluatedInclude
                                .NormalizeForPathComparison()));
            }

            FilterCompiledItemMetadata(
                itemsToAdd,
                keepMetadata,
                removeMetadata);

            Dictionary<string, string> metadata =
                EvaluateCompiledItemMetadata(operation, environment);
            ProjectItemInstance.SetMetadata(metadata, itemsToAdd);

            bool keepDuplicates =
                operation.KeepDuplicates?.EvaluateForItemGroup(
                    environment,
                    item.KeepDuplicatesLocation) ?? true;
            Action<IList> logFunction = null;
            if (logTaskInputs &&
                !TargetLoggingContext.LoggingService.OnlyLogCriticalEvents &&
                itemsToAdd.Count > 0)
            {
                logFunction = itemList =>
                    ItemGroupLoggingHelper.LogTaskParameter(
                        TargetLoggingContext,
                        TaskParameterMessageKind.AddItem,
                        parameterName: null,
                        propertyName: null,
                        item.ItemType,
                        itemList,
                        logItemMetadata: true,
                        item.Location);
            }

            lookup.AddNewItemsOfItemType(
                item.ItemType,
                itemsToAdd,
                doNotAddDuplicates: !keepDuplicates,
                logFunction);
        }

        private void ExecuteCompiledItemRemove(
            CompiledItemOperation operation,
            CompiledLookupExpressionEnvironment environment,
            Lookup lookup,
            ProjectInstance project,
            bool logTaskInputs)
        {
            ProjectItemGroupTaskItemInstance item = operation.Item;
            HashSet<string> matchOnMetadata =
                EvaluateCompiledItemMetadataList(
                    operation.MatchOnMetadata,
                    environment,
                    item.MatchOnMetadataLocation);
            ICollection<ProjectItemInstance> group =
                lookup.GetItems(item.ItemType);
            if (group == null || group.Count == 0)
            {
                return;
            }

            string evaluatedRemove =
                operation.Remove.EvaluateLeaveEscaped(
                    environment,
                    item.RemoveLocation);
            if (evaluatedRemove.Length == 0)
            {
                return;
            }

            List<ProjectItemInstance> itemsToRemove;
            if (matchOnMetadata != null)
            {
                var itemSpec =
                    new ItemSpec<
                        ProjectPropertyInstance,
                        ProjectItemInstance>(
                        evaluatedRemove,
                        environment.Expander,
                        item.RemoveLocation,
                        project.Directory,
                        expandProperties: false);
                ProjectFileErrorUtilities.VerifyThrowInvalidProjectFile(
                    itemSpec.Fragments.All(
                        fragment =>
                            fragment is ItemSpec<
                                ProjectPropertyInstance,
                                ProjectItemInstance>.ItemExpressionFragment),
                    BuildEventFileInfo.Empty,
                    "OM_MatchOnMetadataIsRestrictedToReferencedItems",
                    item.RemoveLocation,
                    item.Remove);
                var metadataSet =
                    new MetadataTrie<
                        ProjectPropertyInstance,
                        ProjectItemInstance>(
                        operation.MatchOnMetadataOptions,
                        matchOnMetadata,
                        itemSpec);
                itemsToRemove = group.Where(
                    candidate => metadataSet.Contains(
                        matchOnMetadata.Select(
                            metadataName =>
                                candidate.GetMetadataValue(
                                    metadataName)))).ToList();
            }
            else
            {
                var specificationsToFind =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (string piece
                    in environment.Expander
                        .ExpandIntoStringListLeaveEscaped(
                            evaluatedRemove,
                            ExpanderOptions.ExpandItems,
                            item.RemoveLocation))
                {
                    string[] fileList =
                        EngineFileUtilities.GetFileListEscaped(
                            project.Directory,
                            piece,
                            loggingMechanism: TargetLoggingContext,
                            includeLocation: item.RemoveLocation,
                            excludeLocation: item.RemoveLocation);
                    foreach (string file in fileList)
                    {
                        specificationsToFind.Add(
                            EscapingUtilities.UnescapeAll(file));
                    }
                }

                if (specificationsToFind.Count == 0)
                {
                    return;
                }

                itemsToRemove = new List<ProjectItemInstance>();
                foreach (ProjectItemInstance candidate in group)
                {
                    if (specificationsToFind.Contains(
                            candidate.EvaluatedInclude))
                    {
                        itemsToRemove.Add(candidate);
                    }
                }
            }

            if (itemsToRemove.Count == 0)
            {
                return;
            }

            if (logTaskInputs &&
                !TargetLoggingContext.LoggingService.OnlyLogCriticalEvents)
            {
                ItemGroupLoggingHelper.LogTaskParameter(
                    TargetLoggingContext,
                    TaskParameterMessageKind.RemoveItem,
                    parameterName: null,
                    propertyName: null,
                    item.ItemType,
                    itemsToRemove,
                    logItemMetadata: true,
                    item.Location);
            }

            lookup.RemoveItems(item.ItemType, itemsToRemove);
        }

        private void ExecuteCompiledItemModify(
            CompiledItemOperation operation,
            CompiledLookupExpressionEnvironment environment,
            Lookup lookup)
        {
            ProjectItemGroupTaskItemInstance item = operation.Item;
            HashSet<string> keepMetadata =
                EvaluateCompiledItemMetadataList(
                    operation.KeepMetadata,
                    environment,
                    item.KeepMetadataLocation);
            HashSet<string> removeMetadata =
                EvaluateCompiledItemMetadataList(
                    operation.RemoveMetadata,
                    environment,
                    item.RemoveMetadataLocation);
            ICollection<ProjectItemInstance> group =
                lookup.GetItems(item.ItemType);
            if (group == null || group.Count == 0)
            {
                return;
            }

            var metadataToSet = new Lookup.MetadataModifications(
                keepOnlySpecified: keepMetadata != null);
            if (keepMetadata != null)
            {
                foreach (string metadataName in keepMetadata)
                {
                    metadataToSet[metadataName] =
                        Lookup.MetadataModification.CreateFromNoChange();
                }
            }
            else if (removeMetadata != null)
            {
                foreach (string metadataName in removeMetadata)
                {
                    metadataToSet[metadataName] =
                        Lookup.MetadataModification.CreateFromRemove();
                }
            }

            foreach (CompiledItemMetadataAssignment assignment
                in operation.Metadata)
            {
                if (assignment.Condition != null &&
                    !assignment.Condition.EvaluateForItemGroup(
                        environment,
                        assignment.Metadata.ConditionLocation))
                {
                    continue;
                }

                string evaluatedValue =
                    EvaluateCompiledItemMetadataValue(
                        assignment,
                        environment);
                metadataToSet[assignment.Metadata.Name] =
                    Lookup.MetadataModification.CreateFromNewValue(
                        evaluatedValue);
            }

            lookup.ModifyItems(item.ItemType, group, metadataToSet);
        }

        private Dictionary<string, string> EvaluateCompiledItemMetadata(
            CompiledItemOperation operation,
            CompiledLookupExpressionEnvironment environment)
        {
            var metadata = new Dictionary<string, string>(
                MSBuildNameIgnoreCaseComparer.Default);
            foreach (CompiledItemMetadataAssignment assignment
                in operation.Metadata)
            {
                if (assignment.Condition != null &&
                    !assignment.Condition.EvaluateForItemGroup(
                        environment,
                        assignment.Metadata.ConditionLocation))
                {
                    continue;
                }

                metadata[assignment.Metadata.Name] =
                    EvaluateCompiledItemMetadataValue(
                        assignment,
                        environment);
            }

            return metadata;
        }

        private string EvaluateCompiledItemMetadataValue(
            CompiledItemMetadataAssignment assignment,
            CompiledLookupExpressionEnvironment environment)
        {
            string escapedValue =
                assignment.Value.EvaluateLeaveEscaped(
                    environment,
                    assignment.Metadata.Location);
            return environment.Expander.ExpandIntoStringLeaveEscaped(
                escapedValue,
                ExpanderOptions.ExpandItems,
                assignment.Metadata.Location);
        }

        private static HashSet<string> EvaluateCompiledItemMetadataList(
            CompiledScalarProgram program,
            CompiledLookupExpressionEnvironment environment,
            IElementLocation location)
        {
            if (program == null)
            {
                return null;
            }

            string evaluatedValue =
                program.EvaluateLeaveEscaped(environment, location);
            List<string> values = environment.Expander
                .ExpandIntoStringListLeaveEscaped(
                    evaluatedValue,
                    ExpanderOptions.ExpandItems,
                    location)
                .ToList();
            return values.Count == 0
                ? null
                : new HashSet<string>(values);
        }

        private static void FilterCompiledItemMetadata(
            List<ProjectItemInstance> items,
            HashSet<string> keepMetadata,
            HashSet<string> removeMetadata)
        {
            if (keepMetadata == null && removeMetadata == null)
            {
                return;
            }

            var metadataToRemove = new List<string>();
            foreach (ProjectItemInstance item in items)
            {
                metadataToRemove.Clear();
                foreach (string metadataName in item.EnumerableMetadataNames)
                {
                    if ((keepMetadata != null &&
                         !keepMetadata.Contains(metadataName)) ||
                        (removeMetadata != null &&
                         removeMetadata.Contains(metadataName)))
                    {
                        metadataToRemove.Add(metadataName);
                    }
                }

                foreach (string metadataName in metadataToRemove)
                {
                    item.RemoveMetadata(metadataName);
                }
            }
        }

        private HashSet<string> EvaluateCompiledExcludePaths(
            IReadOnlyList<string> excludes,
            IElementLocation excludeLocation,
            ProjectInstance project)
        {
            var excludedPaths = new HashSet<string>(
                excludes.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (string excludeSplit in excludes)
            {
                string[] excludeSplitFiles =
                    EngineFileUtilities.GetFileListUnescaped(
                        project.Directory,
                        excludeSplit,
                        loggingMechanism: TargetLoggingContext,
                        excludeLocation: excludeLocation);
                foreach (string excludeSplitFile in excludeSplitFiles)
                {
                    excludedPaths.Add(
                        excludeSplitFile.NormalizeForPathComparison());
                }
            }

            return excludedPaths;
        }

        private WorkUnitResult ExecuteIntrinsic(
            CompiledTargetActionRecord record,
            Lookup lookup)
        {
            bool condition =
                string.IsNullOrEmpty(record.Child.Condition) ||
                ConditionEvaluator.EvaluateCondition(
                    record.Child.Condition,
                    ParserOptions.AllowPropertiesAndItemLists,
                    GetConditionExpander(lookup),
                    ExpanderOptions.ExpandAll,
                    RequestEntry.ProjectRootDirectory,
                    record.Child.ConditionLocation,
                    FileSystems.Default,
                    loggingContext: TargetLoggingContext);

            if (!condition)
            {
                return new WorkUnitResult(
                    WorkUnitResultCode.Skipped,
                    WorkUnitActionCode.Continue,
                    null);
            }

            using var intrinsicTaskMeasurement =
                BuildExecutionInstrumentation.Measure(
                    BuildExecutionMetric.IntrinsicTask,
                    BuildExecutionInstrumentation.DetailsEnabled
                        ? record.Child.GetType().Name
                        : null,
                    TargetLoggingContext.Target.Name);
            using var fallbackPropertyGroupMeasurement =
                record.Kind == CompiledTargetActionKind.PropertyGroup
                    ? BuildExecutionInstrumentation.Measure(
                        BuildExecutionMetric.FallbackPropertyGroup,
                        parentName: TargetLoggingContext.Target.Name)
                    : default;
            using var fallbackItemGroupMeasurement =
                record.Kind == CompiledTargetActionKind.ItemGroup
                    ? BuildExecutionInstrumentation.Measure(
                        BuildExecutionMetric.FallbackItemGroup,
                        parentName: TargetLoggingContext.Target.Name)
                    : default;
            try
            {
                bool logTaskInputs =
                    Host.BuildParameters.LogTaskInputs ||
                    Traits.Instance.EscapeHatches.LogTaskInputs;
                IntrinsicTask task = record.Kind switch
                {
                    CompiledTargetActionKind.PropertyGroup =>
                        new PropertyGroupIntrinsicTask(
                            (ProjectPropertyGroupTaskInstance)record.Child,
                            TargetLoggingContext,
                            RequestEntry.RequestConfiguration.Project,
                            logTaskInputs),
                    CompiledTargetActionKind.ItemGroup =>
                        new ItemGroupIntrinsicTask(
                            (ProjectItemGroupTaskInstance)record.Child,
                            TargetLoggingContext,
                            RequestEntry.RequestConfiguration.Project,
                            logTaskInputs),
                    _ => throw new InternalErrorException(
                        "Unexpected intrinsic action kind."),
                };
                task.ExecuteTask(lookup);
                return new WorkUnitResult(
                    WorkUnitResultCode.Success,
                    WorkUnitActionCode.Continue,
                    null);
            }
            catch (InvalidProjectFileException exception)
            {
                TargetLoggingContext.LogInvalidProjectFileError(exception);
                return new WorkUnitResult(
                    WorkUnitResultCode.Failed,
                    WorkUnitActionCode.Stop,
                    exception);
            }
        }

        private Expander<ProjectPropertyInstance, ProjectItemInstance>
            GetConditionExpander(Lookup lookup)
        {
            if (ReferenceEquals(lookup, LookupForExecution))
            {
                return _executionConditionExpander ??=
                    CreateExpander(lookup);
            }

            return _inferenceConditionExpander ??=
                CreateExpander(lookup);
        }

        private CompiledLookupExpressionEnvironment
            GetExpressionEnvironment(Lookup lookup)
        {
            if (ReferenceEquals(lookup, LookupForExecution))
            {
                return _executionExpressionEnvironment ??=
                    new CompiledLookupExpressionEnvironment(
                        GetConditionExpander(lookup));
            }

            return _inferenceExpressionEnvironment ??=
                new CompiledLookupExpressionEnvironment(
                    GetConditionExpander(lookup));
        }

        private Expander<ProjectPropertyInstance, ProjectItemInstance>
            CreateExpander(Lookup lookup) =>
            new(
                lookup,
                lookup,
                new StringMetadataTable(metadata: null),
                FileSystems.Default,
                TargetLoggingContext);

        internal void SetTaskInstance(ProjectTaskInstance taskInstance) => TaskInstance = taskInstance;

        internal void SetCurrentTask(
            ITask task,
            TaskLoggingContext taskLoggingContext,
            CompiledTaskSourceProgram template) =>
            (_cancellationState ??=
                new FastTaskCancellationState(CancellationToken))
                .SetCurrentTask(task, taskLoggingContext, template);

        internal void ClearCurrentTask(ITask task) =>
            _cancellationState?.ClearCurrentTask(task);

        public void Dispose() => _cancellationState?.Dispose();
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
