// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedActionGraph
{
    internal const string HostOSProperty = "MSBuildGraphHostOS";

    private static readonly HashSet<string> s_intrinsicFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Subtract", "Multiply", "Divide", "Modulo", "BitwiseAnd", "BitwiseOr",
        "BitwiseXor", "BitwiseNot", "LeftShift", "RightShift", "Escape", "Unescape",
        "EnsureTrailingSlash", "ValueOrDefault", "NormalizePath", "NormalizeDirectory",
        "VersionEquals", "VersionNotEquals", "VersionGreaterThan", "VersionGreaterThanOrEquals",
        "VersionLessThan", "VersionLessThanOrEquals",
        "GetTargetFrameworkIdentifier", "GetTargetFrameworkVersion", "IsTargetFrameworkCompatible",
        "GetTargetPlatformIdentifier", "GetTargetPlatformVersion", "FilterTargetFrameworks",
        nameof(IntrinsicFunctions.IsOSPlatform),
    };

    internal static string GetHostOS(string? value, IElementLocation location)
    {
        string hostOS =
            string.Equals(value, "windows", StringComparison.OrdinalIgnoreCase) ? "windows" :
            string.Equals(value, "linux", StringComparison.OrdinalIgnoreCase) ? "linux" :
            string.Equals(value, "osx", StringComparison.OrdinalIgnoreCase) ? "osx" :
            string.Equals(value, "freebsd", StringComparison.OrdinalIgnoreCase) ? "freebsd" :
            string.Empty;
        if (hostOS.Length == 0)
        {
            ProjectErrorUtilities.ThrowInvalidProject(
                location, "HardenedGraphUnsupportedConstruct",
                "missing or unsupported global MSBuildGraphHostOS; supply windows, linux, osx, or freebsd",
                "action graph construction");
        }

        return hostOS;
    }

    internal static bool IsAllowedFunction(Type receiver, string member)
    {
        string name = receiver.FullName ?? "";
        if (HardenedTargetValidator.IsProhibitedFunction(name.AsSpan(), member.AsSpan()))
        {
            return false;
        }

        return receiver.IsPrimitive || receiver.IsEnum ||
            (receiver.IsArray && string.Equals(member, nameof(Array.GetValue), StringComparison.OrdinalIgnoreCase)) ||
            name is "System.String" or "System.Math" or "System.MathF" or "System.Convert" or
                "System.Decimal" or "System.Version" or "System.Guid" or "System.DateTime" or
                "System.DateTimeOffset" or "System.TimeSpan" or "System.IO.Path" ||
            (receiver == typeof(IntrinsicFunctions) && s_intrinsicFunctions.Contains(member));
    }

    private readonly string _destination;
    private readonly string _host;
    private readonly string _dotnet;
    private readonly string _directory;
    private readonly string _hostOS;
    private readonly Dictionary<string, string> _environment;
    private readonly List<ActionInvocation> _actions = [];
    private readonly ElementLocation _location;

    internal HardenedActionGraph(ProjectInstance project, TaskEnvironment environment)
    {
        _location = project.ProjectFileLocation;
        _directory = project.Directory;
        Require(project.GlobalProperties.ContainsKey(HostOSProperty),
            $"{HostOSProperty} not supplied as a global construction input");
        _hostOS = GetHostOS(project.GlobalProperties[HostOSProperty], _location);
        Require(project.GlobalProperties.ContainsKey("MSBuildEmitActionGraph"),
            "MSBuildEmitActionGraph set inside a project instead of as a global property");
        Require(Path.IsPathRooted(project.GetPropertyValue("MSBuildEmitActionGraph")),
            "a nonabsolute MSBuildEmitActionGraph destination");
        _destination = Path.GetFullPath(project.GetPropertyValue("MSBuildEmitActionGraph"));
        _host = project.GetPropertyValue("MSBuildActionTaskHost");
        _dotnet = project.GetPropertyValue("MSBuildActionDotNetHost");
        Dictionary<string, string> actionEnvironment = new(StringComparer.Ordinal);
        foreach (string name in ExpressionShredder.SplitSemiColonSeparatedList(project.GetPropertyValue("MSBuildActionEnvironment")))
        {
            string? value = environment.GetEnvironmentVariable(name);
            Require(value is not null, $"missing explicitly selected environment variable '{name}'");
            actionEnvironment.Add(name, value!);
        }

        _environment = actionEnvironment;
        Require(!NativeMethodsShared.IsWindows, "Windows action command quoting");
        Require(Path.IsPathRooted(_host) && File.Exists(_host), "a missing or nonabsolute MSBuildActionTaskHost");
        Require(Path.IsPathRooted(_dotnet) && File.Exists(_dotnet), "a missing or nonabsolute MSBuildActionDotNetHost");
    }

    internal TaskEnvironment CreateConstructionEnvironment()
        => TaskEnvironment.CreateWithProjectDirectoryAndEnvironment(
            _directory, _environment);

    // True means the invocation was recorded, not executed.
    internal bool Record(ITask task, Dictionary<string, object> parameters, ProjectTaskInstance node)
    {
        Require(parameters.TryGetValue("DeclaredInputs", out object? inputValue) && inputValue is ITaskItem[], "missing DeclaredInputs", node.Location);
        Require(parameters.TryGetValue("DeclaredOutputs", out object? outputValue) && outputValue is ITaskItem[], "missing DeclaredOutputs", node.Location);
        string[] inputs = Paths((ITaskItem[])inputValue!);
        string[] outputs = Paths((ITaskItem[])outputValue!);
        if (inputs.Length == 0 && outputs.Length == 0)
        {
            return false;
        }

        Require(outputs.Length != 0, "a file-I/O action without outputs", node.Location);
        Require(node.Outputs.Count == 0, "Output mappings on file-I/O actions", node.Location);
        Require(task.HostObject is null, "task host objects", node.Location);
        foreach (string output in outputs)
        {
            Require(!Directory.Exists(output), $"directory output '{output}'", node.Location);
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter json = new(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteString("taskAssembly", task.GetType().Assembly.Location);
            json.WriteString("taskType", task.GetType().FullName);
            json.WriteString("workingDirectory", _directory);
            json.WriteString("projectFile", node.Location.File);
            json.WriteNumber("lineNumber", node.Location.Line);
            json.WriteNumber("columnNumber", node.Location.Column);
            json.WriteStartObject("environment");
            foreach (string name in SortedKeys(_environment))
            {
                json.WriteString(name, _environment[name]);
            }

            json.WriteEndObject();
            json.WriteStartObject("parameters");
            foreach (string name in SortedKeys(parameters))
            {
                json.WritePropertyName(name);
                WriteValue(json, parameters[name]);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        _actions.Add(new(stream.ToArray(), inputs, outputs, task.GetType().Assembly.Location));
        return true;
    }

    internal void Publish()
    {
        Dictionary<string, int> producers = new(FileUtilities.PathComparer);
        for (int i = 0; i < _actions.Count; i++)
        {
            foreach (string output in _actions[i].Outputs)
            {
                foreach (string existing in producers.Keys)
                {
                    Require(!Conflicts(existing, output), $"conflicting outputs '{existing}' and '{output}'");
                }

                Require(!Conflicts(output, _destination) && !Conflicts(output, _destination + ".actions"),
                    $"task output '{output}' overlapping graph metadata");
                producers.Add(output, i);
            }
        }

        for (int i = 0; i < _actions.Count; i++)
        {
            foreach (string input in _actions[i].Inputs)
            {
                if (producers.TryGetValue(input, out int producer))
                {
                    Require(producer < i, $"in-place, cyclic, or forward-produced input '{input}'");
                }
                else
                {
                    Require(File.Exists(input) && !Directory.Exists(input), $"missing regular-file input '{input}'");
                    foreach (string output in producers.Keys)
                    {
                        Require(!Conflicts(input, output), $"overlapping input/output paths '{input}' and '{output}'");
                    }
                }
            }
        }

        string manifestDirectory = _destination + ".actions";
        StringBuilder ninja = new("ninja_required_version = 1.10\n");
        ninja.Append("msbuild_construction_host_os = ").Append(_hostOS).Append("\n\n");
        List<string> previousOutputs = [];
        List<string> allOutputs = [];
        List<(string Path, byte[] Contents)> manifests = [];
        for (int i = 0; i < _actions.Count; i++)
        {
            ActionInvocation action = _actions[i];
            using SHA256 hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(action.Contents)).Replace("-", "").ToLowerInvariant();
            string manifest = Path.Combine(manifestDirectory, $"{i}-{digest}.json");
            manifests.Add((manifest, action.Contents));
            string rule = "action_" + i.ToString(CultureInfo.InvariantCulture);
            ninja.Append("rule ").Append(rule).Append("\n  command = ")
                .Append(Command(_dotnet)).Append(' ').Append(Command(_host))
                .Append(" --invocation ").Append(Command(manifest)).Append('\n');
            ninja.Append("build");
            AppendPaths(ninja, action.Outputs);
            ninja.Append(": ").Append(rule);
            AppendPaths(ninja, action.Inputs);
            ninja.Append(" | ").Append(EscapePath(manifest));
            HashSet<string> tools = new(FileUtilities.PathComparer) { _host, _dotnet, action.Assembly };
            AddToolDirectory(tools, Path.GetDirectoryName(_host)!);
            AddToolDirectory(tools, Path.GetDirectoryName(action.Assembly)!);
            List<string> sortedTools = [.. tools];
            sortedTools.Sort(StringComparer.Ordinal);
            AppendPaths(ninja, sortedTools);
            if (previousOutputs.Count != 0)
            {
                ninja.Append(" ||");
                AppendPaths(ninja, previousOutputs);
            }

            ninja.Append("\n\n");
            previousOutputs = [.. action.Outputs];
            allOutputs.AddRange(action.Outputs);
        }

        if (allOutputs.Count != 0)
        {
            ninja.Append("default");
            AppendPaths(ninja, allOutputs);
            ninja.Append('\n');
        }

        // Validate and format the entire graph before publishing any executable entry point.
        Directory.CreateDirectory(manifestDirectory);
        foreach ((string path, byte[] contents) in manifests)
        {
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, contents);
            }
        }

        string text = ninja.ToString();
        if (File.Exists(_destination) && File.ReadAllText(_destination) == text)
        {
            return;
        }

        string temporary = _destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(_destination))
            {
                File.Replace(temporary, _destination, null);
            }
            else
            {
                File.Move(temporary, _destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void WriteValue(Utf8JsonWriter json, object value)
    {
        if (value is ITaskItem item)
        {
            json.WriteStartObject();
            json.WriteString("itemSpec", item.ItemSpec);
            json.WriteStartObject("metadata");
            IDictionary metadata = item.CloneCustomMetadata();
            List<string> names = [];
            foreach (string name in metadata.Keys)
            {
                names.Add(name);
            }

            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
            {
                json.WriteString(name, (string)metadata[name]!);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }
        else if (value is Array array)
        {
            Require(array.Rank == 1, $"parameter type '{value.GetType()}'");
            Type element = value.GetType().GetElementType()!;
            Require(element == typeof(ITaskItem) || ValueTypeParser.IsSupportedType(element) || element.IsEnum,
                $"parameter type '{value.GetType()}'");
            json.WriteStartArray();
            foreach (object entry in array)
            {
                WriteValue(json, entry);
            }

            json.WriteEndArray();
        }
        else
        {
            Require(value is not null && (ValueTypeParser.IsSupportedType(value.GetType()) || value.GetType().IsEnum),
                $"parameter type '{value?.GetType()}'");
            json.WriteStringValue(value is DateTime dateTime
                ? dateTime.ToString("O", CultureInfo.InvariantCulture)
                : ValueTypeParser.ToString(value));
        }
    }

    private string[] Paths(ITaskItem[] items)
    {
        string[] paths = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            string path = items[i].ItemSpec;
            Require(!string.IsNullOrWhiteSpace(path) && path.IndexOfAny(['*', '?']) < 0, $"nonexact declared path '{path}'");
            paths[i] = Path.GetFullPath(Path.Combine(_directory, path));
        }

        return paths;
    }

    private static List<string> SortedKeys<T>(IReadOnlyDictionary<string, T> dictionary)
    {
        List<string> keys = [.. dictionary.Keys];
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    private static bool Conflicts(string first, string second)
        => FileUtilities.PathsEqual(first, second)
            || first.StartsWith(second.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, FileUtilities.PathComparison)
            || second.StartsWith(first.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, FileUtilities.PathComparison);

    private static void AddToolDirectory(HashSet<string> tools, string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file) is ".dll" or ".json" or ".so" or ".dylib")
            {
                tools.Add(file);
            }
        }
    }

    private static void AppendPaths(StringBuilder ninja, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            ninja.Append(' ').Append(EscapePath(path));
        }
    }

    private string Command(string value)
    {
        Require(value.IndexOfAny(['\r', '\n']) < 0, "newlines in action command paths");
        return "'" + value.Replace("'", "'\"'\"'").Replace("$", "$$") + "'";
    }

    private static string EscapePath(string path)
    {
        ProjectErrorUtilities.VerifyThrowInvalidProject(
            path.IndexOfAny(['\r', '\n', '\t', '|', '#']) < 0,
            ElementLocation.EmptyLocation, "HardenedGraphUnsupportedConstruct", $"Ninja path '{path}'", "action graph construction");
        return path.Replace("$", "$$").Replace(" ", "$ ").Replace(":", "$:");
    }

    private void Require(bool condition, string construct, ElementLocation? location = null)
        => ProjectErrorUtilities.VerifyThrowInvalidProject(condition, location ?? _location,
            "HardenedGraphUnsupportedConstruct", construct, "action graph construction");

    private sealed record ActionInvocation(byte[] Contents, string[] Inputs, string[] Outputs, string Assembly);
}
