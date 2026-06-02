using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Characterization: the typed MappingTargetRecord must serialize byte-identically to the
/// former hand-built JsonObject CK-RecordArray item, including the conditional "Name" key
/// (present when a stateName is supplied, omitted otherwise) and key order.
/// </summary>
public class BuildMappingTargetsNodeTests
{
    [Fact]
    public void CreateMappingTargetRecord_PlainIdentifier_OmitsNameKey()
    {
        var record = BuildMappingTargetsNode.CreateMappingTargetRecord("ext-1", stateName: null, "ext-1");
        var newJson = JsonSerializer.Serialize(record, SystemTextJsonOptions.Default);

        var legacy = LegacyRecord("ext-1", stateName: null, "ext-1");
        Assert.Equal(legacy.ToJsonString(SystemTextJsonOptions.Default), newJson);
        Assert.DoesNotContain("\"Name\"", newJson);
    }

    [Fact]
    public void CreateMappingTargetRecord_WithStateName_IncludesNameKeyLast()
    {
        var record = BuildMappingTargetsNode.CreateMappingTargetRecord("ext-1", "co2", "state-id-7");
        var newJson = JsonSerializer.Serialize(record, SystemTextJsonOptions.Default);

        var legacy = LegacyRecord("ext-1", "co2", "state-id-7");
        Assert.Equal(legacy.ToJsonString(SystemTextJsonOptions.Default), newJson);
    }

    private static JsonObject LegacyRecord(string sourceIdentifier, string? stateName, string externalId)
    {
        var attributes = new JsonObject
        {
            ["SourceIdentifier"] = sourceIdentifier,
            ["ExternalId"] = externalId
        };

        if (!string.IsNullOrWhiteSpace(stateName))
        {
            attributes["Name"] = stateName;
        }

        return new JsonObject
        {
            ["CkRecordId"] = new JsonObject
            {
                ["SemanticVersionedFullName"] = "System.Communication/MappingTarget"
            },
            ["Attributes"] = attributes
        };
    }
}
