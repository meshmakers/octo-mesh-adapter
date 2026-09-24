using System.Text.Json.Nodes;
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
/// The write-back is therefore a decision taken AFTER the run, and this is where the last step of
/// it is pinned: given the gate, what reaches the server.
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
    public void ABlockedRun_WritesNothingBackToTheServer()
    {
        // The whole point of the fix: even with both switches on, a run the gate blocked leaves
        // the mailbox exactly as it was, so the mail is still unread and still there.
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: true, deleteAfterProcessing: true), writeBackAllowed: false);

        Assert.Equal(MessageFlags.None, decision.Flags);
        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void ABlockedRun_NeverExpunges()
    {
        // Stated separately because expunging is the irreversible half: a `\Deleted` flag can be
        // removed again, an expunged message is gone from the server for good.
        Assert.False(FromEmailNode
            .ResolveFlagDecision(Config(deleteAfterProcessing: true), writeBackAllowed: false)
            .Expunge);
    }

    [Fact]
    public void DefaultConfiguration_MarksSeenOnlyWhenTheWriteBackIsAllowed()
    {
        // `markAsRead` defaults to true and the accounting document import runs with exactly these
        // defaults, so this pair is the configuration the field bug happened under.
        var defaults = new FromEmailNodeConfiguration { ServerConfiguration = "TestServer" };

        Assert.Equal(MessageFlags.None,
            FromEmailNode.ResolveFlagDecision(defaults, writeBackAllowed: false).Flags);
        Assert.Equal(MessageFlags.Seen,
            FromEmailNode.ResolveFlagDecision(defaults, writeBackAllowed: true).Flags);
    }

    // ---- What a confirmed run writes -----------------------------------------------

    [Fact]
    public void AnAllowedRun_MarksSeenWhenConfigured()
    {
        var decision = FromEmailNode.ResolveFlagDecision(Config(), writeBackAllowed: true);

        Assert.Equal(MessageFlags.Seen, decision.Flags);
        Assert.True(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void AnAllowedRun_DeletesAndExpungesWhenConfigured()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: false, deleteAfterProcessing: true), writeBackAllowed: true);

        Assert.Equal(MessageFlags.Deleted, decision.Flags);
        // `\Deleted` only marks — without the expunge the message stays in the folder.
        Assert.True(decision.Expunge);
    }

    [Fact]
    public void AnAllowedRun_CombinesBothFlagsInOneWrite()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: true, deleteAfterProcessing: true), writeBackAllowed: true);

        Assert.Equal(MessageFlags.Seen | MessageFlags.Deleted, decision.Flags);
        Assert.True(decision.Expunge);
    }

    [Fact]
    public void NeitherOptionConfigured_TouchesTheServerAtAllOnAnAllowedRun()
    {
        // "Leave the mailbox alone" has to stay reachable: an operator polling a shared folder
        // read-only relies on the node not changing anything.
        var decision = FromEmailNode.ResolveFlagDecision(
            Config(markAsRead: false), writeBackAllowed: true);

        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }
}

/// <summary>
/// AB#5337, second round. The first fix read "<c>ExecuteAsync</c> returned" as success, and that is
/// too weak: a pipeline ends perfectly normally while a node reported an error and stopped its
/// branch. <c>MakeHttpRequest@1</c>'s <c>OnHttpError: LogAndStop</c> — its DEFAULT — is documented
/// as "report the failure and stop this branch, leaving the execution successful", so a staging call
/// that 500s takes the import branch out while the run still completes. The platform offers a
/// trigger nothing finer: <c>INodeContext.Error</c> only writes to the pipeline log,
/// <c>IEtlContext</c> carries no error state, and <c>PipelineExecutionStatus</c> is
/// <c>Completed</c> for everything that did not throw. The single per-run channel back to the
/// trigger is the data root <c>ExecuteAsync</c> returns — so where a <c>successPath</c> is
/// configured, the pipeline states the outcome itself and the trigger believes nothing else.
///
/// Three levels, all pinned below: a run that THREW never flags (unconditional — that is the fix);
/// with no <c>successPath</c> a run that came back flags as it always did (no fleet-wide
/// regression); with one, only a confirmed run flags. AB#5345 supersedes this with a business
/// success criterion and one post-processing mode shared by the IMAP and Graph channels.
/// </summary>
public class FromEmailNodeRunConfirmationTests
{
    private const string SuccessPath = "$.importCompleted";

    /// <summary>
    /// The shape a real run comes back in: the batch the trigger handed in, plus whatever the
    /// pipeline wrote. <paramref name="tail" /> is spliced in as further properties.
    /// </summary>
    private static JsonNode PipelineResult(string tail = "") =>
        JsonNode.Parse($$"""
                         {
                           "Emails": [ { "Subject": "Rechnung", "FromAddress": "a@example.com" } ],
                           "Count": 1
                           {{tail}}
                         }
                         """)!;

    // ---- The review's case ---------------------------------------------------------

    [Fact]
    public void ARunThatEndedWithoutWritingTheFlag_IsNotConfirmed()
    {
        // The LogAndStop shape: nothing threw, the data root came back, and the import branch
        // never reached its last node — so the confirmation the pipeline promised is simply absent.
        var result = PipelineResult();

        Assert.Equal(MailRunConfirmation.NotConfirmed,
            MailSuccessPath.Evaluate(SuccessPath, result));
    }

