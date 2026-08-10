using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// Integration tests for <c>AggregateStreamData@1</c> against the real CrateDB time-range archive
/// seeded by <see cref="StreamDataFixture" />: eight quarter-hour windows, temperatures 20..27, one
/// complete meter and one missing slots 2, 3 and 7.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class AggregateStreamDataNodeIntegrationTests(StreamDataFixture fixture)
    : IClassFixture<StreamDataFixture>
{
    /// <summary>Slot n carries temperature 20 + n; the complete meter delivers all eight.</summary>
    private static double ExpectedCompleteSum => Enumerable.Range(0, 8).Sum(slot => 20.0 + slot);

    /// <summary>The gappy meter is missing slots 2, 3 and 7.</summary>
    private static double ExpectedGappySum => Enumerable.Range(0, 8)
        .Where(slot => !StreamDataFixture.MissingSlots.Contains(slot))
        .Sum(slot => 20.0 + slot);

    [Fact]
    public async Task ProcessObjectAsync_SumMatchesTheSeededValues()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(Agg("Temperature", AggregationTypesDto.Sum)) with
        {
            WellKnownNames = [fixture.CompleteMeterWellKnownName]
        });

        result!.Columns.Select(c => c.Header).Should().Equal("Temperature");
        var row = result.Rows.Should().ContainSingle().Subject;
        Convert.ToDouble(row.Values[0]).Should().Be(ExpectedCompleteSum);
    }

    [Fact]
    public async Task ProcessObjectAsync_MinMaxAndCount_AreComputedOverTheRange()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(
            Agg("Temperature", AggregationTypesDto.Minimum),
            Agg("Temperature", AggregationTypesDto.Maximum),
            Agg("Temperature", AggregationTypesDto.Count)) with
        {
            WellKnownNames = [fixture.CompleteMeterWellKnownName]
        });

        // The same path three times, so the headers carry the function.
        result!.Columns.Select(c => c.Header).Should().Equal(
            "Temperature (Minimum)", "Temperature (Maximum)", "Temperature (Count)");

        var row = result.Rows.Should().ContainSingle().Subject;
        Convert.ToDouble(row.Values[0]).Should().Be(20.0);
        Convert.ToDouble(row.Values[1]).Should().Be(27.0);
        Convert.ToInt32(row.Values[2]).Should().Be(8);
    }

    [Fact]
    public async Task ProcessObjectAsync_GroupByRtId_ReturnsOneRowPerMeter()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(Agg("Temperature", AggregationTypesDto.Sum)) with
        {
            GroupBy = ["rtId"]
        });

        result!.Columns.Select(c => c.Header).Should().Equal("rtId", "Temperature");
        result.Rows.Should().HaveCount(2);

        // Each meter's own sum, not a merged one.
        result.Rows.Select(r => Convert.ToDouble(r.Values[1]))
            .Should().BeEquivalentTo(new[] { ExpectedCompleteSum, ExpectedGappySum });
    }

    [Fact]
    public async Task ProcessObjectAsync_WithoutGrouping_AggregatesAcrossBothMeters()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(Agg("Temperature", AggregationTypesDto.Sum)));

        var row = result!.Rows.Should().ContainSingle().Subject;
        Convert.ToDouble(row.Values[0]).Should().Be(ExpectedCompleteSum + ExpectedGappySum);
    }

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFree_PassesForTheCompleteMeter()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(Agg("Temperature", AggregationTypesDto.Sum)) with
        {
            WellKnownNames = [fixture.CompleteMeterWellKnownName],
            RequireGapFree = true
        });

        Convert.ToDouble(result!.Rows[0].Values[0]).Should().Be(ExpectedCompleteSum);
    }

    [Fact]
    public async Task ProcessObjectAsync_RequireGapFree_FailsForTheGappyMeter()
    {
        fixture.EnsureInitialized();

        // Without the guard this returns ExpectedGappySum — a figure that looks valid but is short by
        // three quarter-hours. That is exactly what the guard exists to prevent.
        var act = async () => await ExecuteAsync(
            NewConfig(Agg("Temperature", AggregationTypesDto.Sum)) with
            {
                WellKnownNames = [fixture.GappyMeterWellKnownName],
                RequireGapFree = true
            });

        (await act.Should().ThrowAsync<MeshAdapterPipelineExecutionException>())
            .WithMessage($"*{fixture.GappyMeterWellKnownName}*");
    }

    [Fact]
    public async Task ProcessObjectAsync_WithoutGuard_TheGappyMeterStillAggregates()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig(Agg("Temperature", AggregationTypesDto.Sum)) with
        {
            WellKnownNames = [fixture.GappyMeterWellKnownName]
        });

        Convert.ToDouble(result!.Rows[0].Values[0]).Should().Be(ExpectedGappySum);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnsupportedFunction_Throws()
    {
        fixture.EnsureInitialized();

        var act = async () => await ExecuteAsync(
            NewConfig(Agg("Temperature", AggregationTypesDto.TimeWeightedAverage)));

        (await act.Should().ThrowAsync<MeshAdapterPipelineExecutionException>())
            .WithMessage("*GetQueryById*");
    }

    // ------------------------------------------------------------------ helpers

    private static AggregationColumnDto Agg(string path, AggregationTypesDto function)
        => new() { AttributePath = path, Function = function };

    private AggregateStreamDataNodeConfiguration NewConfig(params AggregationColumnDto[] aggregations)
        => new()
        {
            ArchiveRtId = fixture.TimeRangeArchiveRtId,
            TargetPath = "$.aggregated",
            Aggregations = aggregations,
            From = fixture.TimeRangeStart,
            To = fixture.TimeRangeEnd
        };

    private async Task<QueryResult?> ExecuteAsync(AggregateStreamDataNodeConfiguration config)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        QueryResult? captured = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is QueryResult qr) captured = qr;
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("AggregateStreamData", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        var node = new AggregateStreamDataNode(Next, meshEtlContext, systemContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        return captured;
    }

    private static MeshEtlContext CreateMeshEtlContext(ITenantRepository tenantRepository)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>());
    }
}
