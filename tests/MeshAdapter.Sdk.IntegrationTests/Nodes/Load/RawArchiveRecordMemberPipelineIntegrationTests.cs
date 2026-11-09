using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Npgsql;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     AB#8666: a pipeline <c>CreateUpdateInfo@1</c> → <c>SaveStreamDataInArchive@1</c> writes record members into a
///     raw archive, run through the real orchestrator and nodes against the real <c>CrateDbStreamDataRepository</c>
///     and CrateDB (the chain tests in <c>MeshAdapter.Sdk.Tests</c> hand the points to a fake repository). The archive
///     has columns on the sub-paths of the record <c>SensorReading.Amount</c> (<c>Amount.Value</c>, <c>Amount.Unit</c>).
///     The row is seeded in the shape of the asset repository's REST <c>insert</c> (dotted keys); then the pipeline
///     writes a member value, an explicit null and leaves a member out. Before the fix every pipeline write left the
///     REST values in the row: the record arrived as one <c>Amount</c> key, which is no column.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class RawArchiveRecordMemberPipelineIntegrationTests(StreamDataPipelineFixture fixture)
    : IClassFixture<StreamDataPipelineFixture>
{
    private static readonly DateTime Timestamp = new(2026, 8, 1, 6, 0, 0, DateTimeKind.Utc);

    private const string PipelineYaml = """
        transformations:
          - type: CreateUpdateInfo@1
            updateKind: Update
            rtIdPath: $.rtId
            ckTypeId: MeshAdapterIntegrationTest/SensorReading
            timestampPath: $.timestamp
            targetPath: $.updateInfos
            targetValueKind: Array
            targetValueWriteMode: Append
            attributeUpdates:
              - attributeName: Temperature
                attributeValueType: Double
                valuePath: $.temperature
              - attributeName: Amount.Value
                attributeValueType: Double
                valuePath: $.amountValue
              - attributeName: Amount.Unit
                attributeValueType: String
                valuePath: $.amountUnit
          - type: SaveStreamDataInArchive@1
            path: $.updateInfos
            archiveRtId: "{archiveRtId}"
            writeResultPath: $.writeResult
        """;

    [Fact]
    public async Task APipelineWrite_WritesRecordMemberValuesAndNulls_AndKeepsAMemberItLeavesOut()
    {
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateArchiveAsync();
        var meter = OctoObjectId.GenerateNewId();

        // The REST shape: dotted column paths, no record value.
        await SeedRestShapeAsync(archiveRtId, meter);
        (await ReadRowAsync(archiveRtId, meter)).Should().Be((1.0, 10.0, "kW"));

        // A member value and an explicit null for the other member.
        var first = await RunAsync(archiveRtId, new JsonObject
        {
            ["rtId"] = meter.ToString(), ["timestamp"] = Timestamp.ToString("O"),
            ["temperature"] = 2.0, ["amountValue"] = 20.0, ["amountUnit"] = null
        });
        first["writeResult"]!["stored"]!.GetValue<int>().Should().Be(1);
        (await ReadRowAsync(archiveRtId, meter)).Should().Be((2.0, 20.0, null));

        // A member value; the other member is left out of the document.
        await RunAsync(archiveRtId, new JsonObject
        {
            ["rtId"] = meter.ToString(), ["timestamp"] = Timestamp.ToString("O"), ["amountUnit"] = "MW"
        });
        (await ReadRowAsync(archiveRtId, meter)).Should().Be((2.0, 20.0, "MW"));

        // Only a top-level attribute: every record member keeps its stored value.
        await RunAsync(archiveRtId, new JsonObject
        {
            ["rtId"] = meter.ToString(), ["timestamp"] = Timestamp.ToString("O"), ["temperature"] = 3.0
        });
        (await ReadRowAsync(archiveRtId, meter)).Should().Be((3.0, 20.0, "MW"));
    }

    private async Task<OctoObjectId> CreateAndActivateArchiveAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var archive = new RtRawArchive
        {
            RtWellKnownName = $"RecordMemberArchive_{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "Temperature", Indexed = false, Required = false },
                new() { Path = "Amount.Value", Indexed = false, Required = false },
                new() { Path = "Amount.Unit", Indexed = false, Required = false }
            }
        };

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var lifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private async Task SeedRestShapeAsync(OctoObjectId archiveRtId, OctoObjectId meter)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repository = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException("StreamDataRepository not available.");
        var result = await repository.InsertAsync(archiveRtId, new StreamDataPoint
        {
            RtId = meter,
            CkTypeId = new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            Timestamp = Timestamp,
            Attributes = new Dictionary<string, object?>
            {
                ["Temperature"] = 1.0,
                ["Amount.Value"] = 10.0,
                ["Amount.Unit"] = "kW"
            }
        });
        result.Stored.Should().Be(1);
        await fixture.RefreshArchiveAsync(archiveRtId);
    }

    private async Task<JsonObject> RunAsync(OctoObjectId archiveRtId, JsonObject input)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var pipelineId = OctoObjectId.GenerateNewId();
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, pipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", pipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(PipelineYaml.Replace("{archiveRtId}", archiveRtId.ToString()));
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: input);
        await fixture.RefreshArchiveAsync(archiveRtId);
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }

    private async Task<(double? Temperature, double? AmountValue, string? AmountUnit)> ReadRowAsync(
        OctoObjectId archiveRtId, OctoObjectId meter)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT temperature, amountvalue, amountunit FROM \"{fixture.StreamDataTenantId}\".\"archive_{archiveRtId}\" " +
            "WHERE rtid = @rtid", conn);
        cmd.Parameters.AddWithValue("rtid", meter.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the row exists");
        var row = (reader.IsDBNull(0) ? (double?)null : reader.GetDouble(0),
            reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        (await reader.ReadAsync()).Should().BeFalse("one row per rtId and timestamp");
        return row;
    }
}
