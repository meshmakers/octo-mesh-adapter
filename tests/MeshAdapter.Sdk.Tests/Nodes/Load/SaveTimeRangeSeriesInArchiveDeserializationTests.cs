using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// Drives the node's configuration through the real pipeline deserializer.
/// </summary>
/// <remarks>
/// A C# object initializer reaches every property of a config, so a unit test can build one that a
/// pipeline definition can never produce. This node shipped with <c>Columns</c> typed
/// <c>IReadOnlyList&lt;T&gt;</c>: it compiled, 19 shaper tests were green, and the adapter then
/// refused the pipeline at registration with "No node deserializer was able to deserialize the node
/// into type IReadOnlyList`1[...]" — YamlDotNet has no deserializer for that interface. The failure
/// surfaced only on a live deploy, and only in the pipeline entity's status message, because the
/// adapter logs the outcome as <c>success=false</c> without the reason. Anything a pipeline author
/// writes has to be proven through this path.
/// </remarks>
public class SaveTimeRangeSeriesInArchiveDeserializationTests : NodeTestBase
{
    private static async Task<SaveTimeRangeSeriesInArchiveNodeConfiguration> DeserializeAsync(string yaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(SaveTimeRangeSeriesInArchiveNode));
        var serializer = services.BuildServiceProvider()
            .GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + yaml);
        return root.Transformations!.OfType<SaveTimeRangeSeriesInArchiveNodeConfiguration>().Single();
    }

    /// <summary>The block as the EnergyCommunity.EdaIntegration pipeline actually carries it.</summary>
    private const string RealPipelineBlock = """
          - type: SaveTimeRangeSeriesInArchive@1
            description: Persist measurement anchors and all slots
            path: $.consumptionRecords
            archiveRtId: ec0000000000000000000a01
            ckTypeId: Basic.Energy/EnergyMeasurement
            valuesProperty: EnergyQuantities
            wellKnownNameFormat: "{MeteringPointRtId}_{MeterCode}"
            fromProperty: From
            toProperty: To
            anchorWindowFromAttribute: TimeRange.From
            anchorWindowToAttribute: TimeRange.To
            parentRtIdProperty: MeteringPointRtId
            parentCkTypeId: Basic.Energy/MeteringPoint
            parentAssociationRoleId: System/ParentChild
            columns:
              - name: Amount.Value
                valueProperty: Quantity
              - name: Amount.Unit
                valueProperty: QuantityUnit
                scope: Series
              - name: DataQuality
                valueProperty: Quality
              - name: ObisCode
                valueProperty: MeterCode
                scope: Series
              - name: SourceDocumentDate
                valueProperty: CreationTime
                scope: Series
        """;

    [Fact]
    public async Task TheShippedPipelineBlockDeserializes()
    {
        var c = await DeserializeAsync(RealPipelineBlock);

        Assert.Equal("ec0000000000000000000a01", c.ArchiveRtId);
        Assert.Equal("EnergyQuantities", c.ValuesProperty);
        Assert.Equal("{MeteringPointRtId}_{MeterCode}", c.WellKnownNameFormat);
        Assert.Equal("TimeRange.To", c.AnchorWindowToAttribute);
        Assert.Equal("System/ParentChild", c.ParentAssociationRoleId);
    }

    [Fact]
    public async Task TheColumnListSurvivesTheYamlRoundTrip()
    {
        // The property this test exists for. A collection type the deserializer cannot construct
        // fails the whole pipeline, not just the column.
        var c = await DeserializeAsync(RealPipelineBlock);

        Assert.Equal(5, c.Columns.Count);
        Assert.Equal(
            ["Amount.Value", "Amount.Unit", "DataQuality", "ObisCode", "SourceDocumentDate"],
            c.Columns.Select(x => x.Name).ToArray());
        Assert.Equal(
            ["Quantity", "QuantityUnit", "Quality", "MeterCode", "CreationTime"],
            c.Columns.Select(x => x.ValueProperty).ToArray());
    }

    [Fact]
    public async Task ColumnScopeDeserializesByNameAndDefaultsToValue()
    {
        // Node-config enums travel by NAME in a definition; an omitted scope must fall back to the
        // individual value, which is what the bulk of the columns rely on.
        var c = await DeserializeAsync(RealPipelineBlock);
        var byName = c.Columns.ToDictionary(x => x.Name, x => x.Scope);

        Assert.Equal(TimeRangeSeriesColumnScope.Value, byName["Amount.Value"]);
        Assert.Equal(TimeRangeSeriesColumnScope.Value, byName["DataQuality"]);
        Assert.Equal(TimeRangeSeriesColumnScope.Series, byName["Amount.Unit"]);
        Assert.Equal(TimeRangeSeriesColumnScope.Series, byName["ObisCode"]);
        Assert.Equal(TimeRangeSeriesColumnScope.Series, byName["SourceDocumentDate"]);
    }

    [Fact]
    public async Task TheOptionalBlocksMayBeOmittedEntirely()
    {
        // The minimal configuration: no anchor window, no parent association. Both are optional, and
        // a definition that omits them must not fail to deserialize.
        var c = await DeserializeAsync("""
              - type: SaveTimeRangeSeriesInArchive@1
                path: $.series
                archiveRtId: ec0000000000000000000a01
                ckTypeId: Basic.Energy/EnergyMeasurement
                valuesProperty: values
                wellKnownNameFormat: "{key}"
                fromProperty: from
                toProperty: to
                columns:
                  - name: Amount.Value
                    valueProperty: quantity
            """);

        Assert.Null(c.AnchorWindowFromAttribute);
        Assert.Null(c.AnchorWindowToAttribute);
        Assert.Null(c.ParentRtIdProperty);
        Assert.Single(c.Columns);
    }
}
