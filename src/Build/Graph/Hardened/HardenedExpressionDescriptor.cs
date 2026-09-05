// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal sealed class HardenedExpressionDescriptorCache
{
    private ConditionalWeakTable<object, NodeDescriptors> _descriptors = new();

    internal void Clear()
    {
        _descriptors = new ConditionalWeakTable<object, NodeDescriptors>();
    }

    internal HardenedExpressionDescriptor GetOrCreate(
        object owner,
        string? expression,
        string? implicitItemType = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!_descriptors.TryGetValue(owner, out NodeDescriptors? nodeDescriptors))
        {
            nodeDescriptors = new NodeDescriptors();
            _descriptors.Add(owner, nodeDescriptors);
        }

        return nodeDescriptors.GetOrCreate(expression ?? string.Empty, implicitItemType);
    }

    private sealed class NodeDescriptors
    {
        private readonly Dictionary<ExpressionKey, HardenedExpressionDescriptor> _descriptors = [];

        internal HardenedExpressionDescriptor GetOrCreate(string expression, string? implicitItemType)
        {
            var key = new ExpressionKey(expression, implicitItemType);
            if (!_descriptors.TryGetValue(key, out HardenedExpressionDescriptor? descriptor))
            {
                descriptor = HardenedExpressionDescriptor.Create(expression, implicitItemType);
                _descriptors.Add(key, descriptor);
            }

            return descriptor;
        }
    }

    private readonly record struct ExpressionKey(string Expression, string? ImplicitItemType);
}

internal sealed class HardenedExpressionDescriptor
{
    private HardenedExpressionDescriptor(
        string expression,
        string? implicitItemType,
        ImmutableArray<string> properties,
        ImmutableArray<string> itemTypes,
        IReadOnlyDictionary<string, MetadataReference>? batchMetadata,
        ImmutableArray<HardenedItemVectorDescriptor> itemVectors)
    {
        Expression = expression;
        ImplicitItemType = implicitItemType;
        Properties = properties;
        ItemTypes = itemTypes;
        BatchMetadata = batchMetadata;
        ItemVectors = itemVectors;
    }

    internal string Expression { get; }

    internal string? ImplicitItemType { get; }

    internal ImmutableArray<string> Properties { get; }

    internal ImmutableArray<string> ItemTypes { get; }

    internal IReadOnlyDictionary<string, MetadataReference>? BatchMetadata { get; }

    internal ImmutableArray<HardenedItemVectorDescriptor> ItemVectors { get; }

    internal static HardenedExpressionDescriptor Create(string expression, string? implicitItemType)
    {
        ItemsAndMetadataPair references =
            ExpressionShredder.GetReferencedItemNamesAndMetadata([expression]);

        return new HardenedExpressionDescriptor(
            expression,
            implicitItemType,
            FindProperties(expression),
            references.Items is null ? [] : [.. references.Items],
            references.Metadata is null
                ? null
                : new ReadOnlyDictionary<string, MetadataReference>(references.Metadata),
            FindItemVectors(expression));
    }

    private static ImmutableArray<string> FindProperties(string expression)
    {
        var properties = ImmutableArray.CreateBuilder<string>();
        var propertyNames = new HashSet<string>(MSBuildNameIgnoreCaseComparer.Default);
        AddProperties(expression, 0, expression.Length, properties, propertyNames);
        return properties.DrainToImmutable();
    }

    private static void AddProperties(
        string expression,
        int startIndex,
        int endIndex,
        ImmutableArray<string>.Builder properties,
        HashSet<string> propertyNames)
    {
        int marker = ExpressionShredder.IndexOfPropertyMarker(
            expression,
            startIndex,
            endIndex - startIndex);
        while (marker >= 0 && marker < endIndex)
        {
            int bodyStart = marker + 2;
            int close = FindClosingParenthesis(expression, bodyStart);
            if (close < 0 || close >= endIndex)
            {
                return;
            }

            ReadOnlySpan<char> body = expression.AsSpan(bodyStart, close - bodyStart).Trim();
            if (!body.IsEmpty && body[0] != '[' && !body.StartsWith("registry:", StringComparison.OrdinalIgnoreCase))
            {
                int nameEnd = body.IndexOfAny('.', '[');
                ReadOnlySpan<char> propertyName = (nameEnd < 0 ? body : body[..nameEnd]).Trim();
                if (!propertyName.IsEmpty)
                {
                    string name = propertyName.ToString();
                    if (propertyNames.Add(name))
                    {
                        properties.Add(name);
                    }
                }
            }

            AddProperties(expression, bodyStart, close, properties, propertyNames);
            marker = ExpressionShredder.IndexOfPropertyMarker(
                expression,
                close + 1,
                endIndex - close - 1);
        }
    }

