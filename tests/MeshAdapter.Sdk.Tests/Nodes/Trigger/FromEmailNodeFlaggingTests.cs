using MailKit;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5337. <c>FromEmail@1</c> used to flag every fetched message <c>\Seen</c> (and <c>\Deleted</c>)
/// while the batch was still being assembled — the pipeline had not run yet. With the default
/// <c>onlyUnread: true</c> the next poll asks the server for <c>NOT SEEN</c>, so a mail whose run
/// then failed was never offered again: the receipt was gone and nothing about it was missing in a
/// way anyone would notice. Confirmed on prod-1/gastroacker on 2026-09-23, where the run threw in
/// <c>SendEMail@1</c> after the document had already been staged — harmless by luck; a failure
/// before staging (stager timeout, <c>RenderHtmlPdf</c> OOM, the allowlist gate) destroys the
/// document instead.
///
/// The write-back is therefore a decision taken AFTER the run, and this is where that decision is
/// pinned: a run that did not succeed writes nothing back, whatever the node is configured to do.
/// </summary>
public class FromEmailNodeFlaggingTests
{
    private static FromEmailNodeConfiguration Config(
        bool markAsRead = true,
        bool deleteAfterProcessing = false) =>
        new()
        {
            ServerConfiguration = "TestServer",
            MarkAsRead = markAsRead,
            DeleteAfterProcessing = deleteAfterProcessing
        };

    // ---- The regression itself -----------------------------------------------------

    [Fact]
    public void FailedRun_WritesNothingBackToTheServer()
    {
        // The whole point of the fix: even with both switches on, a failed run leaves the mailbox
        // exactly as it was, so the mail is still unread and still there.
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: true, deleteAfterProcessing: true), pipelineSucceeded: false);

        Assert.Equal(MessageFlags.None, decision.Flags);
        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void FailedRun_NeverExpunges()
    {
        // Stated separately because expunging is the irreversible half: a `\Deleted` flag can be
        // removed again, an expunged message is gone from the server for good.
        Assert.False(FromEmailNode
            .ResolveFlagDecision(Config(deleteAfterProcessing: true), pipelineSucceeded: false)
            .Expunge);
    }

    [Fact]
    public void DefaultConfiguration_MarksSeenOnlyAfterASuccessfulRun()
    {
        // `markAsRead` defaults to true and the accounting document import runs with exactly these
        // defaults, so this pair is the configuration the field bug happened under.
        var defaults = new FromEmailNodeConfiguration { ServerConfiguration = "TestServer" };

        Assert.Equal(MessageFlags.None,
            FromEmailNode.ResolveFlagDecision(defaults, pipelineSucceeded: false).Flags);
        Assert.Equal(MessageFlags.Seen,
            FromEmailNode.ResolveFlagDecision(defaults, pipelineSucceeded: true).Flags);
    }

    // ---- What a successful run writes ----------------------------------------------

    [Fact]
    public void SuccessfulRun_MarksSeenWhenConfigured()
    {
        var decision = FromEmailNode.ResolveFlagDecision(Config(), pipelineSucceeded: true);

        Assert.Equal(MessageFlags.Seen, decision.Flags);
        Assert.True(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void SuccessfulRun_DeletesAndExpungesWhenConfigured()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: false, deleteAfterProcessing: true), pipelineSucceeded: true);

        Assert.Equal(MessageFlags.Deleted, decision.Flags);
        // `\Deleted` only marks — without the expunge the message stays in the folder.
        Assert.True(decision.Expunge);
    }

    [Fact]
    public void SuccessfulRun_CombinesBothFlagsInOneWrite()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: true, deleteAfterProcessing: true), pipelineSucceeded: true);

        Assert.Equal(MessageFlags.Seen | MessageFlags.Deleted, decision.Flags);
        Assert.True(decision.Expunge);
    }

    [Fact]
    public void NeitherOptionConfigured_TouchesTheServerAtAllOnSuccess()
    {
        // "Leave the mailbox alone" has to stay reachable: an operator polling a shared folder
        // read-only relies on the node not changing anything.
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: false), pipelineSucceeded: true);

        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }
}
