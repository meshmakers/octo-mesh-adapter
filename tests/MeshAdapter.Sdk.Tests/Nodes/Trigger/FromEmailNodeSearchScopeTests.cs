using MailKit;
using MailKit.Search;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5336 / AB#5340. Both regressions were found together on prod-1/gastroacker, where the invoice
/// folder holds 1697 mails: with <c>onlyUnread: false</c> the node ran <c>SearchQuery.All</c>, fetched
/// every hit and base64-encoded every attachment into one batch, and died with an
/// <c>OutOfMemoryException</c> 32 seconds after start — then re-attempted the identical work on every
/// restart, so the import never made progress. There was also no way to express "only the open
/// accounting period", the two available settings being "unread only" (which finds nothing once
/// everything is flagged read) and "absolutely everything".
///
/// The fix has two halves and both are asserted here: the window is narrowed SERVER-SIDE via IMAP
/// <c>SINCE</c>, and whatever still comes back is drained at most <c>MaxMessagesPerPoll</c> per pass.
/// </summary>
public class FromEmailNodeSearchScopeTests
{
    private static FromEmailNodeConfiguration Config(
        bool onlyUnread = true,
        DateTime? sinceDate = null,
        int? sinceDaysBack = null,
        int? maxMessagesPerPoll = 25) =>
        new()
        {
            ServerConfiguration = "TestServer",
            OnlyUnread = onlyUnread,
            SinceDate = sinceDate,
            SinceDaysBack = sinceDaysBack,
            MaxMessagesPerPoll = maxMessagesPerPoll
        };

    private static List<UniqueId> Uids(int count) =>
        Enumerable.Range(1, count).Select(i => new UniqueId((uint)i)).ToList();

    // ---- AB#5340: the date cut-off -------------------------------------------------

    [Fact]
    public void NoDateConfigured_KeepsThePlainReadStatePredicate()
    {
        Assert.Null(FromEmailNode.ResolveSinceDate(Config()));

        var query = FromEmailNode.BuildSearchQuery(Config());

        Assert.Equal(SearchTerm.NotSeen, query.Term);
    }

    [Fact]
    public void OnlyUnreadFalse_WithoutDate_StillSearchesEverything()
    {
        // The pre-fix behaviour has to stay reachable, because that is what an operator gets
        // when no period is configured. It is the combination with a cut-off that makes it safe.
        var query = FromEmailNode.BuildSearchQuery(Config(onlyUnread: false));

        Assert.Equal(SearchTerm.All, query.Term);
    }

    [Fact]
    public void SinceDate_IsAndedOntoTheReadStatePredicate()
    {
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var query = Assert.IsType<BinarySearchQuery>(FromEmailNode.BuildSearchQuery(Config(sinceDate: since)));

        Assert.Equal(SearchTerm.And, query.Term);
        Assert.Equal(SearchTerm.NotSeen, query.Left.Term);

        // DeliveredAfter maps to IMAP SINCE, which the server evaluates on INTERNALDATE with date
        // granularity and an INCLUSIVE lower bound — so a fiscal year starting 2026-01-01 keeps the
        // mails delivered on 2026-01-01 itself.
        var right = Assert.IsType<DateSearchQuery>(query.Right);
        Assert.Equal(SearchTerm.DeliveredAfter, right.Term);
        Assert.Equal(since.Date, right.Date.Date);
    }

    [Fact]
    public void SinceDate_CombinesWithOnlyUnreadFalse()
    {
        var query = Assert.IsType<BinarySearchQuery>(
            FromEmailNode.BuildSearchQuery(Config(onlyUnread: false, sinceDate: new DateTime(2026, 1, 1))));

        Assert.Equal(SearchTerm.All, query.Left.Term);
        Assert.Equal(SearchTerm.DeliveredAfter, query.Right.Term);
    }

    [Fact]
    public void SinceDate_IsTruncatedToTheDay()
    {
        // IMAP SINCE has date granularity; carrying a time component would only invite the
        // expectation that it is honoured.
        var resolved = FromEmailNode.ResolveSinceDate(
            Config(sinceDate: new DateTime(2026, 1, 1, 17, 45, 13, DateTimeKind.Utc)));

        Assert.Equal(new DateTime(2026, 1, 1), resolved);
    }

