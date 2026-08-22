// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Controls which effects are permitted while evaluating a project.
    /// </summary>
    public enum ProjectEvaluationMode
    {
        /// <summary>
        /// Preserves existing MSBuild evaluation behavior.
        /// </summary>
        Classic,

        /// <summary>
        /// Requires evaluation to be deterministic and side-effect-free. Project source, imports,
        /// <c>Exists</c> conditions, and item globs remain available through MSBuild's declarative
        /// evaluation operations. Environment variables are not imported as initial properties,
        /// and arbitrary property-function I/O and ambient nondeterminism are rejected.
        /// </summary>
        Pure,
    }
}
