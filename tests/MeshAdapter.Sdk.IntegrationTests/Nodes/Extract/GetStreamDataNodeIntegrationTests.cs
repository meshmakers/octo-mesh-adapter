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
