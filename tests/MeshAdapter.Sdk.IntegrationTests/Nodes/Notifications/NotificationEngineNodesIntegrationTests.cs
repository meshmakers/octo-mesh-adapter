using System.Text.Json;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;

/// <summary>
///     <c>PrepareNotification@1</c> and <c>CommitNotificationState@1</c> against a real MongoDB: the
///     association directions, the partial updates of the delivery state and the lease are what a test
///     with a faked repository cannot see.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class NotificationEngineNodesIntegrationTests(NotificationEngineFixture fixture)
    : IClassFixture<NotificationEngineFixture>
{
    private NotificationEngineHarness NewScenario()
    {
        fixture.EnsureInitialized();
        return new NotificationEngineHarness(fixture);
    }

    [Fact]
    public async Task OnwardsFirstRun_RecordsTheExistingRowsSilentlyInAStateLinkedToThePreference()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");

        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        var state = await h.StateOfAsync(preference);
        state.Should().NotBeNull();
        state!.SkippedRtIds.Should().BeEquivalentTo(existing.Select(rtId => rtId.ToString()));
        state.SeenRtIds.Should().BeNullOrEmpty();
        state.QueryRtId.Should().Be(h.QueryRtId.ToString());
        state.LastRunAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        state.RunningSince.Should().BeNull();
        state.LastError.Should().BeNull();
    }

    [Fact]
    public async Task OnwardsAfterTheStart_SendsOnlyTheNewRow_RenderedInTheRecipientsTimeZone_UnderTheLease()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        var stateRtId = (await h.StateOfAsync(preference))!.RtId;

        h.Advance(TimeSpan.FromMinutes(1));
        var added = await h.AddReadingsAsync(1, new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        DateTime? leaseDuringSend = null;
        var tick = await h.TickAsync(preference,
            async (_, _) => leaseDuringSend = (await h.StateOfAsync(preference))!.RunningSince);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        var prepared = tick.Prepared!;
        prepared.NewCount.Should().Be(1);
        prepared.Subject.Should().Be("1 new readings");
        prepared.Body.Should().Be($"{h.SerialOf(4)} at 2026-07-01 12:00");
        prepared.SeenRtIds.Should().Equal(added.Single().ToString());
        prepared.SkippedRtIds.Should().BeEquivalentTo(existing.Select(rtId => rtId.ToString()));
        prepared.StateRtId.Should().Be(stateRtId.ToString());
        prepared.SubjectId.Should().Be("user-a");
        prepared.QueryRtId.Should().Be(h.QueryRtId.ToString());
        leaseDuringSend.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        (await h.StateCountAsync(preference)).Should().Be(1);
    }

    [Fact]
    public async Task Commit_RecordsTheSentRows_KeepsTheHeldBackOnes_AndReleasesTheLease()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var added = await h.AddReadingsAsync(1);

        var tick = await h.TickAndCommitAsync(preference);

        tick.Failure.Should().BeNull();
        var state = (await h.StateOfAsync(preference))!;
        state.SeenRtIds.Should().Equal(added.Single().ToString());
        state.SkippedRtIds.Should().BeEquivalentTo(existing.Select(rtId => rtId.ToString()));
        state.LastSentAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        state.LastRunAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        state.RunningSince.Should().BeNull();
        state.LastError.Should().BeNull();

        h.Advance(TimeSpan.FromMinutes(1));
        var again = await h.TickAsync(preference);
        again.Failure.Should().BeNull();
        again.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task CatchUpFirstRun_SendsEveryExistingRowOnce()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var first = await h.TickAndCommitAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var second = await h.TickAsync(preference);

        first.Sent.Should().BeTrue();
        first.Prepared!.NewCount.Should().Be(2);
        first.Prepared.SkippedRtIds.Should().BeEmpty();
        second.Failure.Should().BeNull();
        second.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingFromOnwardsToCatchUp_MailsExactlyTheHeldBackRows_NeverTheSentOne()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);
        await h.TickAndCommitAsync(preference);

        await h.UpdateAsync<RtNotificationPreference>(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId,
            preference, p =>
            {
                p.SubjectId = "user-a";
                p.CronExpression = "0 7 * * *";
                p.TimeZone = "Europe/Vienna";
                p.SkipExistingRows = false;
            });
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        tick.Prepared!.NewCount.Should().Be(3);
        tick.Prepared.SkippedRtIds.Should().BeEmpty();
        tick.Prepared.SeenRtIds.Should().HaveCount(4);
        foreach (var index in new[] { 1, 2, 3 })
        {
            tick.Prepared.Body.Should().Contain(h.SerialOf(index));
        }

        tick.Prepared.Body.Should().NotContain(h.SerialOf(4));
        existing.Should().HaveCount(3);
    }

    [Fact]
    public async Task ChangingTheUserWithoutTheStudio_StartsTheNewUserFromScratch()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAndCommitAsync(preference);

        h.KnowSubject("user-b");
        await UpdatePreferenceAsync(h, preference, "user-b", skipExistingRows: false);
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        tick.Prepared!.SubjectId.Should().Be("user-b");
        tick.Prepared.NewCount.Should().Be(2);
    }

    [Fact]
    public async Task ANewUserWhoGotNoMailYet_DoesNotShowTheLastMailOfThePreviousUser()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAndCommitAsync(preference);

        h.KnowSubject("user-b");
        await UpdatePreferenceAsync(h, preference, "user-b", skipExistingRows: true);
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        var state = (await h.StateOfAsync(preference))!;
        state.SubjectId.Should().Be("user-b");
        state.LastSentAt.Should().BeNull();
        state.SeenRtIds.Should().BeEmpty();
        state.SkippedRtIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task SwitchingFromCatchUpToOnwardsWithoutTheStudio_IsANewStartThatHoldsBackWhatWasNotSent()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAndCommitAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var unsent = await h.AddReadingsAsync(1);

        await UpdatePreferenceAsync(h, preference, "user-a", skipExistingRows: true);
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        var state = (await h.StateOfAsync(preference))!;
        state.SkippedRtIds.Should().Equal(unsent.Single().ToString());
        state.SeenRtIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task AStateWrittenBeforeItRecordedTheRecipient_KeepsItsRows()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var rows = await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), null, preference,
            new NotificationDeliveryStateChange(SeenRtIds: rows.Select(rtId => rtId.ToString()).ToList(),
                QueryRtId: h.QueryRtId.ToString(), LastRunAt: h.Clock.UtcNow));

        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
    }

    private static Task UpdatePreferenceAsync(NotificationEngineHarness h, OctoObjectId preference, string subjectId,
        bool skipExistingRows) =>
        h.UpdateAsync<RtNotificationPreference>(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference,
            p =>
            {
                p.SubjectId = subjectId;
                p.CronExpression = "0 7 * * *";
                p.TimeZone = "Europe/Vienna";
                p.SkipExistingRows = skipExistingRows;
            });

    [Fact]
    public async Task NothingNew_WritesNoStateUntilAnHourHasPassed()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        var recorded = (await h.StateOfAsync(preference))!;

        h.Advance(TimeSpan.FromMinutes(1));
        await h.TickAsync(preference);
        var idle = (await h.StateOfAsync(preference))!;

        h.Advance(TimeSpan.FromMinutes(61));
        await h.TickAsync(preference);
        var refreshed = (await h.StateOfAsync(preference))!;

        idle.RtVersion.Should().Be(recorded.RtVersion);
        idle.LastRunAt.Should().Be(recorded.LastRunAt);
        refreshed.LastRunAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        refreshed.SkippedRtIds.Should().BeEquivalentTo(recorded.SkippedRtIds);
    }

    [Fact]
    public async Task AFailedSend_RecordsTheError_ReleasesTheLease_AndKeepsTheRowsUnsent()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);

        var tick = await h.TickAsync(preference, (_, _) => throw new InvalidOperationException("smtp is down"));

        tick.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("smtp is down");
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Be("smtp is down");
        state.RunningSince.Should().BeNull();
        state.SeenRtIds.Should().BeNullOrEmpty();
        state.SkippedRtIds.Should().BeEquivalentTo(existing.Select(rtId => rtId.ToString()));
        state.LastSentAt.Should().BeNull();
    }

    [Fact]
    public async Task AFailedRun_BacksOffForAQuarterOfAnHour_ThenRetries()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAsync(preference, (_, _) => throw new InvalidOperationException("smtp is down"));

        h.Advance(TimeSpan.FromMinutes(1));
        var backingOff = await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(15));
        var retried = await h.TickAndCommitAsync(preference);

        backingOff.Failure.Should().BeNull();
        backingOff.Sent.Should().BeFalse();
        retried.Failure.Should().BeNull();
        retried.Sent.Should().BeTrue();
        (await h.StateOfAsync(preference))!.LastError.Should().BeNull();
    }

    [Fact]
    public async Task AConfigurationChangeAfterAFailure_RetriesOnTheNextTick()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAsync(preference, (_, _) => throw new InvalidOperationException("smtp is down"));

        await h.UpdateAsync<RtNotificationTemplate>(SystemNotificationCkIds.RtCkNotificationTemplateTypeId,
            h.TemplateRtId, t =>
            {
                t.SubjectTemplate = NotificationEngineHarness.SubjectTemplate;
                t.BodyTemplate = NotificationEngineHarness.BodyTemplate;
                t.Type = RtNotificationTypesEnum.EMail;
                t.RenderingType = RtRenderingTypesEnum.Plain;
            });
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
    }

    [Fact]
    public async Task ALeaseHeldByAnotherRun_SkipsTheTick_AnExpiredOneIsTakenOver()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        var stateRtId = (await h.StateOfAsync(preference))!.RtId;
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);

        await SetLeaseAsync(h, stateRtId, h.Clock.UtcNow.AddMinutes(-2));
        var held = await h.TickAsync(preference);
        await SetLeaseAsync(h, stateRtId, h.Clock.UtcNow.AddMinutes(-11));
        var expired = await h.TickAsync(preference);

        held.Failure.Should().BeNull();
        held.Sent.Should().BeFalse();
        expired.Failure.Should().BeNull();
        expired.Sent.Should().BeTrue();
    }

    [Fact]
    public async Task AnUndeployedSubscription_SendsNothingAndWritesNoState()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync(deployed: false);
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        (await h.StateCountAsync(preference)).Should().Be(0);
    }

    [Fact]
    public async Task ARemovedPreference_SendsNothing()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();

        var tick = await h.TickAsync(OctoObjectId.GenerateNewId());

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task AnInvalidPreferenceRtId_FailsNamingThePath()
    {
        var h = NewScenario();

        var tick = await h.TickAsync(OctoObjectId.GenerateNewId(), rawPreferenceRtId: "not-an-rtid");

        tick.Failure.Should().BeAssignableTo<PipelineExecutionException>()
            .Which.Message.Should().Contain("'not-an-rtid' at path '$.preferenceRtId'");
    }

    [Fact]
    public async Task ASubscriptionWhoseTemplateWasDeleted_FailsAndRecordsTheErrorOnANewState()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.DeleteTemplateAsync();
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Failure!.Message.Should().Contain("has no notification template");
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Contain("has no notification template");
        state.RunningSince.Should().BeNull();
        (await h.StateCountAsync(preference)).Should().Be(1);
    }

    [Fact]
    public async Task ARecipientWhoseUserWasDeleted_FailsReadablyWithoutTakingTheLease()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-gone", onwards: false);
        h.ForgetSubject("user-gone");

        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        tick.Failure!.Message.Should().Contain("no longer exists. Pick another user or delete the recipient.");
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Contain("no longer exists");
        state.RunningSince.Should().BeNull();
        state.SeenRtIds.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ASecondRecipientOfTheSameUser_SendsNothing_TheOldestOneSends()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(1);
        var oldest = await h.CreatePreferenceAsync("user-a", onwards: false);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var newer = await h.CreatePreferenceAsync("user-a", onwards: false);

        var duplicate = await h.TickAsync(newer);
        var original = await h.TickAsync(oldest);

        duplicate.Sent.Should().BeFalse();
        duplicate.Failure!.Message.Should().Contain("already gets subscription");
        original.Failure.Should().BeNull();
        original.Sent.Should().BeTrue();
    }

    [Fact]
    public async Task PointingTheSubscriptionAtAnotherQuery_IsANewStartForOnwards()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);

        var otherQuery = await h.CreateQueryAsync("other readings");
        await h.AddReadingsAsync(2);
        await h.RepointQueryAsync(otherQuery);
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        var state = (await h.StateOfAsync(preference))!;
        state.QueryRtId.Should().Be(otherQuery.ToString());
        state.SkippedRtIds.Should().HaveCount(5);
    }

    [Fact]
    public async Task ADryRun_RendersTheMailButWritesNothing()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference, dryRun: true);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        tick.Prepared!.NewCount.Should().Be(2);
        tick.Prepared.StateRtId.Should().BeNull();
        (await h.StateCountAsync(preference)).Should().Be(0);
    }

    [Fact]
    public async Task AnUnknownPlaceholder_FailsTheRunAndReleasesTheLeaseItTook()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync(bodyTemplate: "${SerialNumber} ${Nope}");
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        tick.Failure!.Message.Should().Contain("Nope");
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Contain("Nope");
        state.RunningSince.Should().BeNull();
    }

    [Fact]
    public async Task MoreThanAHundredNewRows_ListsTheFirstHundred_AndCountsThemAll()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(105);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        var prepared = tick.Prepared!;
        prepared.NewCount.Should().Be(105);
        prepared.Subject.Should().Be("105 new readings");
        prepared.SeenRtIds.Should().HaveCount(105);
        prepared.Body.Should().Contain("...and 5 more.");
        Enumerable.Range(1, 105).Count(index => prepared.Body.Contains(h.SerialOf(index))).Should().Be(100);
    }

    [Fact]
    public async Task AFailureAfterTheMailWentOut_RecordsTheRows_SoTheyAreNotMailedAgain()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference, (dc, _) =>
        {
            var prepared = dc.Get<PreparedNotification>("$.notification")!;
            dc.Set("$.notification", prepared with { Sent = true });
            throw new InvalidOperationException("commit failed");
        });

        tick.Failure!.Message.Should().Be("commit failed");
        var state = (await h.StateOfAsync(preference))!;
        state.SeenRtIds.Should().HaveCount(2);
        state.LastSentAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        state.LastError.Should().Be("commit failed");
        state.RunningSince.Should().BeNull();

        h.Advance(TimeSpan.FromMinutes(16));
        var next = await h.TickAsync(preference);
        next.Failure.Should().BeNull();
        next.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task CommitWithoutAPreparedNotification_FailsNamingThePath()
    {
        var h = NewScenario();
        using var document = JsonDocument.Parse("{}");
        using var dataContext = new DataContextImpl(document.RootElement);

        var act = () => h.CommitAsync(dataContext);

        await act.Should().ThrowAsync<PipelineExecutionException>()
            .WithMessage("*No prepared notification found at path '$.notification'*");
    }

    private static Task SetLeaseAsync(NotificationEngineHarness h, OctoObjectId stateRtId, DateTime runningSince) =>
        h.UpdateAsync<RtNotificationDeliveryState>(SystemNotificationCkIds.RtCkNotificationDeliveryStateTypeId,
            stateRtId, s => s.RunningSince = runningSince);
}
