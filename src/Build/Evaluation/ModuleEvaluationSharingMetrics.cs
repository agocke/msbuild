// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Describes the amount of evaluation work that could be shared across
    /// configured projects based on the values read by each operation.
    /// </summary>
    public sealed class ModuleEvaluationSharingMetrics
    {
        internal ModuleEvaluationSharingMetrics(
            IReadOnlyList<ModuleEvaluationOperationMetrics> operations,
            ModuleEvaluationCacheMetrics moduleCacheMetrics,
            EvaluationReplayCacheMetrics propertyReplayCacheMetrics,
            EvaluationReplayCacheMetrics conditionReplayCacheMetrics)
        {
            Operations = operations;
            TotalExecutions = operations.Sum(operation => operation.Executions);
            DistinctVariants = operations.Sum(operation => operation.DistinctVariants);
            Replays = operations.Sum(operation => operation.Replays);
            ScalarFallbacks = operations.Sum(operation => operation.ScalarFallbacks);
            ModuleCacheHits = moduleCacheMetrics.Hits;
            ModuleCacheMisses = moduleCacheMetrics.Misses;
            ModuleLowerings = moduleCacheMetrics.Lowerings;
            PropertyReplayCacheHits = propertyReplayCacheMetrics.Hits;
            PropertyReplayCacheMisses = propertyReplayCacheMetrics.Misses;
            PropertyReplayCacheContentions =
                propertyReplayCacheMetrics.PublicationContentions;
            PropertyReplayCacheVariants =
                propertyReplayCacheMetrics.PublishedVariants;
            ConditionReplayCacheHits = conditionReplayCacheMetrics.Hits;
            ConditionReplayCacheMisses = conditionReplayCacheMetrics.Misses;
            ConditionReplayCacheContentions =
                conditionReplayCacheMetrics.PublicationContentions;
            ConditionReplayCacheVariants =
                conditionReplayCacheMetrics.PublishedVariants;
        }

        /// <summary>
        /// Gets metrics for each measured evaluation operation.
        /// </summary>
        public IReadOnlyList<ModuleEvaluationOperationMetrics> Operations { get; }

        /// <summary>
        /// Gets the number of lane-operation visits, including replayed operations.
        /// </summary>
        public long TotalExecutions { get; }

        /// <summary>
        /// Gets the number of operations that executed through the scalar evaluator.
        /// </summary>
        public long ScalarExecutions => TotalExecutions - Replays;

        /// <summary>
        /// Gets the number of distinct observed-input variants.
        /// </summary>
        public long DistinctVariants { get; }

        /// <summary>
        /// Gets the number of operations that reused a previously executed variant.
        /// </summary>
        public long Replays { get; }

        /// <summary>
        /// Gets the number of operations that were explicitly routed through
        /// scalar evaluation because the replay slice did not support them.
        /// </summary>
        public long ScalarFallbacks { get; }

        /// <summary>
        /// Gets the scalar executions that could have reused an existing variant.
        /// </summary>
        public long PotentialReuses => TotalExecutions - DistinctVariants;

        /// <summary>
        /// Gets the number of compact-module cache lookups that reused a module
        /// for the current <see cref="ProjectRootElement"/> version.
        /// </summary>
        public long ModuleCacheHits { get; }

        /// <summary>
        /// Gets the number of compact-module cache lookups that required
        /// lowering or version replacement.
        /// </summary>
        public long ModuleCacheMisses { get; }

        /// <summary>
        /// Gets the number of compact evaluation modules published by this
        /// context.
        /// </summary>
        public long ModuleLowerings { get; }

        public long PropertyReplayCacheHits { get; }

        public long PropertyReplayCacheMisses { get; }

        public long PropertyReplayCacheContentions { get; }

        public long PropertyReplayCacheVariants { get; }

        public long ConditionReplayCacheHits { get; }

        public long ConditionReplayCacheMisses { get; }

        public long ConditionReplayCacheContentions { get; }

        public long ConditionReplayCacheVariants { get; }

        public long ReplayCacheHits =>
            PropertyReplayCacheHits + ConditionReplayCacheHits;

        public long ReplayCacheMisses =>
            PropertyReplayCacheMisses + ConditionReplayCacheMisses;

        public long ReplayCacheContentions =>
            PropertyReplayCacheContentions +
            ConditionReplayCacheContentions;
    }

    /// <summary>
    /// Describes observed-input variants for one evaluation operation.
    /// </summary>
    public sealed class ModuleEvaluationOperationMetrics
    {
        internal ModuleEvaluationOperationMetrics(
            string modulePath,
            int line,
            int column,
            string kind,
            string name,
            long executions,
            int distinctVariants,
            long replays,
            long scalarFallbacks,
            IReadOnlyList<string> dependencies)
        {
            ModulePath = modulePath;
            Line = line;
            Column = column;
            Kind = kind;
            Name = name;
            Executions = executions;
            DistinctVariants = distinctVariants;
            Replays = replays;
            ScalarFallbacks = scalarFallbacks;
            Dependencies = dependencies;
        }

        public string ModulePath { get; }

        public int Line { get; }

        public int Column { get; }

        public string Kind { get; }

        public string Name { get; }

        public long Executions { get; }

        public long ScalarExecutions => Executions - Replays;

        public int DistinctVariants { get; }

        public long Replays { get; }

        public long ScalarFallbacks { get; }

        public long PotentialReuses => Executions - DistinctVariants;

        public IReadOnlyList<string> Dependencies { get; }
    }

    internal sealed class ModuleEvaluationSharingCollector
    {
        private readonly ConcurrentDictionary<EvaluationOperationId, OperationAccumulator> _operations =
            new ConcurrentDictionary<EvaluationOperationId, OperationAccumulator>();

        internal void Record(
            EvaluationOperationId operation,
            IReadOnlyDictionary<string, string> propertyReads,
            IReadOnlyDictionary<string, string> itemReads)
        {
            OperationAccumulator accumulator =
                _operations.GetOrAdd(operation, static _ => new OperationAccumulator());
            accumulator.Record(propertyReads, itemReads);
        }

        internal void RecordReplay(
            EvaluationOperationId operation,
            IReadOnlyDictionary<string, string> propertyReads)
        {
            OperationAccumulator accumulator =
                _operations.GetOrAdd(operation, static _ => new OperationAccumulator());
            accumulator.RecordReplay(propertyReads);
        }

        internal void RecordScalarFallback(EvaluationOperationId operation)
        {
            OperationAccumulator accumulator =
                _operations.GetOrAdd(operation, static _ => new OperationAccumulator());
            accumulator.RecordScalarFallback();
        }

        internal ModuleEvaluationSharingMetrics CreateSnapshot(
            EvaluationModuleCache moduleCache = null,
            PropertyAssignmentReplayCache propertyReplayCache = null,
            ConditionReplayCache conditionReplayCache = null)
        {
            ModuleEvaluationOperationMetrics[] operations = _operations
                .Select(pair => pair.Value.CreateSnapshot(pair.Key))
                .OrderBy(operation => operation.ModulePath, StringComparer.Ordinal)
                .ThenBy(operation => operation.Line)
                .ThenBy(operation => operation.Column)
                .ThenBy(operation => operation.Kind, StringComparer.Ordinal)
                .ThenBy(operation => operation.Name, StringComparer.Ordinal)
                .ToArray();
            return new ModuleEvaluationSharingMetrics(
                Array.AsReadOnly(operations),
                moduleCache?.GetMetrics() ?? default,
                propertyReplayCache?.GetMetrics() ?? default,
                conditionReplayCache?.GetMetrics() ?? default);
        }

        private sealed class OperationAccumulator
        {
            private readonly object _lock = new object();
            private readonly HashSet<string> _variants = new HashSet<string>(
                StringComparer.Ordinal);
            private readonly HashSet<string> _dependencies = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            private long _executions;
            private long _replays;
            private long _scalarFallbacks;

            internal void Record(
                IReadOnlyDictionary<string, string> propertyReads,
                IReadOnlyDictionary<string, string> itemReads)
            {
                Interlocked.Increment(ref _executions);
                string fingerprint = CreateFingerprint(propertyReads, itemReads);

                lock (_lock)
                {
                    _variants.Add(fingerprint);
                    foreach (string property in propertyReads.Keys)
                    {
                        _dependencies.Add($"Property:{property}");
                    }

                    foreach (string itemType in itemReads.Keys)
                    {
                        _dependencies.Add($"Item:{itemType}");
                    }
                }
            }

            internal void RecordReplay(
                IReadOnlyDictionary<string, string> propertyReads)
            {
                Interlocked.Increment(ref _executions);
                Interlocked.Increment(ref _replays);
                lock (_lock)
                {
                    foreach (string property in propertyReads.Keys)
                    {
                        _dependencies.Add($"Property:{property}");
                    }
                }
            }

            internal void RecordScalarFallback()
            {
                Interlocked.Increment(ref _scalarFallbacks);
            }

            internal ModuleEvaluationOperationMetrics CreateSnapshot(EvaluationOperationId key)
            {
                lock (_lock)
                {
                    string[] dependencies = _dependencies
                        .OrderBy(dependency => dependency, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new ModuleEvaluationOperationMetrics(
                        key.ModulePath,
                        key.Line,
                        key.Column,
                        key.Kind,
                        key.Name,
                        Interlocked.Read(ref _executions),
                        _variants.Count,
                        Interlocked.Read(ref _replays),
                        Interlocked.Read(ref _scalarFallbacks),
                        Array.AsReadOnly(dependencies));
                }
            }

            private static string CreateFingerprint(
                IReadOnlyDictionary<string, string> propertyReads,
                IReadOnlyDictionary<string, string> itemReads)
            {
                var builder = new StringBuilder();
                foreach (KeyValuePair<string, string> read in propertyReads.OrderBy(
                             pair => pair.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    Append(builder, "P");
                    Append(builder, read.Key.ToUpperInvariant());
                    Append(builder, read.Value);
                }

                foreach (KeyValuePair<string, string> read in itemReads.OrderBy(
                             pair => pair.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    Append(builder, "I");
                    Append(builder, read.Key.ToUpperInvariant());
                    Append(builder, read.Value);
                }

                return builder.ToString();
            }

            private static void Append(StringBuilder builder, string value)
            {
                if (value is null)
                {
                    builder.Append("-1:");
                    return;
                }

                builder.Append(value.Length);
                builder.Append(':');
                builder.Append(value);
            }
        }
    }

    internal sealed class ModuleEvaluationReadTracker
    {
        private readonly ModuleEvaluationSharingCollector _collector;
        private readonly bool _trackReplayInputs;
        private Scope _currentScope;

        internal ModuleEvaluationReadTracker(
            ModuleEvaluationSharingCollector collector,
            bool trackReplayInputs)
        {
            _collector = collector;
            _trackReplayInputs = trackReplayInputs;
        }

        internal Scope Track(
            ProjectElement element,
            string kind,
            string name = null,
            ElementLocation location = null)
        {
            if (_collector is null)
            {
                return null;
            }

            EvaluationOperationId key = EvaluationOperationId.Create(
                element,
                kind,
                name,
                location);
            return Track(key);
        }

        internal Scope Track(EvaluationOperationId operation)
        {
            if (_collector is null)
            {
                return null;
            }

            return StartScope(operation);
        }

        internal Scope TrackReplay(EvaluationOperationId operation)
        {
            if (_collector is null && !_trackReplayInputs)
            {
                return null;
            }

            return StartScope(operation);
        }

        private Scope StartScope(EvaluationOperationId operation)
        {
            var scope = new Scope(this, _currentScope, operation);
            _currentScope = scope;
            return scope;
        }

        internal void RecordReplay(
            EvaluationOperationId operation,
            IReadOnlyDictionary<string, string> propertyReads)
        {
            _collector?.RecordReplay(operation, propertyReads);
        }

        internal void RecordScalarFallback(EvaluationOperationId operation)
        {
            _collector?.RecordScalarFallback(operation);
        }

        internal void RecordPropertyRead(string name, string value)
        {
            if (_currentScope is not null && !string.IsNullOrEmpty(name))
            {
                _currentScope.PropertyReads[name] = value;
            }
        }

        internal void RecordPropertyRead(string name, IValued value)
        {
            RecordPropertyRead(name, value?.EscapedValue);
        }

        internal void RecordItems<I, M>(string itemType, ICollection<I> items)
            where I : class, IItem<M>
            where M : class, IMetadatum
        {
            if (_currentScope is null || string.IsNullOrEmpty(itemType))
            {
                return;
            }

            var builder = new StringBuilder();
            foreach (I item in items)
            {
                Append(builder, item.EvaluatedIncludeEscaped);
                foreach (M metadata in item.Metadata.OrderBy(
                             value => value.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    Append(builder, metadata.Key.ToUpperInvariant());
                    Append(builder, metadata.EscapedValue);
                }
            }

            _currentScope.ItemReads[itemType] = builder.ToString();
        }

        private static void Append(StringBuilder builder, string value)
        {
            if (value is null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length);
            builder.Append(':');
            builder.Append(value);
        }

        private void Complete(Scope scope)
        {
            if (!ReferenceEquals(_currentScope, scope))
            {
                throw new InvalidOperationException(
                    "Module evaluation measurement scopes must be disposed in order.");
            }

            _currentScope = scope.Parent;
            _collector?.Record(
                scope.Operation,
                scope.PropertyReads,
                scope.ItemReads);
        }

        internal sealed class Scope : IDisposable
        {
            private readonly ModuleEvaluationReadTracker _owner;
            private bool _disposed;

            internal Scope(
                ModuleEvaluationReadTracker owner,
                Scope parent,
                EvaluationOperationId operation)
            {
                _owner = owner;
                Parent = parent;
                Operation = operation;
            }

            internal Scope Parent { get; }

            internal EvaluationOperationId Operation { get; }

            internal Dictionary<string, string> PropertyReads { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal Dictionary<string, string> ItemReads { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _owner.Complete(this);
                }
            }
        }
    }
}
