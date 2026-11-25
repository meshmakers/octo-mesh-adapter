using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     AB#9768: <c>targetTenant</c> is one new optional property of exactly the three read nodes. The strict pipeline
///     serializer accepts it there (with the relation as written) and refuses it on every other node, so an adapter can
///     never load a cross-tenant setting into a node that would silently read or write its own tenant.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class GetRtEntitiesTargetTenantConfigurationTests(PipelineSchemaFixture schema)
{
    [Theory]
    [InlineData("GetRtEntitiesByType@1")]
    [InlineData("GetRtEntitiesById@1")]
    [InlineData("GetRtEntitiesByWellKnownName@1")]
    public void Schema_TargetTenantIsOptional_OnTheReadNodes(string nodeType)
    {
        var node = schema.NodeSchema(nodeType);

        Assert.Contains("targetTenant", node["properties"]!.AsObject().Select(p => p.Key));
        var required = node["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToArray() ?? [];
        Assert.DoesNotContain("targetTenant", required);
    }

    [Theory]
    [InlineData("ApplyChanges@1")]
    [InlineData("ApplyChanges@2")]
    [InlineData("CreateUpdateInfo@1")]
    public void Schema_OtherNodesHaveNoTargetTenant(string nodeType)
    {
        var node = schema.NodeSchemaOrNull(nodeType);
        if (node == null)
        {
            return;
        }

        Assert.DoesNotContain("targetTenant", node["properties"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task Pipeline_WithAParentTarget_LoadsWithTheRelationAsWritten()
    {
        const string yaml = """
                            transformations:
                              - type: GetRtEntitiesByType@1
                                ckTypeId: Basic/Contact
                                identity: Caller
                                targetTenant:
                                  relation: Parent
                                targetPath: $.contacts
                              - type: GetRtEntitiesById@1
                                ckTypeId: Basic/Contact
                                rtIdsPath: $.ids
                                identity: ServiceAccount
                                targetTenant:
                                  relation: Sibling
                                  tenantIdPath: $.recipientTenantId
                                targetPath: $.contact
                              - type: GetRtEntitiesByWellKnownName@1
                                ckTypeId: Basic/Contact
                                path: $.items
                                wellKnownNamePath: $.name
                                targetTenant:
                                  relation: Child
                                  tenantId: companya
                            """;

        var root = await Serializer().DeserializeAsync(yaml);

        var nodes = root.Transformations!.ToList();
        Assert.Equal(TenantRelation.Parent, Assert.IsType<GetRtEntitiesByTypeNodeConfiguration>(nodes[0]).TargetTenant!.Relation);
        var byId = Assert.IsType<GetRtEntitiesByIdNodeConfiguration>(nodes[1]).TargetTenant!;
        Assert.Equal(TenantRelation.Sibling, byId.Relation);
        Assert.Equal("$.recipientTenantId", byId.TenantIdPath);
        var byName = Assert.IsType<GetRtEntitiesByWellKnownNameNodeConfiguration>(nodes[2]).TargetTenant!;
        Assert.Equal(TenantRelation.Child, byName.Relation);
        Assert.Equal("companya", byName.TenantId);
    }

    [Fact]
    public async Task Pipeline_WithoutTarget_LoadsUnchanged_TargetIsNull()
    {
        const string yaml = """
                            transformations:
                              - type: GetRtEntitiesByType@1
                                ckTypeId: Basic/Contact
                                targetPath: $.contacts
                            """;

        var root = await Serializer().DeserializeAsync(yaml);

        Assert.Null(Assert.IsType<GetRtEntitiesByTypeNodeConfiguration>(Assert.Single(root.Transformations!))
            .TargetTenant);
    }

    [Theory]
    [InlineData("Descendant")]
    [InlineData("Ancestor")]
    [InlineData("Any")]
    public async Task Pipeline_WithAnUnknownRelation_IsRefused(string relation)
    {
        var yaml = $"""
                    transformations:
                      - type: GetRtEntitiesByType@1
                        ckTypeId: Basic/Contact
                        targetTenant:
                          relation: {relation}
                          tenantId: x
                        targetPath: $.contacts
                    """;

        await Assert.ThrowsAsync<PipelineSerializationException>(() => Serializer().DeserializeAsync(yaml));
    }

    [Fact]
    public async Task Pipeline_TargetWithoutRelation_NeverDefaultsToParent_TheGuardRefusesIt()
    {
        // The serializer does not enforce `required`: a missing relation must stay null (not the enum's first value,
        // Parent), and the guard refuses it as a configuration error before any registry or identity call.
        const string yaml = """
                            transformations:
                              - type: GetRtEntitiesByType@1
                                ckTypeId: Basic/Contact
                                targetTenant:
                                  tenantId: x
                                targetPath: $.contacts
                            """;

        var root = await Serializer().DeserializeAsync(yaml);

        var target = Assert.IsType<GetRtEntitiesByTypeNodeConfiguration>(Assert.Single(root.Transformations!))
            .TargetTenant!;
        Assert.Null(target.Relation);
        Assert.NotNull(CrossTenantAccessGuard.CheckConfiguration(target));
    }

    [Fact]
    public async Task ApplyChangesV1_WithATargetTenant_IsRefusedWhenThePipelineLoads()
    {
        // Abuse case 9: ApplyChanges@1 gets no target setting, so the key is unknown there.
        const string yaml = """
                            transformations:
                              - type: ApplyChanges@1
                                path: $.updates
                                targetTenant:
                                  relation: Parent
                            """;

        var ex = await Assert.ThrowsAsync<PipelineSerializationException>(() => Serializer().DeserializeAsync(yaml));
        Assert.Contains("targetTenant", ex.Message, StringComparison.Ordinal);
    }

    private static IPipelineConfigurationSerializer Serializer()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        return services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
    }
}
