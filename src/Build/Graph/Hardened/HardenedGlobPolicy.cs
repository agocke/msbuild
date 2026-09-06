// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
#if FEATURE_WINDOWSINTEROP
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
#endif
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedGlobPolicy(string? workspaceRoot)
{
    private readonly string? _workspaceRoot = workspaceRoot;

    internal void ValidatePattern(
        string projectDirectory,
        string patternEscaped,
        ElementLocation location)
    {
        if (!FileMatcher.HasWildcards(patternEscaped))
        {
            return;
        }

        string pattern = EscapingUtilities.UnescapeAll(patternEscaped);
        FileMatcher.Default.GetFileSpecInfo(
            pattern,
            out string fixedDirectoryPart,
            out _,
            out _,
            out _,
            out _);

        string declaredRoot = Path.GetFullPath(
            Path.IsPathRooted(fixedDirectoryPart)
                ? fixedDirectoryPart
                : Path.Combine(projectDirectory, fixedDirectoryPart));
        string boundary = GetBoundary(
            projectDirectory,
            patternEscaped,
            declaredRoot,
            location);
        ValidatePath(patternEscaped, declaredRoot, boundary, location);
    }

    internal void ValidateMatch(
        string projectDirectory,
        string patternEscaped,
        string matchEscaped,
        ElementLocation location)
    {
        if (!FileMatcher.HasWildcards(patternEscaped))
        {
            return;
        }

        string match = EscapingUtilities.UnescapeAll(matchEscaped);
        string declaredPath = Path.GetFullPath(
            Path.IsPathRooted(match)
                ? match
                : Path.Combine(projectDirectory, match));
        string boundary = GetPatternBoundary(projectDirectory, patternEscaped, location);
        ValidatePath(patternEscaped, declaredPath, boundary, location);
    }

    private string GetPatternBoundary(
        string projectDirectory,
        string patternEscaped,
        ElementLocation location)
    {
        string pattern = EscapingUtilities.UnescapeAll(patternEscaped);
        FileMatcher.Default.GetFileSpecInfo(
            pattern,
            out string fixedDirectoryPart,
            out _,
            out _,
            out _,
            out _);
        string declaredRoot = Path.GetFullPath(
            Path.IsPathRooted(fixedDirectoryPart)
                ? fixedDirectoryPart
                : Path.Combine(projectDirectory, fixedDirectoryPart));
        return GetBoundary(projectDirectory, patternEscaped, declaredRoot, location);
    }

    private string GetBoundary(
        string projectDirectory,
        string patternEscaped,
        string declaredRoot,
        ElementLocation location)
    {
        string projectRoot = NormalizeRoot(projectDirectory);
        if (IsWithinWorkspace(declaredRoot, projectRoot))
        {
            return _workspaceRoot ?? projectRoot;
        }

        if (_workspaceRoot is not null)
        {
            return _workspaceRoot;
        }

        ProjectErrorUtilities.ThrowInvalidProject(
            location,
            "HardenedGraphWorkspaceRootRequired",
            EscapingUtilities.UnescapeAll(patternEscaped));
        return null!;
    }

    private static string NormalizeRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length == pathRoot.Length
            ? fullPath
            : fullPath.TrimTrailingSlashes();
    }

    private static bool IsWithinWorkspace(string path, string workspaceRoot)
    {
        if (string.Equals(path, workspaceRoot, FileUtilities.PathComparison))
        {
            return true;
        }

        string pathRoot = Path.GetPathRoot(workspaceRoot) ?? string.Empty;
        if (workspaceRoot.Length == pathRoot.Length)
        {
            return path.StartsWith(workspaceRoot, FileUtilities.PathComparison);
        }

        ReadOnlySpan<char> root = workspaceRoot.AsSpan();
        return path.Length > root.Length &&
               path.AsSpan(0, root.Length).Equals(root, FileUtilities.PathComparison) &&
               FileUtilities.IsSlash(path[root.Length]);
    }

    private static void ValidatePath(
        string patternEscaped,
        string declaredPath,
        string workspaceRoot,
        ElementLocation location)
    {
        if (!IsWithinWorkspace(declaredPath, workspaceRoot))
        {
            ThrowOutsideWorkspace(patternEscaped, declaredPath, workspaceRoot, location);
        }

        string resolvedPath = ResolveLinks(
            declaredPath,
            patternEscaped,
            workspaceRoot,
            location);
        if (!IsWithinWorkspace(resolvedPath, workspaceRoot))
        {
            ThrowOutsideWorkspace(patternEscaped, resolvedPath, workspaceRoot, location);
        }
    }

    private static string ResolveLinks(
        string path,
        string patternEscaped,
        string workspaceRoot,
        ElementLocation location)
    {
#if FEATURE_SYMLINK_TARGET
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string current = root;
        string relative = fullPath[root.Length..];
        int start = 0;
        while (start < relative.Length)
        {
            int separator = relative.AsSpan(start).IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int length = separator < 0 ? relative.Length - start : separator;
            if (length > 0)
            {
                string segment = relative.Substring(start, length);
                string candidate = Path.Combine(current, segment);
                FileSystemInfo? fileSystemInfo = Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : File.Exists(candidate)
                        ? new FileInfo(candidate)
                        : null;
                if (fileSystemInfo?.LinkTarget is not null)
                {
                    FileSystemInfo? target = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
                    current = target?.FullName ?? candidate;
                }
                else
                {
                    current = candidate;
                }
            }

            if (separator < 0)
            {
                break;
            }

            start += length + 1;
        }

        return Path.GetFullPath(current);
#elif FEATURE_WINDOWSINTEROP
        if (NativeMethodsShared.IsWindows)
        {
            return ResolveWindowsLinks(path, patternEscaped, workspaceRoot, location);
        }

        return Path.GetFullPath(path);
#else
        return Path.GetFullPath(path);
#endif
    }

