using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// Integration tests for <c>GetStreamData@1</c> reading end-to-end from the real CrateDB raw archive
/// seeded by <see cref="StreamDataFixture"/> (5 points at 15-minute intervals, well-known names
/// Sensor000..Sensor004).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetStreamDataNodeIntegrationTests(StreamDataFixture fixture)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task ProcessObjectAsync_ProjectsColumnsIncludingDottedPaths()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with { Columns = ["Temperature", "Amount.Value", "Amount.Unit"] };

        var result = await ExecuteAsync(config);

        result.Should().NotBeNull();
        result!.Columns.Select(c => c.Header)
            .Should().ContainInOrder("Timestamp", "Temperature", "Amount.Value", "Amount.Unit");
        result.Rows.Should().HaveCount(fixture.TestDataPointCount);

        foreach (var row in result.Rows)
        {
            row.Values[0].Should().NotBeNull("Timestamp must be populated");
            row.Values[1].Should().NotBeNull("Temperature must be populated");
            // Amount.Value only resolves through the physical column name (amountvalue).
            row.Values[2].Should().NotBeNull("Amount.Value must resolve via physical column name");
            row.Values[3].Should().NotBeNull("Amount.Unit must resolve via physical column name");
        }

        result.Rows.Select(r => r.Values[3]?.ToString()).Should().AllBe("kWh");
    }

    [Fact]
    public async Task ProcessObjectAsync_RawArchive_DoesNotEmitWindowColumns()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with { Columns = ["Temperature"] };

        var result = await ExecuteAsync(config);

        result!.Columns.Select(c => c.Header).Should().Equal("Timestamp", "Temperature");
    }

    [Fact]
    public async Task ProcessObjectAsync_FiltersByWellKnownName()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            WellKnownNames = ["Sensor002"]
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(1);
        // Seeded as 20.0 + i, so Sensor002 carries 22.
        Convert.ToDouble(result.Rows[0].Values[1]).Should().Be(22.0);
    }

    [Fact]
    public async Task ProcessObjectAsync_FiltersByMultipleWellKnownNames()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            WellKnownNames = ["Sensor000", "Sensor004"]
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesTimeRange()
    {
        fixture.EnsureInitialized();

        // Points sit at +0, +15, +30, +45, +60 minutes; this window covers the first three.
        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            From = fixture.TestDataStartTime,
            To = fixture.TestDataStartTime.AddMinutes(30)
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnspecifiedKindBoundaries_AreTreatedAsUtc()
    {
        fixture.EnsureInitialized();

        // Same window as above but with Kind=Unspecified, the shape pipeline JSON produces. Read as
        // UTC it selects the same three rows; shifted into the host's local zone it would not.
        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            From = DateTime.SpecifyKind(fixture.TestDataStartTime, DateTimeKind.Unspecified),
            To = DateTime.SpecifyKind(fixture.TestDataStartTime.AddMinutes(30), DateTimeKind.Unspecified)
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesLimit()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with { Columns = ["Temperature"], Limit = 2 };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesSkipAndTake()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            SortOrders = [Ascending("timestamp")],
            Skip = 1,
            Take = 2
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(2);
        // Skipping the first of the ascending series starts at 20.0 + 1.
        Convert.ToDouble(result.Rows[0].Values[1]).Should().Be(21.0);
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesSortOrder()
    {
        fixture.EnsureInitialized();

        var ascending = await ExecuteAsync(NewConfig() with
        {
            Columns = ["Temperature"],
            SortOrders = [Ascending("timestamp")]
        });

        var descending = await ExecuteAsync(NewConfig() with
        {
            Columns = ["Temperature"],
            SortOrders = [Descending("timestamp")]
        });

        var ascendingTemperatures = ascending!.Rows.Select(r => Convert.ToDouble(r.Values[1])).ToList();
        var descendingTemperatures = descending!.Rows.Select(r => Convert.ToDouble(r.Values[1])).ToList();

        ascendingTemperatures.Should().BeInAscendingOrder();
        descendingTemperatures.Should().BeInDescendingOrder();
        descendingTemperatures.Should().Equal(ascendingTemperatures.AsEnumerable().Reverse());
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByResultHeaderName_ActuallyOrdersTheRows()
    {
        fixture.EnsureInitialized();

        // "Timestamp" is the header the node emits. Before the column translation this was handed to
        // the storage layer verbatim, silently dropped, and the rows came back in storage order.
        var result = await ExecuteAsync(NewConfig() with
        {
            Columns = ["Temperature"],
            SortOrders = [new SortOrderDto
            {
                AttributeName = "Timestamp", SortOrder = SortOrdersDto.Descending
            }]
        });

        result!.Rows.Select(r => (DateTime)r.Values[0]!).Should().BeInDescendingOrder();
        result.Rows.Select(r => Convert.ToDouble(r.Values[1])).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task ProcessObjectAsync_SortByUnknownColumn_FailsInsteadOfReturningUnordered()
    {
        fixture.EnsureInitialized();

        var act = async () => await ExecuteAsync(NewConfig() with
        {
            Columns = ["Temperature"],
            SortOrders = [Ascending("Temparatur")]
        });

        (await act.Should().ThrowAsync<MeshAdapterPipelineExecutionException>())
            .WithMessage("*Temparatur*");
    }

    [Fact]
    public async Task ProcessObjectAsync_AppliesFieldFilter()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "Temperature",
                    Operator = FieldFilterOperatorDto.GreaterThan,
                    ComparisonValue = 22.0
                }
            ]
        };

        var result = await ExecuteAsync(config);

        // Seeded 20..24 — two points are strictly greater than 22.
        result!.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessObjectAsync_ScopesByRtIds()
    {
        fixture.EnsureInitialized();

        var all = await ExecuteAsync(NewConfig() with { Columns = ["Temperature"] });
        var targetRtId = all!.Rows[0].RtId!.Value;

        var config = NewConfig() with
        {
            Columns = ["Temperature"],
            RtIds = [targetRtId.ToString()]
        };

        var result = await ExecuteAsync(config);

        result!.Rows.Should().HaveCount(1);
        result.Rows[0].RtId.Should().Be(targetRtId);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoColumns_ReadsTheWholeArchive()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig());

        // The fixture's archive declares exactly these four columns.
        result!.Columns.Select(c => c.Header).Should().Equal(
            "Timestamp", "WellKnownName", "SerialNumber", "Temperature", "Amount.Value", "Amount.Unit");
        result.Rows.Should().HaveCount(fixture.TestDataPointCount);

        result.Rows.Should().AllSatisfy(row =>
            row.Values.Should().AllSatisfy(value =>
                value.Should().NotBeNull("reading the whole archive must populate every column")));
    }

    [Fact]
    public async Task ProcessObjectAsync_NoColumns_CarriesTheWellKnownName()
    {
        fixture.EnsureInitialized();

        var result = await ExecuteAsync(NewConfig() with
        {
            SortOrders = [Ascending("timestamp")]
        });

        // Seeded as Sensor000..Sensor004 in timestamp order.
        result!.Rows.Select(r => r.Values[1]?.ToString())
            .Should().Equal("Sensor000", "Sensor001", "Sensor002", "Sensor003", "Sensor004");
    }

    [Fact]
    public async Task ProcessObjectAsync_UnknownArchive_Throws()
    {
        fixture.EnsureInitialized();

        var config = NewConfig() with { ArchiveRtId = OctoObjectId.GenerateNewId() };

        var act = async () => await ExecuteAsync(config);

        await act.Should().ThrowAsync<MeshAdapterPipelineExecutionException>();
    }

    // ------------------------------------------------- computed columns (AB#4764)

    [Fact]
    public async Task Fixture_ComputedColumn_IsActuallyVersioned()
    {
        fixture.EnsureInitialized();

        // Guards the regression test below: if the fixture's formula change ever stopped producing a
        // versioned column, the test would still pass while no longer exercising AB#4764 at all.
        var systemContext = fixture.GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(fixture.TimeRangeArchiveRtId);

        var computed = snapshot!.Columns.Should()
            .ContainSingle(spec => spec.IsComputed).Subject;

        computed.Name.Should().Be(fixture.ComputedColumnName);
        computed.ComputedVersion.Should().BeGreaterThan(0,
            "the formula change must have moved the column into a versioned physical column");
        computed.ComputedState.Should().Be(ComputedColumnState.Active);
    }

    [Fact]
    public async Task ProcessObjectAsync_VersionedComputedColumn_ReturnsItsValues()
    {
        fixture.EnsureInitialized();

        // The regression test for AB#4764. The fixture's computed column has been through a formula
        // change, so it lives in power__v1 — a storage key no derivation from its name reproduces.
        // Before the fix every value here came back null, with no error at all.
        var result = await ExecuteAsync(TimeRangeConfig() with
        {
            Columns = ["Temperature", fixture.ComputedColumnName],
            WellKnownNames = [fixture.CompleteMeterWellKnownName],
            SortOrders = [Ascending("WindowStart")],
            GapsTargetPath = null
        });

        result!.Columns.Select(c => c.Header).Should().Equal(
            "Timestamp", "WindowStart", "WindowEnd", "Temperature", fixture.ComputedColumnName);

        result.Rows.Should().HaveCount(8);
        result.Rows.Should().AllSatisfy(row =>
            row.Values[4].Should().NotBeNull("the computed column must resolve to its versioned column"));

        // The active formula is temperature * 3, so each row's value follows its own temperature.
        foreach (var row in result.Rows)
        {
            var temperature = Convert.ToDouble(row.Values[3]);
            Convert.ToDouble(row.Values[4])
                .Should().Be(temperature * StreamDataFixture.ComputedColumnFactor);
        }
    }

    [Fact]
    public async Task ProcessObjectAsync_NoColumns_IncludesTheComputedColumn()
    {
        fixture.EnsureInitialized();

        // Reading a whole archive used to skip computed columns because their key was not derivable.
        var result = await ExecuteAsync(TimeRangeConfig() with { GapsTargetPath = null });

        result!.Columns.Select(c => c.Header).Should().Contain(fixture.ComputedColumnName);
        result.Rows.Should().AllSatisfy(row =>
            row.Values.Should().AllSatisfy(value => value.Should().NotBeNull()));
    }

    // ------------------------------------------------------------- gap detection

    [Fact]
    public async Task ProcessObjectAsync_GapDetection_FindsExactlyTheSeededGaps()
    {
        fixture.EnsureInitialized();

        var report = await ExecuteGapsAsync(TimeRangeConfig() with { GapsOnly = true });

        report.Should().NotBeNull();
        report!.SeriesCount.Should().Be(2);
        report.SeriesWithGapsCount.Should().Be(1);
        report.IsComplete.Should().BeFalse();

        var complete = report.Series.Single(s => s.WellKnownName == fixture.CompleteMeterWellKnownName);
        complete.IsComplete.Should().BeTrue();
        complete.Gaps.Should().BeEmpty();
        complete.PresentIntervals.Should().Be(8);

        // Slots 2+3 are one contiguous gap, slot 7 a second one at the trailing edge.
        var gappy = report.Series.Single(s => s.WellKnownName == fixture.GappyMeterWellKnownName);
        gappy.IsComplete.Should().BeFalse();
        gappy.MissingIntervals.Should().Be(3);
        gappy.PresentIntervals.Should().Be(5);
        gappy.Gaps.Should().HaveCount(2);

        gappy.Gaps[0].From.Should().Be(fixture.TimeRangeStart.AddMinutes(30));
        gappy.Gaps[0].To.Should().Be(fixture.TimeRangeStart.AddMinutes(60));
        gappy.Gaps[0].MissingIntervals.Should().Be(2);

        gappy.Gaps[1].From.Should().Be(fixture.TimeRangeStart.AddMinutes(105));
        gappy.Gaps[1].To.Should().Be(fixture.TimeRangeEnd);
        gappy.Gaps[1].MissingIntervals.Should().Be(1);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapDetection_UsesThePeriodDeclaredOnTheArchive()
    {
        fixture.EnsureInitialized();

        // No ExpectedInterval configured — the archive's own period has to supply the counts.
        var report = await ExecuteGapsAsync(TimeRangeConfig() with { GapsOnly = true });

        report!.Interval.Should().Be("PT15M");
        report.Series.Should().AllSatisfy(s => s.ExpectedIntervals.Should().Be(8));
    }

    [Fact]
    public async Task ProcessObjectAsync_GapDetection_HonoursTheWellKnownNameFilter()
    {
        fixture.EnsureInitialized();

        var report = await ExecuteGapsAsync(TimeRangeConfig() with
        {
            GapsOnly = true,
            WellKnownNames = [fixture.GappyMeterWellKnownName]
        });

        // The same filter narrows the coverage scan, not just the data query.
        report!.SeriesCount.Should().Be(1);
        report.Series[0].WellKnownName.Should().Be(fixture.GappyMeterWellKnownName);
        report.Series[0].Gaps.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapDetection_AlongsideTheData_WritesBoth()
    {
        fixture.EnsureInitialized();

        var config = TimeRangeConfig() with { Columns = ["Temperature"] };
        var captured = await ExecuteBothAsync(config);

        captured.Data.Should().NotBeNull();
        captured.Data!.Columns.Select(c => c.Header)
            .Should().Equal("Timestamp", "WindowStart", "WindowEnd", "Temperature");
        // 8 complete + 5 gappy windows.
        captured.Data.Rows.Should().HaveCount(13);

        captured.Gaps.Should().NotBeNull();
        captured.Gaps!.SeriesWithGapsCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessObjectAsync_GapDetection_OnRawArchive_Throws()
    {
        fixture.EnsureInitialized();

        var act = async () => await ExecuteGapsAsync(NewConfig() with
        {
            From = fixture.TestDataStartTime,
            To = fixture.TestDataStartTime.AddHours(2),
            GapsTargetPath = "$.gaps",
            GapsOnly = true
        });

        (await act.Should().ThrowAsync<MeshAdapterPipelineExecutionException>())
            .WithMessage("*raw archive*");
    }

    // ------------------------------------------------------------------ helpers

    private GetStreamDataNodeConfiguration NewConfig() => new()
    {
        ArchiveRtId = fixture.ArchiveRtId,
        TargetPath = "$.streamData"
    };

    private static SortOrderDto Ascending(string attributeName)
        => new() { AttributeName = attributeName, SortOrder = SortOrdersDto.Ascending };

    private static SortOrderDto Descending(string attributeName)
        => new() { AttributeName = attributeName, SortOrder = SortOrdersDto.Descending };

    private GetStreamDataNodeConfiguration TimeRangeConfig() => new()
    {
        ArchiveRtId = fixture.TimeRangeArchiveRtId,
        TargetPath = "$.streamData",
        GapsTargetPath = "$.gaps",
        From = fixture.TimeRangeStart,
        To = fixture.TimeRangeEnd
    };

    private async Task<StreamDataGapReport?> ExecuteGapsAsync(GetStreamDataNodeConfiguration config)
        => (await ExecuteBothAsync(config)).Gaps;

    private async Task<(QueryResult? Data, StreamDataGapReport? Gaps)> ExecuteBothAsync(
        GetStreamDataNodeConfiguration config)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        QueryResult? data = null;
        StreamDataGapReport? gaps = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                switch (call.Arguments[1])
                {
                    case QueryResult qr: data = qr; break;
                    case StreamDataGapReport report: gaps = report; break;
                }
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetStreamData", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        var node = new GetStreamDataNode(Next, meshEtlContext, systemContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        return (data, gaps);
    }

    private async Task<QueryResult?> ExecuteAsync(GetStreamDataNodeConfiguration config)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        QueryResult? capturedResult = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is QueryResult qr)
                {
                    capturedResult = qr;
                }
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetStreamData", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        var node = new GetStreamDataNode(Next, meshEtlContext, systemContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        return capturedResult;
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