    private static ImmutableArray<HardenedItemVectorDescriptor> FindItemVectors(string expression)
    {
        var itemVectors = ImmutableArray.CreateBuilder<HardenedItemVectorDescriptor>();
        int startIndex = 0;
        while (ExpressionShredder.TryGetNextItemVectorExpression(
            expression,
            startIndex,
            out ExpressionShredder.ItemExpressionCapture itemVector))
        {
            ImmutableArray<HardenedItemTransformDescriptor> transforms;
            if (itemVector.Captures is null)
            {
                transforms = [];
            }
            else
            {
                var transformBuilder =
                    ImmutableArray.CreateBuilder<HardenedItemTransformDescriptor>(itemVector.Captures.Count);
                foreach (ExpressionShredder.ItemExpressionCapture transform in itemVector.Captures)
                {
                    ItemsAndMetadataPair transformReferences =
                        ExpressionShredder.GetReferencedItemNamesAndMetadata([transform.Value]);
                    ImmutableArray<MetadataReference> metadata = transformReferences.Metadata is null
                        ? []
                        : [.. transformReferences.Metadata.Values];

                    transformBuilder.Add(
                        new HardenedItemTransformDescriptor(
                            transform.Value,
                            transform.FunctionName,
                            transform.FunctionArguments,
                            metadata,
                            GetMetadataItemFunctionKind(transform.FunctionName)));
                }

                transforms = transformBuilder.DrainToImmutable();
            }

            itemVectors.Add(new HardenedItemVectorDescriptor(itemVector.ItemType, transforms));
            startIndex = itemVector.Index + itemVector.Length;
        }

        return itemVectors.DrainToImmutable();
    }

    private static HardenedMetadataItemFunctionKind? GetMetadataItemFunctionKind(string? functionName)
    {
        if (functionName is null)
        {
            return null;
        }

        if (functionName.Equals("Metadata", StringComparison.OrdinalIgnoreCase))
        {
            return HardenedMetadataItemFunctionKind.Metadata;
        }

        if (functionName.Equals("HasMetadata", StringComparison.OrdinalIgnoreCase))
        {
            return HardenedMetadataItemFunctionKind.HasMetadata;
        }

        if (functionName.Equals("WithMetadataValue", StringComparison.OrdinalIgnoreCase))
        {
            return HardenedMetadataItemFunctionKind.WithMetadataValue;
        }

        return functionName.Equals("AnyHaveMetadataValue", StringComparison.OrdinalIgnoreCase)
            ? HardenedMetadataItemFunctionKind.AnyHaveMetadataValue
            : null;
    }

    private static int FindClosingParenthesis(string expression, int start)
    {
        int depth = 1;
        for (int index = start; index < expression.Length; index++)
        {
            switch (expression[index])
            {
                case '\'' or '"' or '`':
                    int closingQuote = expression.IndexOf(expression[index], index + 1);
                    if (closingQuote < 0)
                    {
                        return -1;
                    }

                    index = closingQuote;
                    break;

                case '(':
                    depth++;
                    break;

                case ')':
                    if (--depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return -1;
    }
}

internal readonly record struct HardenedItemVectorDescriptor(
    string ItemType,
    ImmutableArray<HardenedItemTransformDescriptor> Transforms);

internal readonly record struct HardenedItemTransformDescriptor(
    string Expression,
    string? FunctionName,
    string? FunctionArguments,
    ImmutableArray<MetadataReference> Metadata,
    HardenedMetadataItemFunctionKind? MetadataItemFunctionKind);

internal enum HardenedMetadataItemFunctionKind
{
    Metadata,
    HasMetadata,
    WithMetadataValue,
    AnyHaveMetadataValue,
}
