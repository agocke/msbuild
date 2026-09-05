// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using Microsoft.Build.Graph.Hardened;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests.Graph.Hardened;

public sealed class HardenedExpressionDescriptor_Tests
{
    [Fact]
    public void CachesDescriptorsByOwnerExpressionAndImplicitItemType()
    {
        var cache = new HardenedExpressionDescriptorCache();
        var owner = new object();

        HardenedExpressionDescriptor descriptor =
            cache.GetOrCreate(owner, "@(Input)", "Input");

        cache.GetOrCreate(owner, "@(Input)", "Input").ShouldBeSameAs(descriptor);
        cache.GetOrCreate(owner, "@(Input)").ShouldNotBeSameAs(descriptor);
        cache.GetOrCreate(new object(), "@(Input)", "Input").ShouldNotBeSameAs(descriptor);
    }

    [Fact]
    public void CapturesExpressionDependenciesAndTransforms()
    {
        HardenedExpressionDescriptor descriptor = HardenedExpressionDescriptor.Create(
            """
            $(Configuration);
            $([System.String]::Copy('$(Nested)'));
            @(Input->'%(Payload)');
            %(Input.Kind);
            %(Loose);
            @(Input->WithMetadataValue('Flavor', 'sweet'))
            """,
            implicitItemType: "Input");

        descriptor.ImplicitItemType.ShouldBe("Input");
        descriptor.Properties.ShouldBe(["Configuration", "Nested"]);
        descriptor.ItemTypes.ShouldNotBeEmpty();
        descriptor.ItemTypes.ShouldContain("Input");
        descriptor.BatchMetadata.ShouldNotBeNull();
        descriptor.BatchMetadata.Count.ShouldBe(2);
        descriptor.BatchMetadata["Input.Kind"].ItemName.ShouldBe("Input");
        descriptor.BatchMetadata["Input.Kind"].MetadataName.ShouldBe("Kind");
        descriptor.BatchMetadata["Loose"].ItemName.ShouldBeNull();
        descriptor.BatchMetadata["Loose"].MetadataName.ShouldBe("Loose");

        descriptor.ItemVectors.Length.ShouldBe(2);
        HardenedItemTransformDescriptor quotedTransform =
            descriptor.ItemVectors[0].Transforms.Single();
        quotedTransform.Metadata.Single().MetadataName.ShouldBe("Payload");

        HardenedItemTransformDescriptor functionTransform =
            descriptor.ItemVectors[1].Transforms.Single();
        functionTransform.FunctionName.ShouldBe("WithMetadataValue");
        functionTransform.FunctionArguments.ShouldBe("'Flavor', 'sweet'");
        functionTransform.MetadataItemFunctionKind.ShouldBe(
            HardenedMetadataItemFunctionKind.WithMetadataValue);
    }

    [Theory]
    [InlineData("Metadata", (int)HardenedMetadataItemFunctionKind.Metadata)]
    [InlineData("HasMetadata", (int)HardenedMetadataItemFunctionKind.HasMetadata)]
    [InlineData("WithMetadataValue", (int)HardenedMetadataItemFunctionKind.WithMetadataValue)]
    [InlineData("AnyHaveMetadataValue", (int)HardenedMetadataItemFunctionKind.AnyHaveMetadataValue)]
    public void ClassifiesMetadataSensitiveItemFunctions(
        string functionName,
        int expectedKind)
    {
        HardenedExpressionDescriptor descriptor = HardenedExpressionDescriptor.Create(
            $"@(Input->{functionName}('Flavor', 'sweet'))",
            implicitItemType: null);

        descriptor.ItemVectors.Single().Transforms.Single().MetadataItemFunctionKind.ShouldBe(
            (HardenedMetadataItemFunctionKind)expectedKind);
    }
}
