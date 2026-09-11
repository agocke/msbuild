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

        private EvaluationContext(SharingPolicy policy, IFileSystem fileSystem, ISdkResolverService sdkResolverService = null,
            ConcurrentDictionary<string, IReadOnlyList<string>> fileEntryExpansionCache = null,
            ModuleEvaluationSharingCollector moduleEvaluationSharingCollector = null,
            EvaluationModuleCache evaluationModuleCache = null,
            PropertyAssignmentReplayCache propertyAssignmentReplayCache = null,
            ConditionReplayCache conditionReplayCache = null,
            bool useCompiledModuleEffectBatches = false)
        {
            Policy = policy;
            SdkResolverService = sdkResolverService ?? new CachingSdkResolverService();
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
            return Create(policy, fileSystem: null);
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
            // Unsupported case: not-fully-shared context with non null file system.
            ErrorUtilities.VerifyThrowArgument(
                policy == SharingPolicy.Shared || fileSystem == null,
                "IsolatedContextDoesNotSupportFileSystem");

            var context = new EvaluationContext(
                policy,
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
            )
        {
            var context = new EvaluationContext(
                SharingPolicy.Shared,
                fileSystem: null,
                moduleEvaluationSharingCollector: new ModuleEvaluationSharingCollector());
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        internal static EvaluationContext CreateForCompiledModuleEvaluation(
            bool useCompiledModuleEffectBatches = false)
        {
            var context = new EvaluationContext(
                SharingPolicy.Shared,
                fileSystem: null,
                moduleEvaluationSharingCollector:
                    new ModuleEvaluationSharingCollector(),
                evaluationModuleCache: new EvaluationModuleCache(),
                useCompiledModuleEffectBatches:
                    useCompiledModuleEffectBatches);
            TestOnlyHookOnCreate?.Invoke(context);
            return context;
        }

        /// <summary>
        /// Creates a shared evaluation context that reuses supported module
        /// evaluation operations within the context's lifetime.
        /// </summary>
        public static EvaluationContext CreateForModuleEvaluationSharing()
        {
            var context = new EvaluationContext(
                SharingPolicy.Shared,
                fileSystem: null,
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
                        fileSystem: null,
                        sdkResolverService:
                            Policy == SharingPolicy.SharedSDKCache
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
    }
}
