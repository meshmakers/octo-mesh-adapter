using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;

/// <summary>
///     The incremental read of a notification query against a real MongoDB: only rows changed after the
///     watermark, never a row already sent, oldest change first, one page at a time.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class NotificationQueryWindowIntegrationTests(NotificationEngineFixture fixture)
    : IClassFixture<NotificationEngineFixture>
{
    [Fact]
    public async Task OnlyRowsChangedAfterTheWatermarkAreRead()
    {
        var h = NewScenario();
        await h.AddReadingsAsync(2);
        var mark = await PauseAsync();
        var fresh = await h.AddReadingsAsync(1);

        var candidates = await FindAsync(h, mark, [], 100);

        candidates.Select(candidate => candidate.RtId).Should().Equal(fresh);
        candidates.Single().ChangedAt.Should().BeAfter(mark);
    }

    [Fact]
    public async Task AnUpdateAfterTheWatermark_BringsAnOlderRowBack()
    {
        var h = NewScenario();
        var old = await h.AddReadingsAsync(2);
        var mark = await PauseAsync();

        await h.UpdateAsync<RtEntity>(NotificationEngineFixture.SensorReadingCkTypeId, old[1],
            entity => entity.SetAttributeRawValue("SourceDate", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));

        (await FindAsync(h, mark, [], 100)).Select(candidate => candidate.RtId).Should().Equal(old[1]);
    }

    [Fact]
    public async Task WithoutAWatermark_EverythingMatchingIsRead()
    {
        var h = NewScenario();
        var all = await h.AddReadingsAsync(3);

        (await FindAsync(h, null, [], 100)).Select(candidate => candidate.RtId).Should().BeEquivalentTo(all);
    }

    [Fact]
    public async Task RowsAlreadySentAreNeverRead_SoAPageAlwaysMovesOn()
    {
        var h = NewScenario();
        var all = await h.AddReadingsAsync(4);

        var candidates = await FindAsync(h, null, [all[0], all[1]], 1);

        candidates.Select(candidate => candidate.RtId).Should().Equal(all[2]);
    }

    [Fact]
    public async Task ThePageHoldsTheOldestChangesFirst()
    {
        var h = NewScenario();
        var first = await h.AddReadingsAsync(1);
        await PauseAsync();
        var second = await h.AddReadingsAsync(1);
        await PauseAsync();
        await h.UpdateAsync<RtEntity>(NotificationEngineFixture.SensorReadingCkTypeId, first[0],
            entity => entity.SetAttributeRawValue("SourceDate", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));

        (await FindAsync(h, null, [], 2)).Select(candidate => candidate.RtId).Should().Equal(second[0], first[0]);
    }

    private NotificationEngineHarness NewScenario()
    {
        fixture.EnsureInitialized();
        return new NotificationEngineHarness(fixture);
    }

    private static async Task<DateTime> PauseAsync()
    {
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var mark = DateTime.UtcNow;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        return mark;
    }

    private async Task<IReadOnlyList<NotificationCandidate>> FindAsync(NotificationEngineHarness h, DateTime? changedAfter,
        IReadOnlyCollection<OctoObjectId> sent, int take)
    {
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = NotificationEngineFixture.SensorReadingCkTypeId.ToString(),
            FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new()
                {
                    AttributePath = "SerialNumber",
                    Operator = RtFieldFilterOperatorEnum.MatchRegEx,
                    ComparisonValue = $"^{h.SerialOf(1)[..^5]}-"
                }
            }
        };

        using var session = await fixture.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var candidates = await SimpleRtQueryExecution.FindChangedAsync(fixture.TenantRepository,
            fixture.GetService<ICkCacheService>(), session, query, changedAfter, sent, take);
        await session.CommitTransactionAsync();
        return candidates;
    }
}
