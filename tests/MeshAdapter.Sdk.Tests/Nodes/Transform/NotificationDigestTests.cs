using System.Text.RegularExpressions;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
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

    private static DeliveryMemory Memory(DateTime? processedUntil, IReadOnlyList<SentRow>? sentRows = null,
        string? queryRtId = "q", string? subjectId = "user-a", bool? skipExistingRows = true,
        DateTime? lastRunAt = null) =>
        new(processedUntil, sentRows ?? [], queryRtId, subjectId, skipExistingRows, lastRunAt ?? AfterStart);

    private static NotificationCandidate Row(string rtId, DateTime changedAt) =>
        new(new Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId(rtId), changedAt);

    private const string A = "670000000000000000000001";
    private const string B = "670000000000000000000002";
    private const string C = "670000000000000000000003";

    [Fact]
    public void Plan_OnwardsFirstRun_ReadsFromTheStart()
    {
        var plan = NotificationDigest.Plan(null, "user-a", skipExistingRows: true, "q", Start, null, null, Now);

        Assert.True(plan.Restarted);
        Assert.Equal(Start, plan.ReadFrom);
        Assert.Equal(Start, NotificationDigest.ReadFrom(plan.ProcessedUntil));
    }

    [Fact]
    public void Decide_OnwardsFirstRun_SendsTheRowsChangedSinceTheStart()
    {
        var plan = NotificationDigest.Plan(null, "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [Row(A, Start.AddMinutes(1))], Now);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal([A], decision.NewRtIds);
        Assert.Equal(Start + NotificationDigest.Overlap, decision.ProcessedUntil);
    }

    [Fact]
    public void Decide_OnwardsFirstRunWithNothingNew_KeepsReadingFromTheStart()
    {
        var plan = NotificationDigest.Plan(null, "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [], Now);

        Assert.Equal(NotificationDigestAction.NothingNew, decision.Action);
        Assert.Equal(Start, NotificationDigest.ReadFrom(decision.ProcessedUntil));
    }

    [Fact]
    public void Plan_CatchUpFirstRun_ReadsFromTheBeginning()
    {
        var plan = NotificationDigest.Plan(null, "user-a", skipExistingRows: false, "q", Start, null, null, Now);

        Assert.True(plan.Restarted);
        Assert.Null(plan.ReadFrom);
    }

    [Fact]
    public void Plan_LaterRun_ReadsFromTheWatermarkMinusTheOverlap()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1)), "user-a", true, "q", Start, null, null, Now);

        Assert.False(plan.Restarted);
        Assert.Equal(Now.AddHours(-1) - NotificationDigest.Overlap, plan.ReadFrom);
    }

    [Fact]
    public void Plan_OnwardsStartWithNoMatchingRows_ReadsEverythingLater()
    {
        var plan = NotificationDigest.Plan(Memory(DateTime.MinValue), "user-a", true, "q", Start, null, null, Now);

        Assert.False(plan.Restarted);
        Assert.Null(plan.ReadFrom);
    }

    [Theory]
    [InlineData("other-query", "user-a", true)]
    [InlineData("q", "user-b", true)]
    [InlineData("q", "user-a", false)]
    public void Plan_AnotherQueryUserOrStartOption_IsANewStart(string queryRtId, string subjectId, bool onwards)
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1)), subjectId, onwards, queryRtId, Start, null, null, Now);

        Assert.True(plan.Restarted);
        Assert.Equal(onwards, plan.ReadFrom != null);
    }

    [Fact]
    public void Plan_AnotherUserOrStartOption_StartsWhenThePreferenceChanged()
    {
        var changedAt = Now.AddMinutes(-5);

        var otherUser = NotificationDigest.Plan(Memory(Now.AddHours(-1)), "user-b", true, "q", Start, changedAt, null, Now);
        var otherOption = NotificationDigest.Plan(Memory(Now.AddHours(-1), skipExistingRows: false), "user-a", true, "q", Start,
            changedAt, null, Now);

        Assert.Equal(changedAt, otherUser.ReadFrom);
        Assert.Equal(changedAt, otherOption.ReadFrom);
    }

    [Fact]
    public void Plan_AnotherQuery_StartsWhenTheSubscriptionChanged()
    {
        var changedAt = Now.AddMinutes(-5);

        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), lastRunAt: Now.AddMinutes(-30)), "user-a", true,
            "other-query", Start, Now.AddMinutes(-10), changedAt, Now);

        Assert.Equal(changedAt, plan.ReadFrom);
    }

    [Fact]
    public void Plan_AnotherQueryWithoutASubscriptionChange_StartsAfterTheLastRunOfTheOldOne()
    {
        var lastRunAt = Now.AddMinutes(-5);

        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), lastRunAt: lastRunAt), "user-a", true, "other-query",
            Start, Now.AddMinutes(-10), BeforeStart, Now);

        Assert.Equal(lastRunAt, plan.ReadFrom);
    }

    [Fact]
    public void Plan_AChangeBeforeTheDeployment_StartsAtTheDeployment()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), lastRunAt: BeforeStart), "user-b", true, "other-query",
            Start, BeforeStart, BeforeStart, Now);

        Assert.Equal(Start, plan.ReadFrom);
    }

    [Fact]
    public void Plan_ARedeployAfterTheLastRun_IsANewStartForOnwards()
    {
        var plan = NotificationDigest.Plan(Memory(BeforeStart, lastRunAt: BeforeStart), "user-a", true, "q", Start, null, null, Now);

        Assert.True(plan.Restarted);
        Assert.Equal(Start, plan.ReadFrom);
    }

    [Fact]
    public void Plan_AnotherUser_ForgetsWhatThePreviousUserGot()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))]), "user-b",
            false, "q", Start, null, null, Now);

        Assert.Empty(plan.SentRows);
    }

    [Fact]
    public void Plan_SwitchToCatchUp_SendsTheFullSummaryAgain()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))]), "user-a",
            false, "q", Start, null, null, Now);

        Assert.Null(plan.ReadFrom);
        Assert.Empty(plan.SentRows);
    }

    [Fact]
    public void Plan_CatchUpRedeployed_ReadsOnFromItsWatermark_AndKeepsWhatWasSent()
    {
        var plan = NotificationDigest.Plan(
            Memory(BeforeStart, [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))], skipExistingRows: false, lastRunAt: BeforeStart),
            "user-a", false, "q", Start, null, null, Now);

        Assert.False(plan.Restarted);
        Assert.Equal(NotificationDigest.ReadFrom(BeforeStart), plan.ReadFrom);
        Assert.Equal([A], plan.SentRows.Select(row => row.RtId));
    }

    [Fact]
    public void Plan_SentRowsOlderThanThirtyDays_AreForgotten()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1),
                [new SentRow(A, Now.AddDays(-31), Now.AddDays(-31)), new SentRow(B, Now.AddDays(-29), Now.AddDays(-29))]),
            "user-a", true, "q", Start, null, null, Now);

        Assert.Equal([B], plan.SentRows.Select(row => row.RtId));
    }

    [Fact]
    public void Decide_NewRows_AreSentAndRemembered()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1)), "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [Row(A, Now.AddMinutes(-30)), Row(B, Now.AddMinutes(-10))], Now);

        Assert.Equal(NotificationDigestAction.Send, decision.Action);
        Assert.Equal([A, B], decision.NewRtIds);
        Assert.Equal(Now.AddMinutes(-10), decision.ProcessedUntil);
        Assert.Equal([A, B], decision.SentRows.Select(row => row.RtId).Order());
    }

    [Fact]
    public void Decide_ARowAlreadySent_IsNotSentAgain()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))]), "user-a",
            true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [Row(A, Now.AddMinutes(-30))], Now);

        Assert.Equal(NotificationDigestAction.NothingNew, decision.Action);
    }

    [Fact]
    public void Decide_NothingChanged_KeepsTheWatermark()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1)), "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [], Now);

        Assert.Equal(NotificationDigestAction.NothingNew, decision.Action);
        Assert.Equal(Now.AddHours(-1), decision.ProcessedUntil);
    }

    [Fact]
    public void Decide_RowsInsideTheOverlap_NeverMoveTheWatermarkBack()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1)), "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [Row(A, Now.AddHours(-1).AddMinutes(-2))], Now);

        Assert.Equal(Now.AddHours(-1), decision.ProcessedUntil);
    }

    [Fact]
    public void Decide_RememberedRows_AreCappedToTheMostRecent()
    {
        var old = Enumerable.Range(0, NotificationDigest.MaxSentRows)
            .Select(i => new SentRow($"6700000000000000{i:D8}", Now.AddDays(-1), Now.AddDays(-1))).ToList();
        var plan = NotificationDigest.Plan(Memory(Now.AddHours(-1), old), "user-a", true, "q", Start, null, null, Now);

        var decision = NotificationDigest.Decide(plan, [Row(C, Now.AddMinutes(-1))], Now);

        Assert.Equal(NotificationDigest.MaxSentRows, decision.SentRows.Count);
        Assert.Contains(decision.SentRows, row => row.RtId == C);
    }

    [Fact]
    public void Plan_ARowTheNextRunReadsAgain_IsRememberedBeyondThirtyDays()
    {
        var quietSince = Now.AddDays(-40);
        var plan = NotificationDigest.Plan(Memory(quietSince, [new SentRow(A, quietSince, quietSince)]), "user-a",
            true, "q", Start, null, null, Now);

        Assert.Equal([A], plan.SentRows.Select(row => row.RtId));
    }

    [Fact]
    public void Decide_AFloodInsideTheOverlap_IsCapped_AndTheWatermarkPassesWhatIsDropped()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddMinutes(-2)), "user-a", true, "q", Start, null, null, Now);
        var flood = Enumerable.Range(0, NotificationDigest.MaxSentRows + 5)
            .Select(i => Row($"6700000000000000{i:D8}", Now.AddMinutes(-1).AddMilliseconds(i))).ToList();

        var decision = NotificationDigest.Decide(plan, flood, Now);

        Assert.Equal(NotificationDigest.MaxSentRows + 5, decision.NewRtIds.Count);
        Assert.Equal(NotificationDigest.MaxSentRows, decision.SentRows.Count);
        Assert.Equal(flood[4].ChangedAt + NotificationDigest.Overlap, decision.ProcessedUntil);
        var readFrom = NotificationDigest.ReadFrom(decision.ProcessedUntil);
        Assert.All(flood.Take(5), row => Assert.True(row.ChangedAt <= readFrom));
        Assert.All(decision.SentRows, row => Assert.True(row.ChangedAt > readFrom));
    }

    [Fact]
    public void Decide_AFloodSpreadOverSeveralRuns_StaysCapped()
    {
        var earlier = Enumerable.Range(0, NotificationDigest.MaxSentRows)
            .Select(i => new SentRow($"6700000000000000{i:D8}", Now.AddMinutes(-1), Now.AddMinutes(-2).AddMilliseconds(i)))
            .ToList();
        var plan = NotificationDigest.Plan(Memory(earlier[^1].ChangedAt, earlier), "user-a", true, "q", Start, null, null, Now);
        var page = Enumerable.Range(0, 3)
            .Select(i => Row($"6800000000000000{i:D8}", earlier[^1].ChangedAt.AddMilliseconds(1 + i))).ToList();

        var decision = NotificationDigest.Decide(plan, page, Now);

        Assert.Equal(3, decision.NewRtIds.Count);
        Assert.Equal(NotificationDigest.MaxSentRows, decision.SentRows.Count);
        Assert.Equal(earlier[2].ChangedAt + NotificationDigest.Overlap, decision.ProcessedUntil);
    }

    [Fact]
    public void Decide_MoreRowsThanTheCapChangedInTheSameMillisecond_AreAllRemembered()
    {
        var plan = NotificationDigest.Plan(Memory(Now.AddMinutes(-2)), "user-a", true, "q", Start, null, null, Now);
        var flood = Enumerable.Range(0, NotificationDigest.MaxSentRows + 5)
            .Select(i => Row($"6700000000000000{i:D8}", Now.AddMinutes(-1))).ToList();

        var decision = NotificationDigest.Decide(plan, flood, Now);

        Assert.Equal(NotificationDigest.MaxSentRows + 5, decision.SentRows.Count);
        Assert.Equal(Now.AddMinutes(-1), decision.ProcessedUntil);
    }

    [Theory]
    [InlineData("SerialNumber", false)]
    [InlineData("Address.City", false)]
    [InlineData("Parent.Process->Success", true)]
    [InlineData("Children::count", true)]
    public void FiltersThroughAssociations_OnlyForAPathIntoAnotherEntity(string attributePath, bool expected)
    {
        var query = new Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2.RtSimpleRtQuery
        {
            FieldFilter = new Meshmakers.Octo.Runtime.Contracts.RepositoryEntities.AttributeRecordValueList<
                Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2.RtFieldFilterRecord>
            {
                new() { AttributePath = attributePath, ComparisonValue = "x" }
            }
        };

        Assert.Equal(expected, SimpleRtQueryExecution.FiltersThroughAssociations(query));
    }

    [Fact]
    public void Plan_SwitchToOnwards_KeepsTheRowsAlreadySent()
    {
        var memory = Memory(Now.AddHours(-1), [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))],
            skipExistingRows: false);

        var plan = NotificationDigest.Plan(memory, "user-a", true, "q", Start, null, null, Now);

        Assert.Equal([A], plan.SentRows.Select(row => row.RtId));
    }

    [Fact]
    public void MemoryUnchanged_OnlyWhenWatermarkRowsAndRecipientAreTheSame()
    {
        var memory = Memory(Now.AddHours(-1), [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))]);
        var same = new NotificationDigestDecision(NotificationDigestAction.NothingNew, [], Now.AddHours(-1),
            [new SentRow(A, Now.AddHours(-1), Now.AddHours(-1))]);
        var moved = same with { ProcessedUntil = Now };

        Assert.True(NotificationDigest.MemoryUnchanged(memory, same, "q", "user-a", true));
        Assert.False(NotificationDigest.MemoryUnchanged(memory, moved, "q", "user-a", true));
        Assert.False(NotificationDigest.MemoryUnchanged(memory, same, "q", "user-b", true));
        Assert.False(NotificationDigest.MemoryUnchanged(null, same, "q", "user-a", true));
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

    [Theory]
    [InlineData("[Confirm your account](https://evil.example)")]
    [InlineData("![](https://tracker.example/px)")]
    [InlineData("[Confirm][1]\n\n[1]: https://evil.example")]
    [InlineData("*bold* _it_ `code` # ~~x~~ | a | {b} $c$ \\[d\\] +e ^f^")]
    public void Render_MarkupBody_ARowValueStaysText_InBothHalvesOfTheMail(string value)
    {
        var rendered = NotificationDigest.Render("x", "[Open](https://studio.example) ${name}",
            NotificationBodyKind.Markup, ["name"], [[value]], TimeZoneInfo.Utc, 1, null);

        var html = EMailSenderNode2.RenderBody(rendered.Body, BodyFormats.Markdown);
        Assert.Contains("<a href=\"https://studio.example\">Open</a>", html);
        Assert.All(Regex.Matches(html, "<a href=\"([^\"]*)\">([^<]*)</a>").Skip(1),
            link => Assert.Equal(link.Groups[1].Value, link.Groups[2].Value));
        Assert.Equal(html.Split("<a ").Length - 1, Regex.Matches(html, "<a href=\"([^\"]*)\">([^<]*)</a>").Count);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<em>", html);
        Assert.DoesNotContain("<code>", html);
        Assert.Equal($"[Open](https://studio.example) {value}",
            EMailSenderNode2.PlainTextAlternative(rendered.Body, BodyFormats.Markdown));
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
