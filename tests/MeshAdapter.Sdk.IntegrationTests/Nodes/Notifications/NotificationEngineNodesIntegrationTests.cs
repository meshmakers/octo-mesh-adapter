using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
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
        await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");

        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        var state = await h.StateOfAsync(preference);
        state.Should().NotBeNull();
        state!.ProcessedUntil.Should().NotBeNull();
        state.SentRows.Should().BeNullOrEmpty();
        state.LastSentAt.Should().BeNull();
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
        await h.AddReadingsAsync(3);
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
        prepared.SentRows.Select(row => row.RtId).Should().Equal(added.Select(rtId => rtId.ToString()));
        prepared.StateRtId.Should().Be(stateRtId.ToString());
        prepared.SubjectId.Should().Be("user-a");
        prepared.QueryRtId.Should().Be(h.QueryRtId.ToString());
        leaseDuringSend.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        (await h.StateCountAsync(preference)).Should().Be(1);
    }

    [Fact]
    public async Task Commit_RecordsTheSentRows_AndReleasesTheLease()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var added = await h.AddReadingsAsync(1);

        var tick = await h.TickAndCommitAsync(preference);

        tick.Failure.Should().BeNull();
        var state = (await h.StateOfAsync(preference))!;
        state.SentRows!.Select(row => row.RowRtId).Should().Equal(added.Select(rtId => rtId.ToString()));
        state.ProcessedUntil.Should().NotBeNull();
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
    public async Task ACommitAfterAnotherRunTookOverTheLease_LeavesThatRunsStateAndDoesNotFail()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);
        var takeover = NotificationDeliveryStateStore.LeaseAt(h.Clock.UtcNow.AddMinutes(11));

        var tick = await h.TickAsync(preference, async (dc, _) =>
        {
            var stateRtId = (await h.StateOfAsync(preference))!.RtId;
            await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), stateRtId, preference,
                new NotificationDeliveryStateChange(RunningSince: takeover));
            await h.CommitAsync(dc);
        });

        tick.Failure.Should().BeNull();
        var state = (await h.StateOfAsync(preference))!;
        state.RunningSince.Should().Be(takeover);
        state.LastSentAt.Should().BeNull();
        state.SentRows.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ACommitAfterAnotherRunTookOverAndReleasedTheLease_LeavesThatRunsState()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);
        var othersSentAt = h.Clock.UtcNow.AddMinutes(11);

        var tick = await h.TickAsync(preference, async (dc, _) =>
        {
            var stateRtId = (await h.StateOfAsync(preference))!.RtId;
            await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), stateRtId, preference,
                new NotificationDeliveryStateChange(LastSentAt: othersSentAt, ReleaseLease: true));
            await h.CommitAsync(dc);
        });

        tick.Failure.Should().BeNull();
        var state = (await h.StateOfAsync(preference))!;
        state.RunningSince.Should().BeNull();
        state.LastSentAt.Should().BeCloseTo(othersSentAt, TimeSpan.FromMilliseconds(1));
        state.SentRows.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ARunThatReadTheStateBeforeAnotherRunSentAndCommitted_SkipsTheTick_AndReleasesItsLease()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var added = await h.AddReadingsAsync(1);
        var interleaved = false;
        TickResult? other = null;
        A.CallTo(() => h.SubjectLookup.FindBySubjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily(async (string _, string subjectId, CancellationToken _) =>
            {
                if (!interleaved)
                {
                    interleaved = true;
                    other = await h.TickAndCommitAsync(preference);
                }

                return (SubjectChannelRecord?)new SubjectChannelRecord(subjectId, $"{subjectId}@example.com",
                    subjectId, null, null, []);
            });

        var tick = await h.TickAsync(preference);

        other!.Failure.Should().BeNull();
        other.Sent.Should().BeTrue();
        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
        var state = (await h.StateOfAsync(preference))!;
        state.RunningSince.Should().BeNull();
        state.SentRows!.Select(row => row.RowRtId).Should().Equal(added.Select(rtId => rtId.ToString()));
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
        first.Prepared.SentRows.Should().HaveCount(2);
        second.Failure.Should().BeNull();
        second.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingFromOnwardsToCatchUp_SendsTheFullSummaryOnce()
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
        tick.Prepared!.NewCount.Should().Be(4);
        foreach (var index in new[] { 1, 2, 3, 4 })
        {
            tick.Prepared.Body.Should().Contain(h.SerialOf(index));
        }

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
        state.SentRows.Should().BeNullOrEmpty("what user-a got is forgotten, and an Onwards start records no rows");
    }

    [Fact]
    public async Task SwitchingFromCatchUpToOnwardsWithoutTheStudio_IsANewStartThatSkipsWhatWasNotSent()
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
        state.SentRows!.Select(row => row.RowRtId).Should().NotContain(unsent.Single().ToString());
        state.SentRows.Should().HaveCount(2);

        h.Advance(TimeSpan.FromMinutes(1));
        (await h.TickAsync(preference)).Sent.Should().BeFalse();
    }

    [Fact]
    public async Task AStateWrittenBeforeItRecordedTheRecipient_KeepsItsRows()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var rows = await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await NotificationDeliveryStateStore.SaveAsync(h.CreateEtlContext(), null, preference,
            new NotificationDeliveryStateChange(ProcessedUntil: h.Clock.UtcNow,
                SentRows: rows.Select(rtId => new SentRow(rtId.ToString(), h.Clock.UtcNow, h.Clock.UtcNow)).ToList(),
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
        refreshed.SentRows!.Select(row => row.RowRtId).Should().BeEquivalentTo(recorded.SentRows!.Select(row => row.RowRtId));
    }

    [Fact]
    public async Task AFailedSend_RecordsTheError_ReleasesTheLease_AndKeepsTheRowsUnsent()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        await h.AddReadingsAsync(1);

        var tick = await h.TickAsync(preference, (_, _) => throw new InvalidOperationException("smtp is down"));

        tick.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("smtp is down");
        var state = (await h.StateOfAsync(preference))!;
        state.LastError.Should().Be("smtp is down");
        state.RunningSince.Should().BeNull();
        state.SentRows.Should().BeNullOrEmpty("the row that failed is not recorded as sent");
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
        state.SentRows.Should().BeNullOrEmpty();
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
        h.Advance(TimeSpan.FromMinutes(1));
        (await h.TickAsync(preference)).Sent.Should().BeFalse();
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
        prepared.SentRows.Should().HaveCount(105);
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
        state.SentRows.Should().HaveCount(2);
        state.LastSentAt.Should().BeCloseTo(h.Clock.UtcNow, TimeSpan.FromMilliseconds(1));
        state.LastError.Should().Be("commit failed");
        state.RunningSince.Should().BeNull();

        h.Advance(TimeSpan.FromMinutes(16));
        var next = await h.TickAsync(preference);
        next.Failure.Should().BeNull();
        next.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task MoreNewRowsThanOneRunReads_AreSentOverConsecutiveRuns_AndNeverTwice()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(NotificationDigest.RowsPerRun + 5);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var first = await h.TickAndCommitAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var second = await h.TickAndCommitAsync(preference);
        h.Advance(TimeSpan.FromMinutes(1));
        var third = await h.TickAsync(preference);

        first.Failure.Should().BeNull();
        first.Prepared!.NewCount.Should().Be(NotificationDigest.RowsPerRun);
        second.Failure.Should().BeNull();
        second.Prepared!.NewCount.Should().Be(5);
        third.Failure.Should().BeNull();
        third.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task ARowChangedAfterItWasSent_IsNotSentAgainWithinThirtyDays()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var rows = await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);
        await h.TickAndCommitAsync(preference);

        await h.UpdateAsync<RtEntity>(NotificationEngineFixture.SensorReadingCkTypeId, rows[0],
            entity => entity.SetAttributeRawValue("SourceDate", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeFalse();
    }

    [Fact]
    public async Task ARowThatExistedBeforeAnOnwardsStart_IsSentWhenItChangesAfterIt()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var existing = await h.AddReadingsAsync(2);
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);

        await h.UpdateAsync<RtEntity>(NotificationEngineFixture.SensorReadingCkTypeId, existing[0],
            entity => entity.SetAttributeRawValue("SourceDate", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAndCommitAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        tick.Prepared!.SentRows.Select(row => row.RtId).Should().Equal(existing[0].ToString());
        h.Advance(TimeSpan.FromMinutes(1));
        (await h.TickAsync(preference)).Sent.Should().BeFalse();
    }

    [Fact]
    public async Task ARowThatStartsToMatchLater_IsNew()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        var preference = await h.CreatePreferenceAsync("user-a");
        await h.TickAsync(preference);
        var outsider = await h.AddForeignReadingAsync();

        h.Advance(TimeSpan.FromMinutes(1));
        await h.TickAsync(preference);
        await h.UpdateAsync<RtEntity>(NotificationEngineFixture.SensorReadingCkTypeId, outsider,
            entity => entity.SetAttributeRawValue("SerialNumber", $"{h.SerialOf(1)[..^5]}-9999"));
        h.Advance(TimeSpan.FromMinutes(1));
        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        tick.Sent.Should().BeTrue();
        tick.Prepared!.NewCount.Should().Be(1);
    }

    [Fact]
    public async Task ASortedQuery_ListsTheNewRowsInItsOrder()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.SortQueryAsync("SerialNumber", RtSortOrdersEnum.Descending);
        await h.AddReadingsAsync(3);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Failure.Should().BeNull();
        var body = tick.Prepared!.Body;
        body.IndexOf(h.SerialOf(3), StringComparison.Ordinal).Should()
            .BeLessThan(body.IndexOf(h.SerialOf(1), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AQueryFilteringOnAnAssociatedEntity_IsRefusedWithAReason()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.FilterQueryAsync("Parent.Process->Success");
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        tick.Failure!.Message.Should().Contain("filters on an associated entity");
        (await h.StateOfAsync(preference))!.LastError.Should().Contain("filters on an associated entity");
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