#if FEATURE_WINDOWSINTEROP
    [SupportedOSPlatform("windows6.0.6000")]
    private static unsafe string ResolveWindowsLinks(
        string path,
        string patternEscaped,
        string workspaceRoot,
        ElementLocation location)
    {
        string fullPath = Path.GetFullPath(path);
        HANDLE handle = PInvoke.CreateFile(
            fullPath,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ |
                FILE_SHARE_MODE.FILE_SHARE_WRITE |
                FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            HANDLE.Null);
        using var safeHandle = new SafeFileHandle((IntPtr)handle.Value, ownsHandle: true);
        if (safeHandle.IsInvalid)
        {
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                ThrowUnresolvablePath(patternEscaped, fullPath, workspaceRoot, location);
            }

            return fullPath;
        }

        char[] buffer = new char[260];
        while (true)
        {
            uint length;
            fixed (char* bufferPointer = buffer)
            {
                length = PInvoke.GetFinalPathNameByHandle(
                    (HANDLE)safeHandle.DangerousGetHandle(),
                    bufferPointer,
                    (uint)buffer.Length,
                    0);
            }

            if (length == 0)
            {
                ThrowUnresolvablePath(patternEscaped, fullPath, workspaceRoot, location);
            }

            if (length < buffer.Length)
            {
                return NormalizeWindowsFinalPath(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }
    }

    private static string NormalizeWindowsFinalPath(string path)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(uncPrefix.Length);
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(devicePrefix.Length)
            : path;
    }
    private static void ThrowUnresolvablePath(
        string patternEscaped,
        string path,
        string workspaceRoot,
        ElementLocation location)
        => ProjectErrorUtilities.ThrowInvalidProject(
            location,
            "HardenedGraphUnresolvableLinkPath",
            EscapingUtilities.UnescapeAll(patternEscaped),
            path,
            workspaceRoot);
#endif

    private static void ThrowOutsideWorkspace(
        string patternEscaped,
        string path,
        string workspaceRoot,
        ElementLocation location)
        => ProjectErrorUtilities.ThrowInvalidProject(
            location,
            "HardenedGraphPathOutsideWorkspace",
            EscapingUtilities.UnescapeAll(patternEscaped),
            path,
            workspaceRoot);
}
