// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.BackEnd.SdkResolution
{
    internal sealed class RecordingSdkResolverService : ISdkResolverService
    {
        private readonly ISdkResolverService _inner;
        private readonly ConcurrentDictionary<SdkResolutionKey, SdkResolutionLockEntry> _entries = new();
        private readonly ConcurrentQueue<string> _conflicts = new();

        internal RecordingSdkResolverService(ISdkResolverService inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public Action<INodePacket> SendPacket => _inner.SendPacket;

        public bool IsNodeShutDown
        {
            get => _inner.IsNodeShutDown;
            set => _inner.IsNodeShutDown = value;
        }

        public void ClearCache(int submissionId) => _inner.ClearCache(submissionId);

        public void ClearCaches() => _inner.ClearCaches();

        public SdkResult ResolveSdk(
            int submissionId,
            SdkReference sdk,
            LoggingContext loggingContext,
            ElementLocation sdkReferenceLocation,
            string solutionPath,
            string projectPath,
            bool interactive,
            bool isRunningInVisualStudio,
            bool failOnUnresolvedSdk)
        {
            SdkResult result = _inner.ResolveSdk(
                submissionId,
                sdk,
                loggingContext,
                sdkReferenceLocation,
                solutionPath,
                projectPath,
                interactive,
                isRunningInVisualStudio,
                failOnUnresolvedSdk);
            SdkResolutionLockEntry entry = SdkResolutionLockEntry.FromResult(
                sdk,
                solutionPath,
                projectPath,
                result);
            _entries.AddOrUpdate(
                entry.Key,
                entry,
                (_, existing) =>
                {
                    if (!StringComparer.Ordinal.Equals(
                            existing.ResultIdentity,
                            entry.ResultIdentity))
                    {
                        _conflicts.Enqueue(
                            $"SDK '{entry.Name}' resolved to different results while acquiring the lock.");
                    }

                    return existing;
                });
            return result;
        }

        internal SdkResolutionLock CreateLock()
        {
            if (!_conflicts.IsEmpty)
            {
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, _conflicts.ToArray()));
            }

            return new(_entries.Values);
        }
    }
}
