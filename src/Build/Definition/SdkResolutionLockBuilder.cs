// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Evaluation.Context;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Records SDK resolver results during a separate acquisition evaluation.
    /// </summary>
    public sealed class SdkResolutionLockBuilder
    {
        private readonly RecordingSdkResolverService _resolverService;

        public SdkResolutionLockBuilder()
            : this(new CachingSdkResolverService())
        {
        }

        internal SdkResolutionLockBuilder(ISdkResolverService resolverService)
        {
            _resolverService = new RecordingSdkResolverService(resolverService);
        }

        /// <summary>
        /// Creates a shared pure evaluation context that records SDK resolutions as the
        /// acquisition pass's only permitted effect.
        /// </summary>
        public EvaluationContext CreateEvaluationContext() =>
            EvaluationContext.CreateForSdkResolutionAcquisition(_resolverService);

        /// <summary>
        /// Creates an immutable snapshot of every SDK resolution observed so far.
        /// </summary>
        public SdkResolutionLock Build() => _resolverService.CreateLock();
    }
}
