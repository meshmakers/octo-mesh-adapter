using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

public class NotificationDigestTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeStart = Start.AddDays(-1);
    private static readonly DateTime AfterStart = Start.AddHours(1);

    [Fact]
    public void StartOf_IsTheLaterOfCreationAndDeployment()
    {
        Assert.Equal(Start, NotificationDigest.StartOf(BeforeStart, Start));
        Assert.Equal(Start, NotificationDigest.StartOf(Start, BeforeStart));
        Assert.Equal(Start, NotificationDigest.StartOf(null, Start));
    }

    [Fact]
    public void Decide_OnwardsFirstRun_RecordsTheCurrentResultWithoutMailing()
    {
        var decision = NotificationDigest.Decide(["a", "b"], null, null, skipExistingRows: true, lastRunAt: null, Start);

        Assert.Equal(NotificationDigestAction.RecordSilently, decision.Action);
        Assert.Empty(decision.NewRtIds);
    }

    [Fact]
    public void Decide_CatchUpFirstRun_MailsEverythingOnce()
    {
        var decision = NotificationDigest.Decide(["a", "b"], null, null, skipExistingRows: false, lastRunAt: null, Start);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal(["a", "b"], decision.NewRtIds);
    }

    [Fact]
    public void Decide_LaterRun_MailsOnlyRowsNotSeenBefore()
    {
        var decision = NotificationDigest.Decide(["a", "b", "c"], ["a", "b"], null, skipExistingRows: true, AfterStart, Start);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal(["c"], decision.NewRtIds);
    }

    [Fact]
    public void Decide_RowThatFailsDaysAfterCreation_IsStillNew()
    {
        var decision = NotificationDigest.Decide(["old", "late"], ["old", "newer"], null, skipExistingRows: true, AfterStart, Start);

        Assert.Equal(["late"], decision.NewRtIds);
    }

    [Fact]
    public void Decide_NothingNew_SendsNothing()
    {
        var decision = NotificationDigest.Decide(["a"], ["a", "b"], null, skipExistingRows: false, AfterStart, Start);

        Assert.Equal(NotificationDigestAction.NothingNew, decision.Action);
    }

    [Fact]
    public void Decide_OnwardsAfterRedeploy_RecordsRowsFromThePauseSilently()
    {
        var decision = NotificationDigest.Decide(["a", "paused"], ["a"], null, skipExistingRows: true, BeforeStart, Start);

        Assert.Equal(NotificationDigestAction.RecordSilently, decision.Action);
    }

    [Fact]
    public void Decide_CatchUpAfterRedeploy_MailsRowsFromThePause()
    {
        var decision = NotificationDigest.Decide(["a", "paused"], ["a"], null, skipExistingRows: false, BeforeStart, Start);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal(["paused"], decision.NewRtIds);
    }

    [Fact]
    public void Decide_OnwardsFirstRun_KeepsTheExistingRowsApartFromTheSentOnes()
    {
        var decision = NotificationDigest.Decide(["a", "b", "c"], ["a"], null, skipExistingRows: true, lastRunAt: null, Start);

        Assert.Equal(["a"], decision.SeenRtIds);
        Assert.Equal(["b", "c"], decision.SkippedRtIds);
    }

    [Fact]
    public void Decide_LaterOnwardsRun_MailsNeitherSentNorSkippedRows()
    {
        var decision = NotificationDigest.Decide(["a", "b", "c", "d"], ["a"], ["b", "c"], skipExistingRows: true,
            AfterStart, Start);

        Assert.Equal(["d"], decision.NewRtIds);
        Assert.Equal(["a", "d"], decision.SeenRtIds);
        Assert.Equal(["b", "c"], decision.SkippedRtIds);
    }

    [Fact]
    public void Decide_SwitchToCatchUp_MailsTheSkippedRowsButNotTheSentOnes()
    {
        var decision = NotificationDigest.Decide(["a", "b", "c"], ["a"], ["b", "c"], skipExistingRows: false,
            lastRunAt: null, Start);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal(["b", "c"], decision.NewRtIds);
        Assert.Equal(["a", "b", "c"], decision.SeenRtIds);
        Assert.Empty(decision.SkippedRtIds);
    }

    [Fact]
    public void Decide_CatchUpWithoutARestart_StillMailsTheSkippedRows()
    {
        var decision = NotificationDigest.Decide(["a", "b"], ["a"], ["b"], skipExistingRows: false, AfterStart, Start);

        Assert.Equal(["b"], decision.NewRtIds);
        Assert.Empty(decision.SkippedRtIds);
    }

    [Fact]
    public void Decide_SwitchBackToOnwards_SkipsOnlyWhatWasNotSent()
    {
        var decision = NotificationDigest.Decide(["a", "b", "new"], ["a", "b"], null, skipExistingRows: true,
            lastRunAt: null, Start);

        Assert.Equal(NotificationDigestAction.RecordSilently, decision.Action);
        Assert.Equal(["a", "b"], decision.SeenRtIds);
        Assert.Equal(["new"], decision.SkippedRtIds);
    }

    [Fact]
    public void Decide_RowsThatLeftTheQuery_AreForgottenInBothSets()
    {
        var decision = NotificationDigest.Decide(["a", "b"], ["a", "gone"], ["b", "left"], skipExistingRows: true,
            AfterStart, Start);

        Assert.Equal(["a"], decision.SeenRtIds);
        Assert.Equal(["b"], decision.SkippedRtIds);
    }

    [Fact]
    public void HasOlderTwin_TheNewerPreferenceOfTheSameUserIsTheDuplicate()
    {
        var older = new NotificationRecipient("p1", "user", Start);
        var newer = new NotificationRecipient("p2", "user", AfterStart);

        Assert.True(NotificationDigest.HasOlderTwin(newer, [older, newer]));
        Assert.False(NotificationDigest.HasOlderTwin(older, [older, newer]));
    }

    [Fact]
    public void HasOlderTwin_OtherUsersAreNoTwins()
    {
        var mine = new NotificationRecipient("p2", "user", AfterStart);

        Assert.False(NotificationDigest.HasOlderTwin(mine, [new NotificationRecipient("p1", "other", Start), mine]));
    }

    [Fact]
    public void HasOlderTwin_SameCreationTime_TheLowerRtIdWins()
    {
        var first = new NotificationRecipient("a1", "user", Start);
        var second = new NotificationRecipient("b2", "user", Start);

        Assert.True(NotificationDigest.HasOlderTwin(second, [first, second]));
        Assert.False(NotificationDigest.HasOlderTwin(first, [first, second]));
    }

    [Fact]
    public void HasOlderTwin_APreferenceWithoutUserIsNoTwin()
    {
        var blank = new NotificationRecipient("p2", null, AfterStart);

        Assert.False(NotificationDigest.HasOlderTwin(blank, [new NotificationRecipient("p1", null, Start), blank]));
    }

    [Fact]
    public void LastRunOfQuery_AnotherQueryIsANewStart()
    {
        Assert.Null(NotificationDigest.LastRunOfQuery(AfterStart, "old-query", "new-query"));
    }

    [Fact]
    public void LastRunOfQuery_SameQueryOrNoneRecordedKeepsTheLastRun()
    {
        Assert.Equal(AfterStart, NotificationDigest.LastRunOfQuery(AfterStart, "query", "query"));
        Assert.Equal(AfterStart, NotificationDigest.LastRunOfQuery(AfterStart, null, "query"));
    }

    [Fact]
    public void IsLeaseHeld_OnlyWhileAnotherRunIsYoungerThanTenMinutes()
    {
        Assert.False(NotificationDigest.IsLeaseHeld(null, Start));
        Assert.True(NotificationDigest.IsLeaseHeld(Start.AddMinutes(-9), Start));
        Assert.False(NotificationDigest.IsLeaseHeld(Start.AddMinutes(-11), Start));
    }

    [Fact]
    public void Render_RepeatsTheBodyPerRowAndFillsCount()
    {
        var rendered = NotificationDigest.Render("${count} failed processes", "Process ${name} is ${state}",
            NotificationBodyKind.Plain, ["name", "state"],
            [["P1", "Failed"], ["P2", "Failed"]], TimeZoneInfo.Utc, 2, null);

        Assert.Equal("2 failed processes", rendered.Subject);
        Assert.Equal("Process P1 is Failed\n\nProcess P2 is Failed", rendered.Body);
    }

    [Fact]
    public void Render_ColumnLookupIgnoresCase()
    {
        var rendered = NotificationDigest.Render("x", "${Name}", NotificationBodyKind.Plain, ["name"], [["P1"]],
            TimeZoneInfo.Utc, 1, null);

        Assert.Equal("P1", rendered.Body);
    }

    [Fact]
    public void Render_UnknownPlaceholder_FailsAndNamesIt()
    {
        var exception = Assert.Throws<NotificationRenderException>(() => NotificationDigest.Render("x",
            "${name} ${owner}", NotificationBodyKind.Plain, ["name"], [["P1"]], TimeZoneInfo.Utc, 1, null));

        Assert.Equal(["owner"], exception.UnknownPlaceholders);
    }

    [Fact]
    public void Render_ColumnInSubject_IsUnknownBecauseTheSubjectSpeaksForAllRows()
    {
        var exception = Assert.Throws<NotificationRenderException>(() => NotificationDigest.Render("${name}", "b",
            NotificationBodyKind.Plain, ["name"], [["P1"]], TimeZoneInfo.Utc, 1, null));

        Assert.Equal(["name"], exception.UnknownPlaceholders);
    }

    [Fact]
    public void Render_MarkupBody_EscapesValuesButNotTheTemplate()
    {
        var rendered = NotificationDigest.Render("x", "<b>${name}</b>", NotificationBodyKind.Markup, ["name"],
            [["<a href=\"x\">Müller & Söhne</a>"]], TimeZoneInfo.Utc, 1, null);

        Assert.Equal("<b>&lt;a href=&quot;x&quot;&gt;Müller &amp; Söhne&lt;/a&gt;</b>", rendered.Body);
    }

    [Fact]
    public void Render_EmptyValue_RendersNothing()
    {
        var rendered = NotificationDigest.Render("x", "[${name}]", NotificationBodyKind.Plain, ["name"], [[null]],
            TimeZoneInfo.Utc, 1, null);

        Assert.Equal("[]", rendered.Body);
    }

    [Fact]
    public void Render_Dates_AreShownInThePreferenceTimeZone()
    {
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

        var rendered = NotificationDigest.Render("x", "${failedAt}", NotificationBodyKind.Plain, ["failedAt"],
            [[new DateTime(2026, 10, 1, 6, 30, 0, DateTimeKind.Utc)]], vienna, 1, null);

        Assert.Equal("2026-10-01 08:30", rendered.Body);
    }

    [Fact]
    public void Render_MoreThanTheCap_ListsTheFirstHundredAndSaysHowManyMore()
    {
        var rows = Enumerable.Range(1, 103).Select(i => (IReadOnlyList<object?>)[$"P{i}"]).ToList();

        var rendered = NotificationDigest.Render("${count}", "${name}", NotificationBodyKind.Plain, ["name"], rows,
            TimeZoneInfo.Utc, 103, null);

        Assert.Equal("103", rendered.Subject);
        Assert.StartsWith("P1\n\nP2\n\n", rendered.Body);
        Assert.Contains("P100\n\n...and 3 more. Open the query to see all.", rendered.Body);
        Assert.DoesNotContain("P101", rendered.Body);
    }

    [Fact]
    public void Render_MoreThanTheCapWithAQueryName_NamesTheQueryAndEncodesItForMarkup()
    {
        var rows = Enumerable.Range(1, 101).Select(i => (IReadOnlyList<object?>)[$"P{i}"]).ToList();

        var rendered = NotificationDigest.Render("x", "${name}", NotificationBodyKind.Markup, ["name"], rows,
            TimeZoneInfo.Utc, 101, "Failed <EDA> processes");

        Assert.EndsWith("...and 1 more. Run the query 'Failed &lt;EDA&gt; processes' in the Studio to see all.", rendered.Body);
    }

    [Fact]
    public void MailOrder_UnsortedQuery_ListsOldestFirstByRtId()
    {
        var order = NotificationDigest.MailOrder(["6accc8dfe40af9ce1161e1fd", "6accc8c5e40af9ce1161e1e3", "6accca3ae40af9ce1161e268"], querySorts: false);

        Assert.Equal(["6accc8c5e40af9ce1161e1e3", "6accc8dfe40af9ce1161e1fd", "6accca3ae40af9ce1161e268"], order);
    }

    [Fact]
    public void MailOrder_SortedQuery_KeepsTheQueryOrder()
    {
        var order = NotificationDigest.MailOrder(["c", "a", "b"], querySorts: true);

        Assert.Equal(["c", "a", "b"], order);
    }

    [Fact]
    public void StateUnchanged_SameSetsInAnotherOrder_IsUnchanged()
    {
        var decision = NotificationDigest.Decide(["b", "a", "c"], ["a", "b"], ["c"], skipExistingRows: true,
            AfterStart, Start);

        Assert.True(NotificationDigest.StateUnchanged(decision, ["b", "a"], ["c"]));
    }

    [Fact]
    public void StateUnchanged_ARowThatLeftTheQuery_IsAChange()
    {
        var decision = NotificationDigest.Decide(["a"], ["a", "b"], null, skipExistingRows: false, AfterStart, Start);

        Assert.False(NotificationDigest.StateUnchanged(decision, ["a", "b"], null));
    }

    [Fact]
    public void StateUnchanged_ASkippedRowThatLeftTheQuery_IsAChange()
    {
        var decision = NotificationDigest.Decide(["a"], ["a"], ["gone"], skipExistingRows: true, AfterStart, Start);

        Assert.False(NotificationDigest.StateUnchanged(decision, ["a"], ["gone"]));
    }

    [Fact]
    public void StateUnchanged_NothingRecordedYet_IsAChange()
    {
        var decision = NotificationDigest.Decide([], null, null, skipExistingRows: false, AfterStart, Start);

        Assert.False(NotificationDigest.StateUnchanged(decision, null, null));
    }

    [Fact]
    public void Render_TotalBeyondTheRowsGiven_CountsAndSaysHowManyMore()
    {
        var rows = Enumerable.Range(1, 100).Select(i => (IReadOnlyList<object?>)[$"P{i}"]).ToList();

        var rendered = NotificationDigest.Render("${count}", "${name}", NotificationBodyKind.Plain, ["name"], rows,
            TimeZoneInfo.Utc, 2500, null);

        Assert.Equal("2500", rendered.Subject);
        Assert.EndsWith("P100\n\n...and 2400 more. Open the query to see all.", rendered.Body);
    }

    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsBackingOff_RecentFailure_Waits()
    {
        Assert.True(NotificationDigest.IsBackingOff("boom", Now.AddMinutes(-14), Now, null));
    }

    [Fact]
    public void IsBackingOff_ConfigurationChangedAfterTheFailure_RunsAtOnce()
    {
        Assert.False(NotificationDigest.IsBackingOff("boom", Now.AddMinutes(-10), Now, Now.AddMinutes(-2)));
    }

    [Fact]
    public void IsBackingOff_ConfigurationChangedBeforeTheFailure_Waits()
    {
        Assert.True(NotificationDigest.IsBackingOff("boom", Now.AddMinutes(-10), Now, Now.AddMinutes(-30)));
    }

    [Theory]
    [InlineData("boom", -16)]
    [InlineData(null, -1)]
    [InlineData("", -1)]
    public void IsBackingOff_OldFailureOrNoError_Runs(string? lastError, int minutesAgo)
    {
        Assert.False(NotificationDigest.IsBackingOff(lastError, Now.AddMinutes(minutesAgo), Now, null));
    }

    [Fact]
    public void NeedsIdleWrite_NothingChangedRecently_WritesNothing()
    {
        Assert.False(NotificationDigest.NeedsIdleWrite(NotificationDigestAction.NothingNew, true, null,
            Now.AddMinutes(-59), Now));
    }

    [Theory]
    [InlineData((int)NotificationDigestAction.RecordSilently, true, null, -1)]
    [InlineData((int)NotificationDigestAction.NothingNew, false, null, -1)]
    [InlineData((int)NotificationDigestAction.NothingNew, true, "boom", -1)]
    [InlineData((int)NotificationDigestAction.NothingNew, true, null, -60)]
    public void NeedsIdleWrite_SomethingToRecord_Writes(int action, bool sameRows,
        string? lastError, int minutesAgo)
    {
        Assert.True(NotificationDigest.NeedsIdleWrite((NotificationDigestAction)action, sameRows, lastError, Now.AddMinutes(minutesAgo), Now));
    }

    [Fact]
    public void NeedsIdleWrite_NeverRecorded_Writes()
    {
        Assert.True(NotificationDigest.NeedsIdleWrite(NotificationDigestAction.NothingNew, true, null, null, Now));
    }
}
