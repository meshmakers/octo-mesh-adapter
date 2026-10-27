using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     AB#6933 compatibility: the one new optional property <c>ckInterfaceId</c> of <c>GetRtEntitiesByType@1</c> in the
///     pipeline JSON schema (generated from the real node registrations, exactly like the build target does) changes
///     nothing for pipelines that do not use it, and pipelines that do are valid.
/// </summary>
public class GetRtEntitiesByTypePipelineSchemaTests
{
    private static readonly Lazy<JsonObject> Generated = new(Generate);

    private static JsonObject Generate()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var json = services.BuildServiceProvider().GetRequiredService<IPipelineSchemaGenerator>().GenerateSchema();

        var nodeSchema = JsonNode.Parse(json)!["$defs"]!["TransformationNode"]!["oneOf"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(n => n["properties"]?["type"]?["const"]?.GetValue<string>() == "GetRtEntitiesByType@1");
        return nodeSchema;
    }

    [Fact]
    public void CkInterfaceId_IsOneNewOptionalStringProperty_NothingElseOfTheNodeChanged()
    {
        var node = Generated.Value;
        var properties = node["properties"]!.AsObject().Select(p => p.Key).ToHashSet();

        Assert.Contains("ckInterfaceId", properties);
        var interfaceType = node["properties"]!["ckInterfaceId"]!["type"]!;
        Assert.Contains("string", interfaceType.ToJsonString());
        // The properties the node had before AB#6933 are all still there.
        foreach (var existing in new[]
                 {
                     "type", "identity", "ckTypeId", "ckTypeIdPath", "skip", "take", "fieldFilters", "sortOrders",
                     "targetPath", "documentMode", "targetValueKind", "targetValueWriteMode"
                 })
        {
            Assert.Contains(existing, properties);
        }

        var required = node["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToArray() ?? [];
        Assert.DoesNotContain("ckInterfaceId", required);
        Assert.DoesNotContain("ckTypeId", required);
    }

    public static TheoryData<string> ExistingStylePipelines =>
    [
        // minimal type selection
        """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: Basic/Employee
            targetPath: $.employees
        """,
        // type selection from the data context, with paging, filters, sort orders and an identity
        """
        transformations:
          - type: GetRtEntitiesByType@1
            identity: ServiceAccount
            ckTypeIdPath: $.typeId
            skip: 10
            take: 20
            fieldFilters:
              - attributePath: Name
                operator: Equals
                comparisonValue: alpha
            sortOrders:
              - attributeName: Name
                sortOrder: Ascending
            targetPath: $.result
        """,
        // the same node inside a ForEach, as the energy-community pipelines use it
        """
        transformations:
          - type: ForEach@1
            path: $.items
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: EnergyCommunity/MeteringPoint
                targetPath: $.points
        """,
        // AB#6933: selection by CK interface
        """
        transformations:
          - type: GetRtEntitiesByType@1
            ckInterfaceId: System/Named
            skip: 0
            take: 50
            fieldFilters:
              - attributePath: Name
                operator: Equals
                comparisonValue: alpha
            sortOrders:
              - attributeName: Name
                sortOrder: Ascending
            targetPath: $.result
        """
    ];

    [Theory]
    [MemberData(nameof(ExistingStylePipelines))]
    public async Task Pipeline_IsAcceptedByThePipelineSerializer(string yaml)
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync(yaml);

        Assert.NotEmpty(root.Transformations!);
    }
}
