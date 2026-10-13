// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Build.Framework;
using NuGet.Versioning;

internal sealed class CachedSdkResolver : SdkResolver
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _versions = new(StringComparer.Ordinal);

    public override string Name => nameof(CachedSdkResolver);

    public override int Priority => 100;

    public override SdkResult Resolve(SdkReference sdkReference, SdkResolverContext context, SdkResultFactory factory)
    {
        if (!string.IsNullOrEmpty(sdkReference.MinimumVersion))
        {
            throw new NotSupportedException("The cached benchmark SDK resolver requires an exact version, not MinimumVersion.");
        }

        string? version = sdkReference.Version;
        if (string.IsNullOrEmpty(version))
        {
            string? directory = Path.GetDirectoryName(context.SolutionFilePath ?? context.ProjectFilePath);
            while (directory is not null)
            {
                string globalJson = Path.Combine(directory, "global.json");
                if (File.Exists(globalJson))
                {
                    _versions.GetOrAdd(globalJson, ReadVersions).TryGetValue(sdkReference.Name, out version);
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        if (string.IsNullOrEmpty(version))
        {
            // Let the registered workload resolver and the built-in SDK directory resolver handle these.
            return factory.IndicateFailure([$"No pinned cached version of {sdkReference.Name}."]);
        }

        if (!NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion))
        {
            throw new NotSupportedException($"SDK '{sdkReference.Name}' must have an exact NuGet version, not '{version}'.");
        }

        foreach (char character in sdkReference.Name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                throw new NotSupportedException($"Invalid cached SDK package ID '{sdkReference.Name}'.");
            }
        }

        if (sdkReference.Name is "." or "..")
        {
            throw new NotSupportedException($"Invalid cached SDK package ID '{sdkReference.Name}'.");
        }

        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? throw new InvalidOperationException("Set NUGET_PACKAGES to the warmed benchmark cache.");
        string normalizedVersion = parsedVersion.ToNormalizedString();
        string package = Path.Combine(packages, sdkReference.Name.ToLowerInvariant(), normalizedVersion.ToLowerInvariant());
        string path = Path.Combine(package, "sdk");
        if (!Directory.Exists(path))
        {
            path = Path.Combine(package, "Sdk");
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Cached SDK '{sdkReference.Name}/{normalizedVersion}' is missing: {path}. No download or JIT fallback is allowed.");
        }

        return factory.IndicateSuccess(path, normalizedVersion);
    }

    private static Dictionary<string, string> ReadVersions(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("msbuild-sdks", out JsonElement sdks))
        {
            foreach (JsonProperty property in sdks.EnumerateObject())
            {
                versions.Add(property.Name, property.Value.GetString()
                    ?? throw new InvalidDataException($"Null SDK version for '{property.Name}' in '{file}'."));
            }
        }

        return versions;
    }
}
