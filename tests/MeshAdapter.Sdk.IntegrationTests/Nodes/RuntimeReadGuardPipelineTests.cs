using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Logging;
using F = MeshAdapter.Sdk.IntegrationTests.Fixtures.RuntimeReadGuardPipelineFixture;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes;

/// <summary>
///     AB#9824, acceptance criterion 6: every pipeline node that may read runtime entities without a bound logs exactly
///     one warning (event 41001) per execution and node and counts <c>octo.engine.runtime_read.unbounded</c> once, with
///     the real orchestrator, the real nodes and MongoDB; a read with take within the limit logs and counts nothing;
///     the node's result is the same with the guard on (Warn) and off (Off), and Off logs and counts nothing.
/// </summary>
/// <remarks>
///     Detection sits in the engine's runtime reads (engine-mongodb <c>RuntimeReadGuard</c>), so these tests prove the
///     wiring per node: the orchestrator's execution and node scope reach the read, and the node is named. Nodes whose
///     CK types are configurable are pointed at the test model; the read happens whatever the node does afterwards.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class RuntimeReadGuardPipelineTests(F fixture) : IClassFixture<F>
{
    private const int UnboundedReadEvent = 41001;
    private const string MetricName = "octo.engine.runtime_read.unbounded";

    private static readonly OctoObjectId PipelineId = new("000000000000000000009824");

    // ------------------------------------------------------------------------------------------- per node

    [Fact]
    public async Task GetRtEntitiesByType_WithoutTake_WarnsOnce_AndCountsOnce() =>
        await AssertReportedOnceAsync("GetRtEntitiesByType@1", $"""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: {F.ControlType}
                targetPath: $.controls
            """, output => output.Should().Contain("control-1"));

    [Fact]
    public async Task GetRtEntitiesByType_WithTakeWithinTheLimit_LogsAndCountsNothing()
    {
        await AssertSilentAsync("GetRtEntitiesByType@1", $"""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: {F.ControlType}
                take: 2
                targetPath: $.controls
            """);
    }

    [Fact]
    public async Task GetQueryById_SimpleQueryWithoutTake_WarnsOnce_AndCountsOnce()
    {
        var queryRtId = await fixture.CreateControlQueryAsync();
        await AssertReportedOnceAsync("GetQueryById@1", $"""
            transformations:
              - type: GetQueryById@1
                queryRtId: '{queryRtId}'
                targetPath: $.result
            """, output => output.Should().Contain("control-1"));
    }

    [Fact]
    public async Task GetQueryById_AggregationQuery_WarnsOnce_AndCountsOnce()
    {
        var queryRtId = await fixture.CreateControlAggregationQueryAsync();
        await AssertReportedOnceAsync("GetQueryById@1", $"""
            transformations:
              - type: GetQueryById@1
                queryRtId: '{queryRtId}'
                targetPath: $.result
            """);
    }

    [Fact]
    public async Task GetQueryById_WithTakeWithinTheLimit_LogsAndCountsNothing()
    {
        var queryRtId = await fixture.CreateControlQueryAsync();
        await AssertSilentAsync("GetQueryById@1", $"""
            transformations:
              - type: GetQueryById@1
                queryRtId: '{queryRtId}'
                take: 10
                targetPath: $.result
            """);
    }

    [Fact]
    public async Task GetAssociationTargets_WarnsOnce_AndCountsOnce() =>
        await AssertReportedOnceAsync("GetAssociationTargets@1", $"""
            transformations:
              - type: GetAssociationTargets@1
                originRtId: '{fixture.ProbeRoleRtId}'
                originCkTypeId: System.Identity/Role
                targetCkTypeId: System.Identity/User
                associationRoleId: System.Identity/AssignedRole
                graphDirection: Inbound
                targetPath: $.users
            """, output => output.Should().Contain(IdentityCredentialPipelineFixture.ProbeUserName));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SimulateEnergyMeasurements_ReadsTheWholeType_WarnsOnce_AndCountsOnce(int version) =>
        await AssertReportedOnceAsync($"SimulateEnergyMeasurements@{version}", $"""
            transformations:
              - type: SimulateEnergyMeasurements@{version}
                startDate: '2026-01-01T00:00:00Z'
                numDays: 1
                energyMeasurementCkTypeId: {F.ReadingType}
                timeRangeCkRecordId: Basic/TimeRange
                amountCkRecordId: MeshAdapterIntegrationTest/Measurement
                parentAssociationRoleId: System/ParentChild
                meteringPointCkTypeId: {F.ControlType}
                producerCkTypeId: {F.ControlType}
                entityUpdatesOutputPath: $.updates
            """);

    // BuildMappingTargets@1 reads the fixed type System.Communication/DataPointMapping, whose model lives in the
    // communication controller and is not available here; it makes the same unbounded type read as the other
    // mapping nodes and is verified on the lane stack (a tenant with System.Communication).

    [Fact]
    public async Task ExportDataPointMappings_WarnsOnce_AndCountsOnce() =>
        await AssertReportedOnceAsync("ExportDataPointMappings@1", $"""
            transformations:
              - type: ExportDataPointMappings@1
                mappingCkTypeId: {F.ContactType}
                targetPath: $.export
            """);

    [Fact]
    public async Task ImportDataPointMappings_TwoTypeReadsInOneNode_WarnOnce_AndCountOnce()
    {
        var input = new JsonObject
        {
            ["doc"] = new JsonObject
            {
                ["version"] = 1,
                ["mappingCkTypeId"] = "System.Communication/DataPointMapping",
                ["mappings"] = new JsonArray(new JsonObject
                {
                    ["name"] = "m1",
                    ["enabled"] = true,
                    ["sourceAttributePath"] = "ControlName",
                    ["targetAttributePath"] = "ContactName",
                    ["mappingExpression"] = "",
                    ["source"] = new JsonObject { ["ckTypeId"] = F.ControlType, ["rtId"] = OctoObjectId.GenerateNewId().ToString() },
                    ["target"] = new JsonObject { ["ckTypeId"] = F.ContactType, ["rtId"] = OctoObjectId.GenerateNewId().ToString() }
                })
            }
        };

        // Two unbounded reads (source and target type) in one node: one warning for the node, as the criterion says;
        // the counter counts the node's first unbounded read (one series, one increment).
        await AssertReportedOnceAsync("ImportDataPointMappings@1", """
            transformations:
              - type: ImportDataPointMappings@1
                path: $.doc
                targetPath: $.imported
            """, input: input);
    }

    [Fact]
    public async Task GenerateDataPointMappings_WarnsOnce_AndCountsOnce() =>
        await AssertReportedOnceAsync("GenerateDataPointMappings@1", $"""
            transformations:
              - type: GenerateDataPointMappings@1
                sourceContainerCkTypeId: {F.ReadingType}
                sourceControlCkTypeId: {F.ControlType}
                targetCkTypeId: {F.ContactType}
                targetPath: $.suggestions
            """);

    [Fact]
    public async Task RegisterSelfReportedMeteringPoint_LookupByNumberWithIntMaxValue_WarnsOnce_AndCountsOnce() =>
        // The producer type is pointed at TestControl, whose seeded entity carries the number: the consumer request
        // reads the producers by number (take int.MaxValue) and is refused with a direction conflict - the same
        // result in both modes.
        await AssertReportedOnceAsync("RegisterSelfReportedMeteringPoint@1", $"""
            transformations:
              - type: RegisterSelfReportedMeteringPoint@1
                requestPath: $.body
                targetPath: $.result
                producerCkTypeId: {F.ControlType}
                consumerCkTypeId: {F.ContactType}
                meteringPointNumberAttribute: ControlName
            """, output => output.Should().Contain("other direction"),
            new JsonObject
            {
                ["body"] = new JsonObject
                {
                    ["meteringPointNumber"] = F.MeteringPointNumber,
                    ["direction"] = "consumption"
                }
            });

    // ------------------------------------------------------------------------------- outside a pipeline

    /// <summary>
    ///     The engine hook without pipeline context: a read of the host itself is reported with caller <c>unknown</c>,
    ///     at most once per tenant and CK type and hour - and the lease invariant: when the pooled adapter releases
    ///     the tenant (<c>CkModelCacheLeaseParticipant.LeaveLeaseAsync</c> calls
    ///     <c>InvalidateTenantResolveImportGuards</c>), that throttle of the tenant is dropped, so nothing of it
    ///     outlives the lease.
    /// </summary>
    [Fact]
    public async Task ReadWithoutPipelineContext_IsReportedAsUnknownOnce_AndTheLeaseReleaseForgetsTheThrottle()
    {
        fixture.GuardOptions.Mode = "Warn";
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());
        var type = new RtCkId<CkTypeId>(F.ContactType);
        var start = GuardWarningCount();

        async Task ReadAsync()
        {
            using var session = await repository.GetSessionAsync();
            await repository.GetRtEntitiesByTypeAsync(session, type, RtEntityQueryOptions.Create());
        }

        await ReadAsync();
        await ReadAsync();
        var afterTwo = GuardWarnings(start);
        afterTwo.Should().ContainSingle();
        afterTwo[0].State["NodeType"].Should().Be("unknown");
        afterTwo[0].State["TenantId"].Should().Be(repository.TenantId);

        fixture.GetSystemContext().InvalidateTenantResolveImportGuards(repository.TenantId);
        await ReadAsync();

        GuardWarnings(start).Should().HaveCount(2, "the release forgot the tenant's throttle");
    }

    // -------------------------------------------------------------------------------------------- helpers

    private async Task AssertReportedOnceAsync(string nodeType, string yaml, Action<string>? checkOutput = null,
        JsonObject? input = null)
    {
        // Off: nothing logged, nothing counted.
        fixture.GuardOptions.Mode = "Off";
        var offStart = GuardWarningCount();
        var offCount = CountFor(nodeType);
        var offOutput = await RunAsync(yaml, input);
        GuardWarnings(offStart).Should().BeEmpty("mode Off logs nothing");
        CountFor(nodeType).Should().Be(offCount, "mode Off counts nothing");

        // Warn, two executions: one warning and one count per execution and node.
        fixture.GuardOptions.Mode = "Warn";
        var warnStart = GuardWarningCount();
        var warnCount = CountFor(nodeType);
        var warnOutput = await RunAsync(yaml, input);
        var secondOutput = await RunAsync(yaml, input);

        var warnings = GuardWarnings(warnStart);
        warnings.Should().HaveCount(2, "one warning per execution and node");
        warnings.Should().AllSatisfy(w =>
        {
            w.Level.Should().Be(LogLevel.Warning);
            w.State["NodeType"].Should().Be(nodeType);
            w.State["PipelineId"].Should().Be(PipelineId.ToString());
            w.State["TenantId"].Should().Be(fixture.GetSystemContext().GetSystemTenantRepository().TenantId);
        });
        warnings.Select(w => w.State["ExecutionId"]).Distinct().Should().HaveCount(2);
        (CountFor(nodeType) - warnCount).Should().Be(2, "one count per execution and node");

        // No behaviour change: the same result with the guard on and off.
        warnOutput.Should().Be(offOutput);
        secondOutput.Should().Be(offOutput);
        checkOutput?.Invoke(warnOutput);
    }

    private async Task AssertSilentAsync(string nodeType, string yaml)
    {
        fixture.GuardOptions.Mode = "Warn";
        var start = GuardWarningCount();
        var count = CountFor(nodeType);

        await RunAsync(yaml, null);

        GuardWarnings(start).Should().BeEmpty("a read with take within the limit is bounded");
        CountFor(nodeType).Should().Be(count);
    }

    private int GuardWarningCount()
    {
        lock (fixture.GuardLog)
        {
            return fixture.GuardLog.Count;
        }
    }

    private List<F.GuardLogEntry> GuardWarnings(int start)
    {
        lock (fixture.GuardLog)
        {
            return fixture.GuardLog.Skip(start).Where(e => e.EventId.Id == UnboundedReadEvent).ToList();
        }
    }

    /// <summary>
    ///     The counter value for the node type in this host's meter (all CK types and modes summed). The first
    ///     collection of a new series is 0 by design, so the second one is read.
    /// </summary>
    private long CountFor(string nodeType)
    {
        var meterFactory = fixture.MeterFactory;
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == MetricName && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "node_type" && Equals(tag.Value, nodeType))
                {
                    total += value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        total = 0;
        listener.RecordObservableInstruments();
        return total;
    }

    private async Task<string> RunAsync(string yaml, JsonObject? input)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, PipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", PipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        var result = await fixture.GetService<IEtlDataOrchestrator>()
            .ExecutePipelineAsync(definition, etlContext, value: (JsonObject?)input?.DeepClone() ?? new JsonObject());
        var node = result as JsonNode ?? JsonSerializer.SerializeToNode(result);
        return node?.ToJsonString() ?? string.Empty;
    }
}
