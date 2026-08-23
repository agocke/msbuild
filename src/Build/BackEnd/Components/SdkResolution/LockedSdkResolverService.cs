// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.BackEnd.SdkResolution
{
    internal sealed class LockedSdkResolverService : ISdkResolverService
    {
        private readonly SdkResolutionLock _lock;

        internal LockedSdkResolverService(SdkResolutionLock sdkResolutionLock)
        {
            _lock = sdkResolutionLock ??
                throw new ArgumentNullException(nameof(sdkResolutionLock));
        }

        public Action<INodePacket> SendPacket => null;

        public bool IsNodeShutDown { get; set; }

        public void ClearCache(int submissionId)
        {
        }

        public void ClearCaches()
        {
        }

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
            if (_lock.TryGet(sdk, out SdkResolutionLockEntry entry))
            {
                SdkResult result = entry.ToResult(sdk, sdkReferenceLocation);
                SdkResolverService.LogWarnings(
                    loggingContext,
                    sdkReferenceLocation,
                    entry.Warnings);
                if (!SdkResolverService.IsReferenceSameVersion(
                        sdk,
                        entry.Version) &&
                    !SdkResolverService.IsReferenceSameVersion(
                        sdk,
                        entry.ResolvedVersion))
                {
                    loggingContext.LogWarning(
                        null,
                        new BuildEventFileInfo(sdkReferenceLocation),
                        "ReferencingMultipleVersionsOfTheSameSdk",
                        sdk.Name,
                        entry.ResolvedVersion,
                        sdkReferenceLocation,
                        sdk.Version);
                }

                if (!result.Success &&
                    failOnUnresolvedSdk &&
                    entry.Errors.Count != 0)
                {
                    loggingContext.LogError(
                        new BuildEventFileInfo(sdkReferenceLocation),
                        "FailedToResolveSDK",
                        sdk.Name,
                        string.Join(
                            $"{Environment.NewLine}  ",
                            entry.Errors.Where(error =>
                                !string.IsNullOrWhiteSpace(error))));
                }

                return result;
            }

            return new SdkResult(sdk, errors: null, warnings: null)
            {
                ElementLocation = sdkReferenceLocation,
                IsMissingFromLock = true,
            };
        }
    }
}
