// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Build.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.ActionTaskHost;

internal static class TaskParameterBinder
{
    internal static Dictionary<string, object> Bind(ITask task, Dictionary<string, JsonElement> parameters)
    {
        Type taskType = task.GetType();
        Dictionary<string, PropertyInfo> properties = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (PropertyInfo property in taskType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            properties.Add(property.Name, property);
        }

        Dictionary<string, object> bound = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (KeyValuePair<string, JsonElement> parameter in parameters)
        {
            if (!properties.TryGetValue(parameter.Key, out PropertyInfo? property) ||
                property.SetMethod is not { IsPublic: true } ||
                property.GetIndexParameters().Length != 0 ||
                property.Name is nameof(ITask.BuildEngine) or nameof(ITask.HostObject)
                    or nameof(IMultiThreadableTask.TaskEnvironment))
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameter", parameter.Key, taskType.FullName));
            }

            if (bound.ContainsKey(property.Name))
            {
                throw new InvalidDataException(HostResources.Format("DuplicateParameter", parameter.Key));
            }

            object value;
            try
            {
                value = ConvertValue(parameter.Value, property.PropertyType);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException(HostResources.Format("InvalidParameterValue", parameter.Key, property.PropertyType), ex);
            }

            property.SetValue(task, value);
            bound.Add(property.Name, value);
        }

        foreach (PropertyInfo property in properties.Values)
        {
            if (property.IsDefined(typeof(RequiredAttribute), inherit: true) && !bound.ContainsKey(property.Name))
            {
                throw new InvalidDataException(HostResources.Format("RequiredParameter", property.Name, taskType.FullName));
            }
        }

        return bound;
    }

    private static object ConvertValue(JsonElement value, Type type)
    {
        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            if (type.GetArrayRank() != 1 || !IsSupportedScalarType(elementType))
            {
                throw new NotSupportedException(HostResources.Format("UnsupportedParameterType", type));
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }

            Array array = Array.CreateInstance(elementType, value.GetArrayLength());
            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                array.SetValue(ConvertValue(element, elementType), index++);
            }

            return array;
        }

        if (type == typeof(ITaskItem))
        {
            return ConvertItem(value);
        }

        if (!IsSupportedScalarType(type))
        {
            throw new NotSupportedException(HostResources.Format("UnsupportedParameterType", type));
        }

        string text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw new JsonException(),
        };

        if (type == typeof(AbsolutePath) || type == typeof(FileInfo) || type == typeof(DirectoryInfo))
        {
            text = Path.GetFullPath(text);
        }

        return ValueTypeParser.Parse(text, type);
    }

    private static bool IsSupportedScalarType(Type type)
        => type == typeof(ITaskItem) || ValueTypeParser.IsSupportedType(type) || type.IsEnum;

    private static ITaskItem ConvertItem(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return new TaskItem(EscapingUtilities.Escape(value.GetString()!), treatAsFilePath: false);
        }

        ItemValue item = value.Deserialize<ItemValue>(TaskInvocation.JsonOptions) ?? throw new JsonException();
        TaskItem result = new(EscapingUtilities.Escape(item.ItemSpec), treatAsFilePath: false);
        HashSet<string> metadataNames = new(MSBuildNameIgnoreCaseComparer.Default);
        foreach (KeyValuePair<string, string> metadata in item.Metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata.Value);
            if (!metadataNames.Add(metadata.Key))
            {
                throw new InvalidDataException(HostResources.Format("DuplicateParameter", metadata.Key));
            }

            ((ITaskItem2)result).SetMetadataValueLiteral(metadata.Key, metadata.Value);
        }

        return result;
    }

    private sealed class ItemValue
    {
        public required string ItemSpec { get; init; }

        public Dictionary<string, string> Metadata { get; init; } = [];
    }
}
