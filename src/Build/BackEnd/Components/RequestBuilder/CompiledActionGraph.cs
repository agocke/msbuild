// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
using System.Runtime.Loader;
#endif
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Partially evaluates target children into source-only action templates.
    /// The residual plans are deliberately external to translated project state.
    /// </summary>
    internal sealed class CompiledTargetPlan
    {
        internal const string EnablePartialEvaluationEnvVarName =
            "MSBUILDENABLEACTIONGRAPHPARTIALEVALUATION";

        internal static bool IsPartialEvaluationEnabled =>
            Environment.GetEnvironmentVariable(EnablePartialEvaluationEnvVarName) == "1";

        private static readonly ConditionalWeakTable<ProjectInstance, PartiallyEvaluatedProject> s_projects = new();

        private readonly CompiledTaskAction[] _actions;

        private CompiledTargetPlan(ProjectTargetInstance target, PartiallyEvaluatedProject project)
        {
            _actions = new CompiledTaskAction[target.Children.Count];
            for (int i = 0; i < target.Children.Count; i++)
            {
                if (target.Children[i] is ProjectTaskInstance task)
                {
                    CompiledTaskSourceProgram program =
                        CompiledTaskSourceProgram.GetOrCreate(task);
                    _actions[i] = new CompiledTaskAction(
                        program,
                        project.GetTaskRegistration(program.Name));
                }
            }
        }

        internal static CompiledTargetPlan PartiallyEvaluate(ProjectInstance projectInstance, ProjectTargetInstance target)
        {
            ArgumentNullException.ThrowIfNull(projectInstance);
            ArgumentNullException.ThrowIfNull(target);

            PartiallyEvaluatedProject project =
                s_projects.GetValue(projectInstance, static _ => new PartiallyEvaluatedProject());
            return project.PartiallyEvaluate(target);
        }

        internal CompiledTaskAction GetAction(int childIndex) => _actions[childIndex];

        private sealed class PartiallyEvaluatedProject
        {
            private readonly ConcurrentDictionary<ProjectTargetInstance, CompiledTargetPlan> _targets = new();
            private readonly ConcurrentDictionary<string, PartiallyEvaluatedTaskRegistration> _taskRegistrations =
                new(StringComparer.OrdinalIgnoreCase);

            internal CompiledTargetPlan PartiallyEvaluate(ProjectTargetInstance target) =>
                _targets.GetOrAdd(target, targetInstance => new CompiledTargetPlan(targetInstance, this));

            internal PartiallyEvaluatedTaskRegistration GetTaskRegistration(string taskName) =>
                _taskRegistrations.GetOrAdd(
                    taskName,
                    static _ => new PartiallyEvaluatedTaskRegistration());
        }
    }

    /// <summary>
    /// Project-bound action for an ordinary, in-process assembly task site.
    /// </summary>
    internal sealed class CompiledTaskAction
    {
        private readonly CompiledTaskSourceProgram _program;
        private readonly PartiallyEvaluatedTaskRegistration _registration;
        private BoundTaskAction _boundAction;

        internal CompiledTaskAction(
            CompiledTaskSourceProgram program,
            PartiallyEvaluatedTaskRegistration registration)
        {
            _program = program;
            _registration = registration;
        }

        internal CompiledTaskSourceProgram Program => _program;

        internal CompiledTaskSourceProgram Template => _program;

        internal BoundTaskAction GetBoundAction()
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            ResolvedTaskRegistration registration = _registration.Get();
            return registration == null
                ? null
                : Bind(registration);
        }

        internal FastTaskInvocation GetFastInvocation()
        {
            ResolvedTaskRegistration registration = _registration.Get();
            if (registration == null)
            {
                return default;
            }

            FastTaskAction action =
                FastTaskAction.TryGetOrCreate(_program, registration);
            return action == null
                ? default
                : new FastTaskInvocation(
                    action,
                    registration.TaskFactoryWrapper);
        }

        internal FastTaskAction GetFastAction()
        {
            ResolvedTaskRegistration registration = _registration.Get();
            return registration == null
                ? null
                : FastTaskAction.TryGetOrCreate(
                    _program,
                    registration);
        }

        internal BoundTaskAction TryBind(TaskRequirements requirements, TaskFactoryWrapper taskFactoryWrapper)
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            var registration = new ResolvedTaskRegistration(requirements, taskFactoryWrapper);
            BoundTaskAction candidate = BoundTaskAction.TryCreate(_program, registration);
            if (candidate == null)
            {
                return null;
            }

            return Bind(_registration.Publish(registration));
        }

        private BoundTaskAction Bind(ResolvedTaskRegistration registration)
        {
            BoundTaskAction action = Volatile.Read(ref _boundAction);
            if (action != null)
            {
                return action;
            }

            BoundTaskAction candidate = BoundTaskAction.TryCreate(_program, registration);
            return candidate == null
                ? null
                : Interlocked.CompareExchange(ref _boundAction, candidate, null) ?? candidate;
        }

        internal bool Invoke(CompiledTaskActionFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return frame.TaskExecutionHost.SetCompiledTaskParameters(
                frame.BoundAction,
                frame.BoundAction.TypeMetadata);
        }
    }

    /// <summary>
    /// Dynamic per-batch state made available to a compiled task action.
    /// </summary>
    internal sealed class CompiledTaskActionFrame
    {
        internal CompiledTaskActionFrame(TaskExecutionHost taskExecutionHost, BoundTaskAction boundAction)
        {
            TaskExecutionHost = taskExecutionHost;
            BoundAction = boundAction;
        }

        internal TaskExecutionHost TaskExecutionHost { get; }

        internal BoundTaskAction BoundAction { get; }
    }

    /// <summary>
    /// Publication cell for the project-local registration selected for one static task identity.
    /// </summary>
    internal sealed class PartiallyEvaluatedTaskRegistration
    {
        private ResolvedTaskRegistration _registration;

        internal ResolvedTaskRegistration Get() => Volatile.Read(ref _registration);

        internal ResolvedTaskRegistration Publish(ResolvedTaskRegistration registration) =>
            Interlocked.CompareExchange(ref _registration, registration, null) ?? registration;
    }

    /// <summary>
    /// Project-local task registration selected by normal MSBuild task resolution.
    /// </summary>
    internal sealed record ResolvedTaskRegistration(
        TaskRequirements Requirements,
        TaskFactoryWrapper TaskFactoryWrapper);

    /// <summary>
    /// Project-local specialization of a source task site. It retains registration-local property wrappers,
    /// while task-type metadata and memoized setter actions remain ALC-scoped.
    /// </summary>
    internal sealed class BoundTaskAction
    {
        private BoundTaskAction(
            TaskFactoryWrapper taskFactoryWrapper,
            TaskActionTypeMetadata typeMetadata,
            BoundTaskParameter[] parameters,
            string[] requiredParameterNames,
            ulong allRequiredParameters,
            FastTaskAction fastAction)
        {
            TaskFactoryWrapper = taskFactoryWrapper;
            TypeMetadata = typeMetadata;
            Parameters = parameters;
            RequiredParameterNames = requiredParameterNames;
            AllRequiredParameters = allRequiredParameters;
            FastAction = fastAction;
        }

        internal TaskFactoryWrapper TaskFactoryWrapper { get; }

        internal TaskActionTypeMetadata TypeMetadata { get; }

        internal Type TaskType => TaskFactoryWrapper.TaskFactoryLoadedType.Type;

        internal BoundTaskParameter[] Parameters { get; }

        internal string[] RequiredParameterNames { get; }

        internal ulong AllRequiredParameters { get; }

        internal FastTaskAction FastAction { get; }

        internal static BoundTaskAction TryCreate(
            CompiledTaskSourceProgram program,
            ResolvedTaskRegistration registration)
        {
            TaskRequirements requirements = registration.Requirements;
            TaskFactoryWrapper taskFactoryWrapper = registration.TaskFactoryWrapper;
            if (!program.HasStaticCurrentProcessIdentity ||
                requirements != TaskRequirements.None ||
                taskFactoryWrapper?.TaskFactory is not AssemblyTaskFactory ||
                !taskFactoryWrapper.FactoryIdentityParameters.IsEmpty)
            {
                return null;
            }

            LoadedType loadedType = taskFactoryWrapper.TaskFactoryLoadedType;
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
            if (loadedType.IsMarshalByRef || loadedType.HasLoadInSeparateAppDomainAttribute)
            {
                return null;
            }
#endif

            IReadOnlyDictionary<string, string> requiredParameters = taskFactoryWrapper.GetNamesOfPropertiesWithRequiredAttribute;
            if (requiredParameters.Count > 64)
            {
                return null;
            }

            TaskActionTypeMetadata metadata = TaskActionTypeMetadata.GetOrCreate(loadedType);
            var boundParameters = new BoundTaskParameter[program.Parameters.Length];
            string[] requiredParameterNames = requiredParameters.Keys.ToArray();

            for (int parameterIndex = 0; parameterIndex < program.Parameters.Length; parameterIndex++)
            {
                CompiledTaskParameterProgram parameter = program.Parameters[parameterIndex];
                int propertyIndex = FindPropertyIndex(loadedType, parameter.Name);
                if (propertyIndex < 0)
                {
                    return null;
                }

                TaskPropertyInfo property = taskFactoryWrapper.GetProperty(propertyIndex);
                TaskActionPropertyMetadata propertyMetadata = metadata.GetProperty(propertyIndex);
                if (!TaskParameterTypeVerifier.IsValidScalarInputParameter(propertyMetadata.ParameterType) &&
                    !TaskParameterTypeVerifier.IsValidVectorInputParameter(propertyMetadata.ParameterType))
                {
                    return null;
                }

                ulong requiredBit = 0;
                for (int requiredIndex = 0; requiredIndex < requiredParameterNames.Length; requiredIndex++)
                {
                    if (requiredParameterNames[requiredIndex].Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        requiredBit = 1UL << requiredIndex;
                        break;
                    }
                }

                boundParameters[parameterIndex] = new BoundTaskParameter(parameter, property, propertyIndex, requiredBit);
            }

            ulong allRequiredParameters = requiredParameterNames.Length == 64
                ? ulong.MaxValue
                : (1UL << requiredParameterNames.Length) - 1;

            FastTaskAction fastAction = metadata.GetOrCreateFastAction(
                program,
                taskFactoryWrapper,
                loadedType);

            return new BoundTaskAction(
                taskFactoryWrapper,
                metadata,
                boundParameters,
                requiredParameterNames,
                allRequiredParameters,
                fastAction);
        }

        internal static int FindPropertyIndex(LoadedType loadedType, string parameterName)
        {
            int caseInsensitiveIndex = -1;
            int caseInsensitiveMatches = 0;

            for (int i = 0; i < loadedType.Properties.Length; i++)
            {
                if (loadedType.Properties[i].Name.Equals(parameterName))
                {
                    return i;
                }

                if (loadedType.Properties[i].Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveIndex = i;
                    caseInsensitiveMatches++;
                }
            }

            return caseInsensitiveMatches == 1 ? caseInsensitiveIndex : -1;
        }
    }

    /// <summary>
    /// Prebound source parameter and its registration-local property wrapper.
    /// </summary>
    internal sealed class BoundTaskParameter
    {
        internal BoundTaskParameter(CompiledTaskParameterProgram source, TaskPropertyInfo property, int propertyIndex, ulong requiredBit)
        {
            Source = source;
            Property = property;
            PropertyIndex = propertyIndex;
            RequiredBit = requiredBit;
        }

        internal CompiledTaskParameterProgram Source { get; }

        internal TaskPropertyInfo Property { get; }

        internal int PropertyIndex { get; }

        internal ulong RequiredBit { get; }
    }

    /// <summary>
    /// Task-type metadata and memoized setter actions. Values are dependent on the task ALC so they
    /// cannot extend the lifetime of a collectible task assembly.
    /// </summary>
    internal sealed class TaskActionTypeMetadata
    {
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
        private static readonly ConditionalWeakTable<AssemblyLoadContext, TaskActionTypeMetadataCache> s_metadataByLoadContext = new();
#else
        private static readonly ConcurrentDictionary<Type, TaskActionTypeMetadata> s_metadataByType = new();
#endif

        private readonly TaskActionPropertyMetadata[] _properties;
        private readonly ConditionalWeakTable<
            CompiledTaskSourceProgram,
            FastTaskActionBinding> _fastActions = new();

        private TaskActionTypeMetadata(LoadedType loadedType)
        {
            _properties = new TaskActionPropertyMetadata[loadedType.Properties.Length];
            for (int i = 0; i < _properties.Length; i++)
            {
                ReflectableTaskPropertyInfo property = loadedType.Properties[i];
                _properties[i] = new TaskActionPropertyMetadata(
                    property.PropertyType,
                    CompileSetter(loadedType.Type, property),
                    CompileGetter(loadedType.Type, property));
            }
        }

        internal static TaskActionTypeMetadata GetOrCreate(LoadedType loadedType)
        {
#if NET && FEATURE_ASSEMBLYLOADCONTEXT
            Type taskType = loadedType.Type;
            AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(taskType.Assembly);
            TaskActionTypeMetadataCache metadataByType =
                s_metadataByLoadContext.GetValue(
                    loadContext,
                    static _ => new TaskActionTypeMetadataCache());
            return metadataByType.GetOrCreate(taskType, loadedType);
#else
            return s_metadataByType.GetOrAdd(loadedType.Type, _ => new TaskActionTypeMetadata(loadedType));
#endif
        }

        internal TaskActionPropertyMetadata GetProperty(int propertyIndex) => _properties[propertyIndex];

        internal FastTaskAction GetOrCreateFastAction(
            CompiledTaskSourceProgram program,
            TaskFactoryWrapper taskFactoryWrapper,
            LoadedType loadedType) =>
            _fastActions.GetValue(
                program,
                source => new FastTaskActionBinding(
                    FastTaskAction.TryCreate(
                        source,
                        taskFactoryWrapper,
                        loadedType,
                        this))).Action;

        private static Action<ITask, object> CompileSetter(Type taskType, ReflectableTaskPropertyInfo property)
        {
            MethodInfo setter = property.Reflection?.SetMethod;
            if (setter?.IsPublic != true || !taskType.IsVisible)
            {
                return null;
            }

#if NET
            MethodInvoker invoker = MethodInvoker.Create(setter);
            return (task, value) => invoker.Invoke(task, value);
#else
            return null;
#endif
        }

        private static Func<ITask, object> CompileGetter(Type taskType, ReflectableTaskPropertyInfo property)
        {
            MethodInfo getter = property.Reflection?.GetMethod;
            if (getter?.IsPublic != true || !taskType.IsVisible)
            {
                return null;
            }

#if NET
            MethodInvoker invoker = MethodInvoker.Create(getter);
            return task => invoker.Invoke(task);
#else
            return null;
#endif
        }

        private sealed class FastTaskActionBinding
        {
            internal FastTaskActionBinding(FastTaskAction action)
            {
                Action = action;
            }

            internal FastTaskAction Action { get; }
        }

#if NET && FEATURE_ASSEMBLYLOADCONTEXT
        private sealed class TaskActionTypeMetadataCache
        {
            private readonly ConditionalWeakTable<Type, TaskActionTypeMetadata> _metadataByType = new();

            internal TaskActionTypeMetadata GetOrCreate(Type taskType, LoadedType loadedType)
            {
                if (_metadataByType.TryGetValue(taskType, out TaskActionTypeMetadata cached))
                {
                    return cached;
                }

                return _metadataByType.GetValue(taskType, _ => new TaskActionTypeMetadata(loadedType));
            }
        }
#endif
    }

    /// <summary>
    /// Immutable task-type property metadata. Registration-local logging policy is intentionally absent.
    /// </summary>
    internal sealed class TaskActionPropertyMetadata
    {
        internal TaskActionPropertyMetadata(
            Type parameterType,
            Action<ITask, object> setter,
            Func<ITask, object> getter)
        {
            ParameterType = parameterType;
            Setter = setter;
            Getter = getter;
        }

        internal Type ParameterType { get; }

        internal Action<ITask, object> Setter { get; }

        internal Func<ITask, object> Getter { get; }
    }
}
