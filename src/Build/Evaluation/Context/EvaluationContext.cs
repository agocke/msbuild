// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    /// <summary>
    ///     An object used by the caller to extend the lifespan of evaluation caches (by passing the object on to other
    ///     evaluations).
    ///     The caller should throw away the context when the environment changes (IO, environment variables, SDK resolution
    ///     inputs, etc).
    ///     This class and its closure needs to be thread safe since API users can do evaluations in parallel.
    /// </summary>
    public class EvaluationContext
    {
        public enum SharingPolicy
        {
            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to reuse all cached state between the different project evaluations that use it.
            /// </summary>
            Shared,

            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to not reuse any cached state between the different project evaluations that use it.
            /// </summary>
            Isolated,

            /// <summary>
            /// Instructs the <see cref="EvaluationContext"/> to reuse SDK resolver cache between the different project evaluations that use it.
            /// No other cached state is reused.
            /// </summary>
            SharedSDKCache,
        }

        /// <summary>
        /// For contexts that are not fully shared, this field tracks whether the instance has already been used for evaluation.
        /// </summary>
        private int _used;

        internal static Action<EvaluationContext> TestOnlyHookOnCreate { get; set; }

        internal SharingPolicy Policy { get; }

        /// <summary>
        /// Gets the semantic policy applied to project evaluation.
        /// </summary>
        public ProjectEvaluationMode EvaluationMode { get; }

        internal ISdkResolverService SdkResolverService { get; }
        internal IFileSystem FileSystem { get; }
        internal FileMatcher FileMatcher { get; }
        internal ModuleEvaluationSharingCollector ModuleEvaluationSharingCollector { get; }
        internal EvaluationModuleCache EvaluationModuleCache { get; }
        internal PropertyAssignmentReplayCache PropertyAssignmentReplayCache { get; }
        internal ConditionReplayCache ConditionReplayCache { get; }
        internal bool UseCompiledModuleEffectBatches { get; }

        /// <summary>
        /// Key to file entry list. Example usages: cache glob expansion and intermediary directory expansions during glob expansion.
        /// </summary>
        private ConcurrentDictionary<string, IReadOnlyList<string>> FileEntryExpansionCache { get; }

        private EvaluationContext(SharingPolicy policy, ProjectEvaluationMode evaluationMode, IFileSystem fileSystem, ISdkResolverService sdkResolverService = null,
            ConcurrentDictionary<string, IReadOnlyList<string>> fileEntryExpansionCache = null,
            ModuleEvaluationSharingCollector moduleEvaluationSharingCollector = null,
            EvaluationModuleCache evaluationModuleCache = null,
            PropertyAssignmentReplayCache propertyAssignmentReplayCache = null,
            ConditionReplayCache conditionReplayCache = null,
            bool useCompiledModuleEffectBatches = false)
        {
            Policy = policy;
            EvaluationMode = evaluationMode;

            SdkResolverService = sdkResolverService ??
                (evaluationMode == ProjectEvaluationMode.Pure
                    ? new LockedSdkResolverService(SdkResolutionLock.Empty)
                    : new CachingSdkResolverService());
            FileEntryExpansionCache = fileEntryExpansionCache ?? new ConcurrentDictionary<string, IReadOnlyList<string>>();
            FileSystem = fileSystem ?? new CachingFileSystemWrapper(FileSystems.Default);
            FileMatcher = new FileMatcher(FileSystem, FileEntryExpansionCache);
            ModuleEvaluationSharingCollector = moduleEvaluationSharingCollector;
            EvaluationModuleCache = evaluationModuleCache;
            PropertyAssignmentReplayCache = propertyAssignmentReplayCache;
            ConditionReplayCache = conditionReplayCache;
            UseCompiledModuleEffectBatches = useCompiledModuleEffectBatches;
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        public static EvaluationContext Create(SharingPolicy policy)
        {
            // Do not remove this method to avoid breaking binary compatibility.
            return Create(policy, ProjectEvaluationMode.Classic, fileSystem: null);
        }

        /// <summary>
        /// Creates an evaluation context with the specified sharing and semantic policies.
        /// </summary>
        public static EvaluationContext Create(SharingPolicy policy, ProjectEvaluationMode evaluationMode)
        {
            return Create(policy, evaluationMode, fileSystem: null);
        }

        /// <summary>
        /// Creates an evaluation context that resolves SDKs exclusively from an immutable lock.
        /// </summary>
        public static EvaluationContext CreateForLockedSdkResolution(
            SharingPolicy policy,
            SdkResolutionLock sdkResolutionLock)
        {
            ArgumentNullException.ThrowIfNull(sdkResolutionLock);
            if (!Enum.IsDefined(typeof(SharingPolicy), policy))
            {
                throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
            }

            var context = new EvaluationContext(
                policy,
                ProjectEvaluationMode.Pure,
                fileSystem: null,
                new LockedSdkResolverService(sdkResolutionLock));
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        /// <summary>
        ///     Factory for <see cref="EvaluationContext" />
        /// </summary>
        /// <param name="policy">The <see cref="SharingPolicy"/> to use.</param>
        /// <param name="fileSystem">The <see cref="MSBuildFileSystemBase"/> to use.
        ///     This parameter is compatible only with <see cref="SharingPolicy.Shared"/>.
        ///     The method throws if a file system is used with <see cref="SharingPolicy.Isolated"/> or <see cref="SharingPolicy.SharedSDKCache"/>.
        ///     The reasoning is that these values guarantee not reusing file system caches between evaluations,
        ///     and the passed in <paramref name="fileSystem"/> might cache state.
        /// </param>
        public static EvaluationContext Create(SharingPolicy policy, MSBuildFileSystemBase fileSystem)
        {
            return Create(policy, ProjectEvaluationMode.Classic, fileSystem);
        }

        /// <summary>
        /// Creates an evaluation context with the specified sharing policy, semantic policy, and file system.
        /// </summary>
        public static EvaluationContext Create(SharingPolicy policy, ProjectEvaluationMode evaluationMode, MSBuildFileSystemBase fileSystem)
        {
            ErrorUtilities.VerifyThrowArgument(
                Enum.IsDefined(typeof(ProjectEvaluationMode), evaluationMode),
                "InvalidProjectEvaluationMode",
                evaluationMode);

            // Unsupported case: not-fully-shared context with non null file system.
            ErrorUtilities.VerifyThrowArgument(
                policy == SharingPolicy.Shared || fileSystem == null,
                "IsolatedContextDoesNotSupportFileSystem");

            var context = new EvaluationContext(
                policy,
                evaluationMode,
                fileSystem,
                evaluationModuleCache:
                    policy == SharingPolicy.Shared &&
                    Traits.Instance.EnableCompiledModuleEvaluation
                        ? new EvaluationModuleCache()
                        : null,
                propertyAssignmentReplayCache:
                    policy == SharingPolicy.Shared &&
                    Traits.Instance.EnableCompiledModuleEvaluation &&
                    Traits.Instance.EnableCompiledModuleReplay
                        ? new PropertyAssignmentReplayCache()
                        : null,
                conditionReplayCache:
                    policy == SharingPolicy.Shared &&
                    Traits.Instance.EnableCompiledModuleEvaluation &&
                    Traits.Instance.EnableCompiledModuleReplay
                        ? new ConditionReplayCache()
                        : null,
                useCompiledModuleEffectBatches:
                    policy == SharingPolicy.Shared &&
                    Traits.Instance.EnableCompiledModuleEvaluation &&
                    Traits.Instance.EnableCompiledModuleEffectBatching);

            TestOnlyHookOnCreate?.Invoke(context);

            return context;
        }

        /// <summary>
        /// Creates a shared evaluation context that measures observed-input variants for
        /// individual evaluation operations.
        /// </summary>
        public static EvaluationContext CreateForModuleEvaluationSharingMeasurement(
            ProjectEvaluationMode evaluationMode = ProjectEvaluationMode.Classic)
        {
            ErrorUtilities.VerifyThrowArgument(
                Enum.IsDefined(typeof(ProjectEvaluationMode), evaluationMode),
                "InvalidProjectEvaluationMode",
                evaluationMode);

            var context = new EvaluationContext(
                SharingPolicy.Shared,
                evaluationMode,
                fileSystem: null,
                moduleEvaluationSharingCollector: new ModuleEvaluationSharingCollector());
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        internal static EvaluationContext CreateForCompiledModuleEvaluation(
            ProjectEvaluationMode evaluationMode)
        {
            ErrorUtilities.VerifyThrowArgument(
                Enum.IsDefined(typeof(ProjectEvaluationMode), evaluationMode),
                "InvalidProjectEvaluationMode",
                evaluationMode);

            var context = new EvaluationContext(
                SharingPolicy.Shared,
                evaluationMode,
                fileSystem: null,
                moduleEvaluationSharingCollector:
                    new ModuleEvaluationSharingCollector(),
                evaluationModuleCache: new EvaluationModuleCache());
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        /// <summary>
        /// Creates a pure, shared evaluation context that reuses supported module
        /// evaluation operations within the context's lifetime.
        /// </summary>
        public static EvaluationContext CreateForModuleEvaluationSharing()
        {
            return CreateForModuleEvaluationSharing(SdkResolutionLock.Empty);
        }

        /// <summary>
        /// Creates a pure, shared evaluation context that reuses supported module
        /// evaluation operations and resolves SDKs exclusively from an immutable lock.
        /// </summary>
        public static EvaluationContext CreateForModuleEvaluationSharing(
            SdkResolutionLock sdkResolutionLock)
        {
            ArgumentNullException.ThrowIfNull(sdkResolutionLock);
            var context = new EvaluationContext(
                SharingPolicy.Shared,
                ProjectEvaluationMode.Pure,
                fileSystem: null,
                new LockedSdkResolverService(sdkResolutionLock),
                moduleEvaluationSharingCollector:
                    new ModuleEvaluationSharingCollector(),
                evaluationModuleCache: new EvaluationModuleCache(),
                propertyAssignmentReplayCache:
                    new PropertyAssignmentReplayCache(),
                conditionReplayCache:
                    new ConditionReplayCache());
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        /// <summary>
        /// Creates an immutable snapshot of module evaluation sharing measurements.
        /// </summary>
        public ModuleEvaluationSharingMetrics GetModuleEvaluationSharingMetrics()
        {
            if (ModuleEvaluationSharingCollector is null)
            {
                if (EvaluationModuleCache is null)
                {
                    throw new InvalidOperationException(
                        "This evaluation context was not created for module evaluation sharing measurement.");
                }

                return new ModuleEvaluationSharingMetrics(
                    Array.Empty<ModuleEvaluationOperationMetrics>(),
                    EvaluationModuleCache.GetMetrics(),
                    PropertyAssignmentReplayCache?.GetMetrics() ?? default,
                    ConditionReplayCache?.GetMetrics() ?? default);
            }

            return ModuleEvaluationSharingCollector.CreateSnapshot(
                EvaluationModuleCache,
                PropertyAssignmentReplayCache,
                ConditionReplayCache);
        }

        internal EvaluationContext ContextForNewProject()
        {
            // Projects using Isolated and SharedSDKCache contexts need to get a new context instance.
            switch (Policy)
            {
                case SharingPolicy.Shared:
                    return this;
                case SharingPolicy.SharedSDKCache:
                case SharingPolicy.Isolated:
                    // Reuse the first not-fully-shared context if it's not been used for an evaluation yet.
                    if (Interlocked.CompareExchange(ref _used, 1, 0) == 0)
                    {
                        return this;
                    }
                    // Create a copy if this context has already been used. Mark it used.
                    EvaluationContext context = new EvaluationContext(
                        Policy,
                        EvaluationMode,
                        fileSystem: null,
                        sdkResolverService:
                            Policy == SharingPolicy.SharedSDKCache ||
                            EvaluationMode == ProjectEvaluationMode.Pure
                                ? SdkResolverService
                                : null,
                        moduleEvaluationSharingCollector: ModuleEvaluationSharingCollector,
                        evaluationModuleCache: EvaluationModuleCache,
                        propertyAssignmentReplayCache: PropertyAssignmentReplayCache,
                        conditionReplayCache: ConditionReplayCache,
                        useCompiledModuleEffectBatches:
                            UseCompiledModuleEffectBatches)
                    {
                        _used = 1,
                    };
                    TestOnlyHookOnCreate?.Invoke(context);
                    return context;

                default:
                    return Assumed.Unreachable<EvaluationContext>();
            }
        }

        /// <summary>
        /// Creates a copy of this <see cref="EvaluationContext"/> with a given <see cref="IFileSystem"/> swapped in.
        /// </summary>
        /// <param name="fileSystem">The file system to use by the new evaluation context.</param>
        /// <returns>The new evaluation context.</returns>
        internal EvaluationContext ContextWithFileSystem(IFileSystem fileSystem)
        {
            return new EvaluationContext(
                Policy,
                EvaluationMode,
                fileSystem,
                SdkResolverService,
                FileEntryExpansionCache,
                ModuleEvaluationSharingCollector,
                EvaluationModuleCache,
                PropertyAssignmentReplayCache,
                ConditionReplayCache,
                UseCompiledModuleEffectBatches)
            {
                _used = 1,
            };
        }

        internal static EvaluationContext CreateForSdkResolutionAcquisition(
            RecordingSdkResolverService resolverService)
        {
            var context = new EvaluationContext(
                SharingPolicy.Shared,
                ProjectEvaluationMode.Pure,
                fileSystem: null,
                resolverService);
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }
    }
}
