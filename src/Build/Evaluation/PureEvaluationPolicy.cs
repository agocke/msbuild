// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Classifies property functions that may execute during pure evaluation.
    /// </summary>
    internal static class PureEvaluationPolicy
    {
        private static readonly FrozenSet<string> s_forbiddenIntrinsicFunctions = new[]
        {
            nameof(IntrinsicFunctions.GetRegistryValue),
            nameof(IntrinsicFunctions.GetRegistryValueFromView),
            nameof(IntrinsicFunctions.DoesTaskHostExist),
            nameof(IntrinsicFunctions.FileExists),
            nameof(IntrinsicFunctions.DirectoryExists),
            nameof(IntrinsicFunctions.RegisterBuildCheck),
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> s_forbiddenPathFunctions = new[]
        {
            nameof(Path.GetFullPath),
            nameof(Path.GetRandomFileName),
            nameof(Path.GetTempFileName),
            nameof(Path.GetTempPath),
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> s_pureFileSystemInfoMembers = new[]
        {
            nameof(FileSystemInfo.FullName),
            nameof(FileSystemInfo.Name),
            nameof(FileSystemInfo.Extension),
            nameof(FileSystemInfo.ToString),
            nameof(DirectoryInfo.Parent),
            nameof(DirectoryInfo.Root),
            nameof(FileInfo.Directory),
            nameof(FileInfo.DirectoryName),
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private static readonly FrozenSet<string> s_pureStaticReceiverTypes = new[]
        {
            "System.Byte",
            "System.Char",
            "System.Convert",
            "System.Decimal",
            "System.Double",
            "System.Enum",
            "System.Int16",
            "System.Int32",
            "System.Int64",
            "System.Math",
            "System.OperatingSystem",
            "System.Runtime.InteropServices.OSPlatform",
            "System.Runtime.InteropServices.RuntimeInformation",
            "System.SByte",
            "System.Single",
            "System.String",
            "System.Text.RegularExpressions.Regex",
            "System.TimeSpan",
            "System.UInt16",
            "System.UInt32",
            "System.UInt64",
            "System.Uri",
            "System.UriBuilder",
            "System.Version",
            "Microsoft.Build.Framework.OperatingSystem",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        internal static bool IsPropertyFunctionAllowed(Type receiverType, string methodName, bool isStatic, object[] arguments)
        {
            if (!isStatic)
            {
                if (typeof(FileSystemInfo).IsAssignableFrom(receiverType))
                {
                    return s_pureFileSystemInfoMembers.Contains(methodName);
                }

                // Pure mode never honors the unrestricted-property-function escape hatch. Every runtime
                // receiver reached by a chain must remain on the bounded instance allowlist.
                return PropertyFunctionReceiver.IsAllowed(receiverType, methodName);
            }

            if (receiverType == typeof(Environment)
                || receiverType == typeof(File))
            {
                return false;
            }

            if (string.Equals(
                    receiverType.FullName,
                    "Microsoft.Build.Utilities.ToolLocationHelper",
                    StringComparison.Ordinal))
            {
                return arguments.Length == 2
                    && arguments[1] is string version
                    && version.Length == 0
                    && (string.Equals(
                            methodName,
                            "GetPlatformSDKLocation",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            methodName,
                            "GetPlatformSDKDisplayName",
                            StringComparison.OrdinalIgnoreCase));
            }

            if (receiverType == typeof(Directory))
            {
                return string.Equals(
                        methodName,
                        nameof(Directory.GetParent),
                        StringComparison.OrdinalIgnoreCase)
                    && arguments.Length == 1
                    && arguments[0] is string path
                    && IsPathFullyQualified(path);
            }

            if (receiverType == typeof(IntrinsicFunctions))
            {
                return !s_forbiddenIntrinsicFunctions.Contains(methodName);
            }

            if (receiverType == typeof(DateTime))
            {
                return !string.Equals(methodName, nameof(DateTime.Now), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(methodName, nameof(DateTime.UtcNow), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(methodName, nameof(DateTime.Today), StringComparison.OrdinalIgnoreCase);
            }

            if (receiverType == typeof(DateTimeOffset))
            {
                return !string.Equals(methodName, nameof(DateTimeOffset.Now), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(methodName, nameof(DateTimeOffset.UtcNow), StringComparison.OrdinalIgnoreCase);
            }

            if (receiverType == typeof(Guid))
            {
                return !string.Equals(methodName, nameof(Guid.NewGuid), StringComparison.OrdinalIgnoreCase);
            }

            if (receiverType == typeof(Path))
            {
                if (!s_forbiddenPathFunctions.Contains(methodName))
                {
                    return true;
                }

                // The two-argument overload has an explicit base and is deterministic.
                return string.Equals(methodName, nameof(Path.GetFullPath), StringComparison.OrdinalIgnoreCase)
                    && (arguments.Length == 2
                        || (arguments.Length == 1
                            && arguments[0] is string path
                            && IsPathFullyQualified(path)));
            }

            if (receiverType == typeof(CultureInfo))
            {
                return string.Equals(methodName, nameof(CultureInfo.GetCultureInfo), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(methodName, "new", StringComparison.OrdinalIgnoreCase);
            }

            if (receiverType == typeof(StringComparer))
            {
                return !string.Equals(methodName, nameof(StringComparer.CurrentCulture), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(methodName, nameof(StringComparer.CurrentCultureIgnoreCase), StringComparison.OrdinalIgnoreCase);
            }

            return receiverType.FullName is string receiverTypeName
                && s_pureStaticReceiverTypes.Contains(receiverTypeName);
        }

        private static bool IsPathFullyQualified(string path)
        {
#if NETFRAMEWORK
            return Microsoft.IO.Path.IsPathFullyQualified(path);
#else
            return Path.IsPathFullyQualified(path);
#endif
        }
    }
}