    [Fact]
    public void ARunThatEndedWithoutWritingTheFlag_FlagsNothing()
    {
        // The two halves joined: this is the sequence the poll loop runs, and a batch whose branch
        // stopped must leave the mailbox untouched even though nothing threw.
        var confirmed = MailSuccessPath.Evaluate(SuccessPath, PipelineResult())
                        == MailRunConfirmation.Confirmed;

        var decision = FromEmailNode.ResolveFlagDecision(
            new FromEmailNodeConfiguration
            {
                ServerConfiguration = "TestServer",
                SuccessPath = SuccessPath,
                DeleteAfterProcessing = true
            }, confirmed);

        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void ARunThatWroteTheFlag_IsConfirmed()
    {
        var result = PipelineResult(""", "importCompleted": true""");

        Assert.Equal(MailRunConfirmation.Confirmed,
            MailSuccessPath.Evaluate(SuccessPath, result));
    }

    // ---- Only a real boolean true confirms ------------------------------------------

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("[true]")]
    public void OnlyTheBooleanTrueConfirms(string json)
    {
        // Every one of these fails towards leaving the mail alone, which is the direction a
        // confirmation must fail in. A stringly "true" is the likeliest near-miss — it is what a
        // SetPrimitiveValue@1 with valueType: String produces.
        var result = PipelineResult($""", "importCompleted": {json}""");

        Assert.Equal(MailRunConfirmation.NotConfirmed,
            MailSuccessPath.Evaluate(SuccessPath, result));
    }

    [Fact]
    public void AMissingOrNullResultIsNotConfirmed()
    {
        Assert.Equal(MailRunConfirmation.NotConfirmed,
            MailSuccessPath.Evaluate(SuccessPath, null));
    }

    [Fact]
    public void ANestedConfirmationIsRead()
    {
        var result = PipelineResult(""", "stageOut": { "ok": true }""");

        Assert.Equal(MailRunConfirmation.Confirmed,
            MailSuccessPath.Evaluate("$.stageOut.ok", result));
        // A path whose intermediate segment is missing must not throw its way out of the poll.
        Assert.Equal(MailRunConfirmation.NotConfirmed,
            MailSuccessPath.Evaluate("$.missing.ok", result));
    }

    // ---- No confirmation configured -------------------------------------------------

    [Fact]
    public void WithoutASuccessPath_TheOutcomeIsSimplyUnknown()
    {
        var result = PipelineResult(""", "importCompleted": true""");

        Assert.Equal(MailRunConfirmation.NotConfigured,
            MailSuccessPath.Evaluate(null, result));
        Assert.Equal(MailRunConfirmation.NotConfigured,
            MailSuccessPath.Evaluate("   ", result));
    }

    [Fact]
    public void WithoutASuccessPath_ACleanRunFlagsExactlyAsItDidBefore()
    {
        // Level 2 of the rule. A stricter default would stop every deployed FromEmail@1 from
        // marking anything read and re-offer its whole SINCE window on every adapter restart — a
        // certain fleet-wide regression traded for one edge case, so an unconfigured node keeps
        // doing what it always did.
        var confirmation = MailSuccessPath.Evaluate(null, PipelineResult());

        Assert.True(MailSuccessPath.IsPostProcessingAllowed(confirmation));
        Assert.Equal(MessageFlags.Seen,
            FromEmailNode.ResolveFlagDecision(
                    new FromEmailNodeConfiguration { ServerConfiguration = "TestServer" },
                    MailSuccessPath.IsPostProcessingAllowed(confirmation))
                .Flags);
    }

    [Fact]
    public void WithoutASuccessPath_ARunThatThrewStillFlagsNothing()
    {
        // Level 1, and the one the relaxed default must not weaken: a run that threw never reaches
        // the confirmation at all — the poll loop's gate is still its initial false when the
        // exception is caught — and ResolveFlagDecision obeys the gate without ever consulting
        // SuccessPath. So an unset path cannot turn a failed import into a flagged mailbox, which
        // is the AB#5337 gap itself.
        var unconfigured = new FromEmailNodeConfiguration
        {
            ServerConfiguration = "TestServer",
            SuccessPath = null,
            MarkAsRead = true,
            DeleteAfterProcessing = true
        };

        var decision = FromEmailNode.ResolveFlagDecision(unconfigured, writeBackAllowed: false);

        Assert.Equal(MessageFlags.None, decision.Flags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void OnlyAnExplicitlyUnconfirmedRunBlocksTheWriteBack()
    {
        // Levels 2 and 3 side by side: the strict promise applies exactly where somebody configured
        // it, and nowhere else. (Written as one Fact rather than a Theory because the enum is
        // internal and an InlineData parameter would have to be public.)
        Assert.True(MailSuccessPath.IsPostProcessingAllowed(MailRunConfirmation.Confirmed));
        Assert.True(MailSuccessPath.IsPostProcessingAllowed(MailRunConfirmation.NotConfigured));
        Assert.False(MailSuccessPath.IsPostProcessingAllowed(MailRunConfirmation.NotConfirmed));
    }

    // ---- The path syntax ------------------------------------------------------------

    [Theory]
    [InlineData("$.importCompleted", "importCompleted")]
    [InlineData("importCompleted", "importCompleted")]
    [InlineData("$.stageOut.ok", "stageOut|ok")]
    public void APlainPathIsAccepted(string path, string expected)
    {
        Assert.True(MailSuccessPath.TryParse(path, out var segments));
        Assert.Equal(expected, string.Join('|', segments));
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$..ok")]
    [InlineData("$.results[0].ok")]
    [InlineData("$.results[*].ok")]
    [InlineData("$.results[?(@.ok)]")]
    [InlineData("$.")]
    [InlineData("")]
    [InlineData(null)]
    public void APathThatCanSelectMoreThanOneValueIsRejected(string? path)
    {
        // A confirmation matching a set has no single truth value, and quietly picking one would be
        // the kind of guess this fix exists to remove. StartAsync turns the rejection into a
        // configuration error, so the author hears it at deploy time.
        Assert.False(MailSuccessPath.TryParse(path, out _));
    }
}
