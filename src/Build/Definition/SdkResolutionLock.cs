// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;
using InternalSdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// An immutable set of SDK resolver results that can be consumed by pure evaluation.
    /// </summary>
    public sealed class SdkResolutionLock
    {
        private readonly IReadOnlyDictionary<SdkResolutionKey, SdkResolutionLockEntry> _entriesByKey;

        /// <summary>
        /// Creates a lock from previously acquired SDK resolver results.
        /// </summary>
        public SdkResolutionLock(IEnumerable<SdkResolutionLockEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            var entriesByKey = new Dictionary<SdkResolutionKey, SdkResolutionLockEntry>();
            foreach (SdkResolutionLockEntry entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                if (entriesByKey.ContainsKey(entry.Key))
                {
                    throw new ArgumentException(
                        $"The SDK resolution lock contains more than one result for '{entry.Name}'.",
                        nameof(entries));
                }

                entriesByKey.Add(entry.Key, entry);
            }

            Entries = Array.AsReadOnly(
                entriesByKey.Values
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.MinimumVersion, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.ProjectPath, StringComparer.Ordinal)
                    .ThenBy(entry => entry.SolutionPath, StringComparer.Ordinal)
                    .ToArray());
            _entriesByKey = new ReadOnlyDictionary<SdkResolutionKey, SdkResolutionLockEntry>(
                entriesByKey);
            Identity = ComputeIdentity(Entries);
        }

        /// <summary>
        /// Gets the locked SDK resolver results in deterministic order.
        /// </summary>
        public IReadOnlyList<SdkResolutionLockEntry> Entries { get; }

        /// <summary>
        /// Gets a stable SHA-256 identity for the complete lock contents.
        /// </summary>
        public string Identity { get; }

        internal static SdkResolutionLock Empty { get; } = new(Array.Empty<SdkResolutionLockEntry>());

        internal bool TryGet(
            SdkReference sdk,
            out SdkResolutionLockEntry entry) =>
            _entriesByKey.TryGetValue(
                new SdkResolutionKey(sdk.Name),
                out entry);

        private static string ComputeIdentity(
            IReadOnlyList<SdkResolutionLockEntry> entries)
        {
            var builder = new StringBuilder();
            foreach (SdkResolutionLockEntry entry in entries)
            {
                entry.AppendIdentity(builder);
            }

            using SHA256 sha256 = SHA256.Create();
            return ToHexString(
                sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static string ToHexString(byte[] value)
        {
#if NET
            return Convert.ToHexStringLower(value);
#else
            return BitConverter.ToString(value).Replace("-", string.Empty).ToLowerInvariant();
#endif
        }
    }

    /// <summary>
    /// One SDK request and its acquired resolver result.
    /// </summary>
    public sealed class SdkResolutionLockEntry
    {
        private readonly string _resultIdentity;

        /// <summary>
        /// Creates an SDK resolution lock entry.
        /// </summary>
        public SdkResolutionLockEntry(
            string name,
            string version,
            string minimumVersion,
            string solutionPath,
            string projectPath,
            bool success,
            string resolvedPath,
            string resolvedVersion,
            IEnumerable<string> additionalPaths,
            IReadOnlyDictionary<string, string> propertiesToAdd,
            IReadOnlyDictionary<string, SdkResolutionLockItem> itemsToAdd,
            IReadOnlyDictionary<string, string> environmentVariablesToAdd,
            IEnumerable<string> warnings,
            IEnumerable<string> errors,
            string resolverName,
            string resolverIdentity)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "An SDK resolution lock entry requires an SDK name.",
                    nameof(name));
            }

            Name = name;
            Version = version;
            MinimumVersion = minimumVersion;
            SolutionPath = solutionPath;
            ProjectPath = projectPath;
            Success = success;
            ResolvedPath = resolvedPath;
            ResolvedVersion = resolvedVersion;
            AdditionalPaths = Array.AsReadOnly(
                (additionalPaths ?? Array.Empty<string>()).ToArray());
            PropertiesToAdd = CopyDictionary(propertiesToAdd);
            ItemsToAdd = CopyItems(itemsToAdd);
            EnvironmentVariablesToAdd = CopyDictionary(environmentVariablesToAdd);
            Warnings = Array.AsReadOnly(
                (warnings ?? Array.Empty<string>()).ToArray());
            Errors = Array.AsReadOnly(
                (errors ?? Array.Empty<string>()).ToArray());
            ResolverName = resolverName;
            ResolverIdentity = resolverIdentity;
            _resultIdentity = ComputeResultIdentity();
        }

        public string Name { get; }

        public string Version { get; }

        public string MinimumVersion { get; }

        public string SolutionPath { get; }

        public string ProjectPath { get; }

        public bool Success { get; }

        public string ResolvedPath { get; }

        public string ResolvedVersion { get; }

        public IReadOnlyList<string> AdditionalPaths { get; }

        public IReadOnlyDictionary<string, string> PropertiesToAdd { get; }

        public IReadOnlyDictionary<string, SdkResolutionLockItem> ItemsToAdd { get; }

        public IReadOnlyDictionary<string, string> EnvironmentVariablesToAdd { get; }

        public IReadOnlyList<string> Warnings { get; }

        public IReadOnlyList<string> Errors { get; }

        public string ResolverName { get; }

        public string ResolverIdentity { get; }

        internal SdkResolutionKey Key => new(Name);

        internal string ResultIdentity => _resultIdentity;

        private string ComputeResultIdentity()
        {
            var builder = new StringBuilder();
            AppendResultIdentity(builder);
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(
                Encoding.UTF8.GetBytes(builder.ToString()));
#if NET
            return Convert.ToHexStringLower(hash);
#else
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
#endif
        }

        internal static SdkResolutionLockEntry FromResult(
            SdkReference sdk,
            string solutionPath,
            string projectPath,
            InternalSdkResult result)
        {
            var items = new Dictionary<string, SdkResolutionLockItem>(
                StringComparer.OrdinalIgnoreCase);
            if (result.ItemsToAdd != null)
            {
                foreach (KeyValuePair<string, SdkResultItem> item in result.ItemsToAdd)
                {
                    items[item.Key] = new(
                        item.Value.ItemSpec,
                        item.Value.Metadata);
                }
            }

            IReadOnlyDictionary<string, string> environmentVariables =
                CopyResolverDictionary(result.EnvironmentVariablesToAdd);
            if (result.PropertiesToAdd?.ContainsKey(
                    "DOTNET_EXPERIMENTAL_HOST_PATH") == true &&
                !environmentVariables.ContainsKey(Constants.DotnetHostPathEnvVarName))
            {
                string dotnetExe = Path.Combine(
                    FileUtilities.GetFolderAbove(result.Path, 5),
                    Constants.DotnetProcessName);
                if (FileSystems.Default.FileExists(dotnetExe))
                {
                    var normalizedEnvironmentVariables =
                        ToMutableDictionary(environmentVariables);
                    normalizedEnvironmentVariables[
                        Constants.DotnetHostPathEnvVarName] = dotnetExe;
                    environmentVariables =
                        new ReadOnlyDictionary<string, string>(
                            normalizedEnvironmentVariables);
                }
            }

            return new(
                sdk.Name,
                sdk.Version,
                sdk.MinimumVersion,
                solutionPath,
                projectPath,
                result.Success,
                result.Path,
                result.Version,
                result.AdditionalPaths,
                CopyResolverDictionary(result.PropertiesToAdd),
                items,
                environmentVariables,
                result.Warnings,
                result.Errors,
                result.ResolverName,
                result.ResolverIdentity);
        }

        internal InternalSdkResult ToResult(
            SdkReference sdk,
            Construction.ElementLocation location)
        {
            var items = new Dictionary<string, SdkResultItem>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, SdkResolutionLockItem> item in ItemsToAdd)
            {
                items[item.Key] = new(
                    item.Value.ItemSpec,
                    ToMutableDictionary(item.Value.Metadata));
            }

            InternalSdkResult result = Success
                ? new InternalSdkResult(
                    sdk,
                    GetPaths(),
                    ResolvedVersion,
                    ToMutableDictionary(PropertiesToAdd),
                    items,
                    Warnings,
                    ToMutableDictionary(EnvironmentVariablesToAdd))
                : new InternalSdkResult(sdk, Errors, Warnings);
            result.ElementLocation = location;
            result.ResolverName = ResolverName;
            result.ResolverIdentity = ResolverIdentity;
            return result;
        }

        internal void AppendIdentity(StringBuilder builder)
        {
            Append(builder, "entry");
            Append(builder, Name);
            Append(builder, Version);
            Append(builder, MinimumVersion);
            Append(builder, SolutionPath);
            Append(builder, ProjectPath);
            AppendResultIdentity(builder);
        }

        private void AppendResultIdentity(StringBuilder builder)
        {
            Append(builder, Success ? "true" : "false");
            Append(builder, ResolvedPath);
            Append(builder, ResolvedVersion);
            Append(builder, ResolverName);
            Append(builder, ResolverIdentity);
            Append(builder, "additional-paths");
            Append(
                builder,
                AdditionalPaths.Count.ToString(CultureInfo.InvariantCulture));
            AppendSequence(builder, AdditionalPaths);
            Append(builder, "properties");
            Append(
                builder,
                PropertiesToAdd.Count.ToString(CultureInfo.InvariantCulture));
            AppendDictionary(builder, PropertiesToAdd);
            Append(builder, "items");
            Append(
                builder,
                ItemsToAdd.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, SdkResolutionLockItem> item in
                     ItemsToAdd.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Append(builder, item.Key);
                Append(builder, item.Value.ItemSpec);
                AppendDictionary(builder, item.Value.Metadata);
            }

            Append(builder, "environment");
            Append(
                builder,
                EnvironmentVariablesToAdd.Count.ToString(CultureInfo.InvariantCulture));
            AppendDictionary(builder, EnvironmentVariablesToAdd);
            Append(builder, "warnings");
            Append(
                builder,
                Warnings.Count.ToString(CultureInfo.InvariantCulture));
            AppendSequence(builder, Warnings);
            Append(builder, "errors");
            Append(
                builder,
                Errors.Count.ToString(CultureInfo.InvariantCulture));
            AppendSequence(builder, Errors);
        }

        private IEnumerable<string> GetPaths()
        {
            if (ResolvedPath != null)
            {
                yield return ResolvedPath;
            }

            foreach (string path in AdditionalPaths)
            {
                yield return path;
            }
        }

        private static IReadOnlyDictionary<string, string> CopyDictionary(
            IReadOnlyDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source)
                {
                    result.Add(pair.Key, pair.Value);
                }
            }

            return new ReadOnlyDictionary<string, string>(result);
        }

        private static IReadOnlyDictionary<string, string> CopyResolverDictionary(
            IDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source)
                {
                    result.Add(pair.Key, pair.Value);
                }
            }

            return new ReadOnlyDictionary<string, string>(result);
        }

        private static Dictionary<string, string> ToMutableDictionary(
            IReadOnlyDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in source)
            {
                result.Add(pair.Key, pair.Value);
            }

            return result;
        }

        private static IReadOnlyDictionary<string, SdkResolutionLockItem> CopyItems(
            IReadOnlyDictionary<string, SdkResolutionLockItem> source)
        {
            var result = new Dictionary<string, SdkResolutionLockItem>(
                StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, SdkResolutionLockItem> pair in source)
                {
                    result.Add(
                        pair.Key,
                        new SdkResolutionLockItem(
                            pair.Value.ItemSpec,
                            pair.Value.Metadata));
                }
            }

            return new ReadOnlyDictionary<string, SdkResolutionLockItem>(result);
        }

        private static void AppendSequence(
            StringBuilder builder,
            IEnumerable<string> values)
        {
            foreach (string value in values)
            {
                Append(builder, value);
            }
        }

        private static void AppendDictionary(
            StringBuilder builder,
            IReadOnlyDictionary<string, string> values)
        {
            foreach (KeyValuePair<string, string> pair in
                     values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Append(builder, pair.Key);
                Append(builder, pair.Value);
            }
        }

        private static void Append(StringBuilder builder, string value)
        {
            value ??= string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
            builder.Append(';');
        }
    }

    /// <summary>
    /// An item injected into evaluation by an SDK resolver result.
    /// </summary>
    public sealed class SdkResolutionLockItem
    {
        public SdkResolutionLockItem(
            string itemSpec,
            IReadOnlyDictionary<string, string> metadata)
        {
            ItemSpec = itemSpec ?? string.Empty;
            var copiedMetadata = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            if (metadata != null)
            {
                foreach (KeyValuePair<string, string> pair in metadata)
                {
                    copiedMetadata.Add(pair.Key, pair.Value);
                }
            }

            Metadata = new ReadOnlyDictionary<string, string>(copiedMetadata);
        }

        public string ItemSpec { get; }

        public IReadOnlyDictionary<string, string> Metadata { get; }
    }

    internal readonly struct SdkResolutionKey : IEquatable<SdkResolutionKey>
    {
        internal SdkResolutionKey(string name)
        {
            Name = name ?? string.Empty;
        }

        internal string Name { get; }

        public bool Equals(SdkResolutionKey other) =>
            StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);

        public override bool Equals(object obj) =>
            obj is SdkResolutionKey other && Equals(other);

        public override int GetHashCode()
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
        }
    }
}