    [Fact]
    public void SinceDaysBack_IsTheRelativeFallback()
    {
        // Bracket the call rather than reading UtcNow once afterwards: crossing UTC midnight between
        // the method's read and the assertion's would fail on a perfectly correct result.
        var before = DateTime.UtcNow.Date;
        var resolved = FromEmailNode.ResolveSinceDate(Config(sinceDaysBack: 30));
        var after = DateTime.UtcNow.Date;

        Assert.Contains(resolved, new DateTime?[] { before.AddDays(-30), after.AddDays(-30) });
    }

    [Fact]
    public void SinceDate_WinsOverSinceDaysBack()
    {
        var resolved = FromEmailNode.ResolveSinceDate(
            Config(sinceDate: new DateTime(2026, 1, 1), sinceDaysBack: 3));

        Assert.Equal(new DateTime(2026, 1, 1), resolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void NonPositiveSinceDaysBack_IsIgnored(int daysBack)
    {
        Assert.Null(FromEmailNode.ResolveSinceDate(Config(sinceDaysBack: daysBack)));
    }

    // ---- AB#5336: the per-poll cap -------------------------------------------------

    [Fact]
    public void Backlog_IsCappedAtMaxMessagesPerPoll()
    {
        // The gastroacker shape: far more pending than one pass may carry.
        var batch = FromEmailNode.ApplyBatchCap(Uids(1697), maxMessagesPerPoll: 25);

        Assert.Equal(25, batch.Count);
        Assert.Equal(new UniqueId(1), batch[0]);
        Assert.Equal(new UniqueId(25), batch[24]);
    }

    [Fact]
    public void BacklogDrainsInOrderAcrossConsecutivePolls()
    {
        // Successive passes must advance rather than re-offer the same head, otherwise a backlog
        // never clears. Processed ids are filtered out by the caller, so the next pass sees the tail.
        var remaining = Uids(60);
        var seen = new List<UniqueId>();

        for (var poll = 0; poll < 3; poll++)
        {
            var batch = FromEmailNode.ApplyBatchCap(remaining, maxMessagesPerPoll: 25);
            seen.AddRange(batch);
            remaining = remaining.Skip(batch.Count).ToList();
        }

        Assert.Equal(60, seen.Count);
        Assert.Equal(seen, seen.Distinct().ToList());
        Assert.Empty(remaining);
    }

    [Fact]
    public void SmallBacklog_IsReturnedWhole()
    {
        var pending = Uids(4);

        Assert.Equal(pending, FromEmailNode.ApplyBatchCap(pending, maxMessagesPerPoll: 25));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCap_MeansUnlimited(int cap)
    {
        // Deliberate opt-out, kept so an operator can restore the old behaviour knowingly.
        Assert.Equal(1697, FromEmailNode.ApplyBatchCap(Uids(1697), cap).Count);
    }

    [Fact]
    public void DefaultConfiguration_IsBoundedAndUnread()
    {
        // A node added without touching the new settings must be safe by default — the prod-1
        // failure was reachable purely by flipping an existing switch.
        var defaults = new FromEmailNodeConfiguration { ServerConfiguration = "TestServer" };

        Assert.True(FromEmailNode.ResolveMaxMessagesPerPoll(defaults) > 0);
        Assert.True(defaults.OnlyUnread);
    }

    [Fact]
    public void ExplicitNullCap_StillUsesTheDefault_AndIsNotReadAsUnlimited()
    {
        // The pipeline definition is YAML, where a key that is PRESENT and null overwrites a property
        // initializer. On a non-nullable int that lands on 0 — which this node reads as the deliberate
        // "no limit" opt-out, so the OOM protection would switch itself back off without a trace.
        var explicitNull = Config(maxMessagesPerPoll: null);

        Assert.Equal(FromEmailNodeConfiguration.DefaultMaxMessagesPerPoll,
            FromEmailNode.ResolveMaxMessagesPerPoll(explicitNull));
        Assert.Equal(25, FromEmailNode.ApplyBatchCap(Uids(1697),
            FromEmailNode.ResolveMaxMessagesPerPoll(explicitNull)).Count);
    }

    [Fact]
    public void ZeroCap_RemainsTheDeliberateOptOut()
    {
        // Distinct from the null case above: 0 is something an operator typed.
        Assert.Equal(0, FromEmailNode.ResolveMaxMessagesPerPoll(Config(maxMessagesPerPoll: 0)));
    }
}
