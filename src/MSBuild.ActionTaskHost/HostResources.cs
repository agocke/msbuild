// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Resources;

namespace Microsoft.Build.ActionTaskHost;

internal static class HostResources
{
    private static readonly ResourceManager s_resources = new(
        "Microsoft.Build.ActionTaskHost.Resources.Strings",
        typeof(HostResources).Assembly);

    internal static string Format(string name, params object?[] arguments)
        => string.Format(
            CultureInfo.CurrentCulture,
            s_resources.GetString(name) ?? throw new MissingManifestResourceException(name),
            arguments);
}
