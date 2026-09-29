using FakeItEasy;
using MailKit;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5345. Setting a setting must never rewrite a pipeline definition: the definition is release
/// content — what a redeploy reinstates — while a poll interval or an "only unread" switch is
/// operating state. The accounting app used to patch the YAML with a regex, which moved one
/// tenant's pipeline entity from v35 to v39 with nothing but a checkbox and would have lost every
/// one of those edits on the next blueprint apply.
///
/// So <c>FromEmail@1</c> reads its runtime settings from a configuration entity, by the same rules
/// and through the same reader as the Microsoft Graph channel. These tests pin the rule that makes
/// that safe to roll out: <b>a value found in the settings wins, the node property is the
/// fallback</b> — which is at the same time the migration path, because a pipeline whose settings
/// entity says nothing behaves exactly as it did before.
/// </summary>
public class FromEmailNodeSettingsResolutionTests
{
    private const string SettingsName = "ImapImportSettings";

    private static IGlobalConfiguration GlobalConfigWith(string? rawJson)
    {
        var g = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => g.IsDefined(SettingsName)).Returns(rawJson != null);
        if (rawJson != null)
        {
            A.CallTo(() => g.GetRawJson(SettingsName)).Returns(rawJson);
        }

        return g;
    }

    /// <summary>
    /// A definition carrying the values the seed shipped with, plus the attribute names the node
    /// reads. The point of every test below is which of the two sides wins.
    /// </summary>
    private static FromEmailNodeConfiguration DefinitionConfig() => new()
    {
        ServerConfiguration = "EmailAccountingAssistant",
        PollingIntervalSeconds = 60,
        OnlyUnread = true,
        MarkAsRead = true,
        DeleteAfterProcessing = false,
        MaxMessagesPerPoll = 25,
        SuccessPath = "$.importCompleted",
        SettingsConfiguration = SettingsName,
        PollingSecondsAttribute = "EmailImportPollingSeconds",
        OnlyUnreadAttribute = "EmailImportOnlyUnread",
        PostProcessingModeAttribute = "EmailImportPostProcessingMode",
        SourceFolderAttribute = "EmailImportSourceFolder",
        DoneFolderAttribute = "EmailImportDoneFolder",
        FailedFolderAttribute = "EmailImportFailedFolder",
        MaxMessagesPerPollAttribute = "EmailImportMaxMessagesPerPoll",
        SinceDateAttribute = "EmailImportSinceDate",
        SinceDaysBackAttribute = "EmailImportSinceDaysBack",
        SenderFilterAttribute = "EmailImportSenderFilter",
        SubjectFilterAttribute = "EmailImportSubjectFilter",
        SuccessPathAttribute = "EmailImportSuccessPath",
    };

    // The runtime serializes the whole entity; its CK attributes are nested under "attributes".
    private const string FullSettingsJson =
        """
        {"rtId":"aa0000000000000000000332","ckTypeId":{"fullName":"Meshmakers.Accounting/EmailImportSettings-2"},
         "attributes":{"EmailImportPollingSeconds":300,"EmailImportOnlyUnread":false,
         "EmailImportPostProcessingMode":"MoveToFolders","EmailImportSourceFolder":"INBOX/ToDo",
         "EmailImportDoneFolder":"INBOX/Done","EmailImportFailedFolder":"INBOX/Failed",
         "EmailImportMaxMessagesPerPoll":5,"EmailImportSinceDaysBack":14,
         "EmailImportSenderFilter":"@vendor.com","EmailImportSubjectFilter":"Rechnung",
         "EmailImportSuccessPath":"$.imported"}}
        """;

    // ---- The settings win ------------------------------------------------------------

    [Fact]
    public void EverySettingConfiguredInTheEntity_OverridesTheDefinition()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(FullSettingsJson), DefinitionConfig());

        Assert.Equal(300, result.PollingIntervalSeconds);
        Assert.False(result.OnlyUnread);
        Assert.Equal(MailPostProcessingMode.MoveToFolders, result.PostProcessingMode);
        Assert.Equal("INBOX/ToDo", result.SourceFolder);
        Assert.Equal("INBOX/Done", result.DoneFolder);
        Assert.Equal("INBOX/Failed", result.FailedFolder);
        Assert.Equal(5, result.MaxMessagesPerPoll);
        Assert.Equal(14, result.SinceDaysBack);
        Assert.Equal("@vendor.com", result.SenderFilter);
        Assert.Equal("Rechnung", result.SubjectFilter);
        Assert.Equal("$.imported", result.SuccessPath);
    }

    [Fact]
    public void ADateCutOffInTheSettings_IsParsedCultureInvariantly()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith("""{"attributes":{"EmailImportSinceDate":"2026-03-02T00:00:00Z"}}"""),
            DefinitionConfig());

        // 2 March, never 3 February: a stored cut-off has to mean the same instant on every agent.
        Assert.Equal(new DateTime(2026, 3, 2), result.SinceDate!.Value.Date);
    }

    // ---- The migration path: nothing configured changes nothing -----------------------

    [Fact]
    public void NoSettingsConfiguration_LeavesTheDefinitionExactlyAsItIs()
    {
        var definition = DefinitionConfig() with { SettingsConfiguration = null };

        Assert.Same(definition,
            FromEmailNode.ResolveEffectiveConfiguration(GlobalConfigWith(null), definition));
    }

    [Fact]
    public void AMalformedSettingsPayload_LeavesTheDefinitionExactlyAsItIs()
    {
        // The settings entity is operator-facing data; an unreadable one must degrade to "the
        // pipeline keeps running as deployed", never to a half-applied configuration.
        var definition = DefinitionConfig();

        Assert.Same(definition,
            FromEmailNode.ResolveEffectiveConfiguration(GlobalConfigWith("{not json"), definition));
    }

    [Fact]
    public void AnEmptySettingsEntity_KeepsEveryDefinitionValue()
    {
        // The state a freshly seeded tenant is in, and the one that decides whether this change can
        // be rolled out to a fleet at all.
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith("""{"attributes":{}}"""), DefinitionConfig());

        Assert.Equal(60, result.PollingIntervalSeconds);
        Assert.True(result.OnlyUnread);
        Assert.Null(result.PostProcessingMode);
        Assert.Equal(25, result.MaxMessagesPerPoll);
        Assert.Equal("$.importCompleted", result.SuccessPath);
    }

    [Fact]
    public void ASettingsEntityWithNullAttributes_KeepsEveryDefinitionValue()
    {
        // An optional CK attribute nobody filled in is a PRESENT key carrying null, not a missing
        // one — the shape that has broken settings reads before.
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith(
                """
                {"attributes":{"EmailImportPollingSeconds":null,"EmailImportOnlyUnread":null,
                 "EmailImportPostProcessingMode":null,"EmailImportDoneFolder":null,
                 "EmailImportMaxMessagesPerPoll":null,"EmailImportSuccessPath":null}}
                """),
            DefinitionConfig());

        Assert.Equal(60, result.PollingIntervalSeconds);
        Assert.True(result.OnlyUnread);
        Assert.Null(result.PostProcessingMode);
        Assert.Null(result.DoneFolder);
        Assert.Equal(25, result.MaxMessagesPerPoll);
        Assert.Equal("$.importCompleted", result.SuccessPath);
    }

    // ---- The two readings that are easy to get wrong ----------------------------------

    [Fact]
    public void AConfiguredBatchCapOfZero_ReachesTheNodeAsZero()
    {
        // 0 is this property's documented "no limit" opt-out. Reading it as "unset" would quietly
        // re-apply the cap an operator deliberately removed — the mirror image of AB#5336, where
        // an unbounded fetch killed the adapter.
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith("""{"attributes":{"EmailImportMaxMessagesPerPoll":0}}"""),
            DefinitionConfig());

        Assert.Equal(0, result.MaxMessagesPerPoll);
        Assert.Equal(0, FromEmailNode.ResolveMaxMessagesPerPoll(result));
    }

    [Fact]
    public void APollIntervalOfZeroOrLess_IsNotConfiguredRatherThanAHotLoop()
    {
        foreach (var payload in new[] { "0", "-30" })
        {
            var result = FromEmailNode.ResolveEffectiveConfiguration(
                GlobalConfigWith($$$"""{"attributes":{"EmailImportPollingSeconds":{{{payload}}}}}"""),
                DefinitionConfig());

            Assert.Equal(60, result.PollingIntervalSeconds);
        }
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("\"Nonsense\"")]
    public void APostProcessingModeThatIsNotAKnownNAME_IsIgnored(string payload)
    {
        // Names only. A number there would select a mode by ordinal, and ordinals are exactly what
        // shifts when a member is inserted; an unknown name leaves the deployed behaviour in force
        // instead of guessing at one.
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith($$$"""{"attributes":{"EmailImportPostProcessingMode":{{{payload}}}}}"""),
            DefinitionConfig());

        Assert.Null(result.PostProcessingMode);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData(" None ")]
    public void AStoredNone_FailsSpeakingInsteadOfBeingReadAsNotConfigured(string stored)
    {
        // AB#5372. `None` is the one removed name that must NOT take the "unknown name means not
        // configured" route above: it WAS storable, so an entity out there may still carry it, and
        // degrading it to "not configured" hands the decision to the derivation — which on the
        // accounting seed (markAsRead: true) is MarkAsRead. The node would then start flagging mail
        // in a mailbox whose operator had asked it to change nothing. So it is heard, in StartAsync,
        // where the settings overlay is resolved before the poll loop exists.
        var ex = Assert.ThrowsAny<Exception>(() => FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith($$$"""{"attributes":{"EmailImportPostProcessingMode":"{{{stored}}}"}}"""),
            DefinitionConfig()));

        // The message has to say what happened, where, and what to put there instead.
        Assert.Contains("FromEmail@1", ex.Message);
        Assert.Contains("EmailImportPostProcessingMode", ex.Message);
        Assert.Contains("REMOVED (AB#5372)", ex.Message);
        Assert.Contains("the mailbox IS the bookkeeping", ex.Message);
        Assert.Contains("MoveToFolders, Delete or MarkAsRead", ex.Message);
        Assert.Contains("MarkAsRead is the lightest equivalent", ex.Message);
    }

    [Theory]
    [InlineData("MoveToFolders", MailPostProcessingMode.MoveToFolders)]
    [InlineData("Delete", MailPostProcessingMode.Delete)]
    [InlineData("MarkAsRead", MailPostProcessingMode.MarkAsRead)]
    public void TheThreeValidModes_StillResolveUnchanged(string stored, MailPostProcessingMode expected)
    {
        // The guard above must not cost the three names that are the point of the setting, and the
        // members keep their numbers — persistence is by NAME on both routes, so AB#5372 renumbered
        // nothing.
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith($$$"""{"attributes":{"EmailImportPostProcessingMode":"{{{stored}}}"}}"""),
            DefinitionConfig());

        Assert.Equal(expected, result.PostProcessingMode);
        Assert.Equal(expected, FromEmailNode.ResolveEffectivePostProcessingMode(result));
    }

    [Fact]
    public void AModeNameIsReadCaseInsensitively()
    {
        var result = FromEmailNode.ResolveEffectiveConfiguration(
            GlobalConfigWith("""{"attributes":{"EmailImportPostProcessingMode":"markasread"}}"""),
            DefinitionConfig());

        Assert.Equal(MailPostProcessingMode.MarkAsRead, result.PostProcessingMode);
    }

    // ---- The polled folder ------------------------------------------------------------

    [Fact]
    public void WithoutASourceFolder_TheConnectionEntitysFolderIsPolled()
    {
        // host/port/user/password/folder have always lived on the EMailReceiverConfiguration the
        // definition merely POINTS at; a settings entity that says nothing must not move that.
        Assert.Equal("INBOX",
            FromEmailNode.ResolveSourceFolderName(DefinitionConfig(), "INBOX"));
    }

    [Fact]
    public void AConfiguredSourceFolder_WinsOverTheConnectionEntity()
    {
        Assert.Equal("INBOX/ToDo",
            FromEmailNode.ResolveSourceFolderName(
                DefinitionConfig() with { SourceFolder = "INBOX/ToDo" }, "INBOX"));
    }

    // ---- Folder paths ------------------------------------------------------------------

    [Fact]
    public void AFolderPathIsSplitOnSlashesOnly()
    {
        // '/' is the ONE separator an operator types; which delimiter the server itself uses
        // (Dovecot commonly '.') is MailKit's business during the walk, not the operator's.
        Assert.Equal(["Archive", "Invoices", "Done"],
            FromEmailNode.SplitFolderPath("Archive/Invoices/Done"));
    }

    [Fact]
    public void AFolderPathIgnoresEmptySegmentsAndSurroundingSpace()
    {
        Assert.Equal(["Archive", "Done"], FromEmailNode.SplitFolderPath("/Archive/ Done /"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyFolderPathYieldsNoSegments(string? path)
    {
        Assert.Empty(FromEmailNode.SplitFolderPath(path));
    }
}

/// <summary>
/// AB#5345. Both mail channels offer the same three post-processing modes, and BOTH have to keep
/// every already deployed pipeline behaving exactly as it did. That is what the derivation below
/// buys: a pipeline that names no mode gets the one its old properties describe.
/// </summary>
public class MailPostProcessingModeDerivationTests
{
    private static FromEmailNodeConfiguration Imap(
        bool markAsRead = true, bool deleteAfterProcessing = false,
        string? doneFolder = null, MailPostProcessingMode? mode = null) => new()
    {
        ServerConfiguration = "TestServer",
        MarkAsRead = markAsRead,
        DeleteAfterProcessing = deleteAfterProcessing,
        DoneFolder = doneFolder,
        PostProcessingMode = mode,
    };

    private const string GraphSettingsName = "GraphImportSettings";

    private static IGlobalConfiguration GraphGlobalConfigWith(string rawJson)
    {
        var g = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => g.IsDefined(GraphSettingsName)).Returns(true);
        A.CallTo(() => g.GetRawJson(GraphSettingsName)).Returns(rawJson);
        return g;
    }

    private static FromMicrosoftGraphEmailNodeConfiguration Graph(
        string? doneFolder = null, string? failedFolder = null,
        MailPostProcessingMode? mode = null) => new()
    {
        ServerConfiguration = "graph",
        Mailbox = "box@example.com",
        FolderPath = "Archive/ToDo",
        MoveToFolderPathOnSuccess = doneFolder,
        MoveToFolderPathOnFailure = failedFolder,
        PostProcessingMode = mode,
    };

    // ---- IMAP: derived from the two legacy flags ---------------------------------------

    [Fact]
    public void ImapDefaults_DeriveMarkAsRead()
    {
        // markAsRead defaults to true and the accounting import runs with exactly these defaults.
        Assert.Equal(MailPostProcessingMode.MarkAsRead,
            FromEmailNode.ResolveEffectivePostProcessingMode(Imap()));
    }

    [Fact]
    public void ImapDeleteWinsOverMarkAsRead()
    {
        // An expunged message cannot meaningfully also be "read".
        Assert.Equal(MailPostProcessingMode.Delete,
            FromEmailNode.ResolveEffectivePostProcessingMode(
                Imap(markAsRead: true, deleteAfterProcessing: true)));
    }

    [Fact]
    public void ImapWithNeitherFlagNorFolder_FailsSpeakingInsteadOfLeavingTheMailboxAlone()
    {
        // AB#5372, and this is the reversal: this used to derive `None` — "leave the mailbox alone",
        // which reads like a legitimate read-only poll and is not one. The mailbox is the trigger's
        // ONLY record of what it already imported, so a pass that changes nothing hands the next pass
        // the same maxMessagesPerPoll messages and never reaches the mail behind them: the import
        // runs for ever, reports success and imports nothing new (AB#5336 — 1697 mails, three pod
        // restarts, no progress).
        var ex = Assert.ThrowsAny<Exception>(
            () => FromEmailNode.ResolveEffectivePostProcessingMode(Imap(markAsRead: false)));

        Assert.Contains("FromEmail@1", ex.Message);
        Assert.Contains("no post-processing mode is configured and none can be derived", ex.Message);
        // The reason, so an operator learns WHY there is no such mode ...
        Assert.Contains("the mailbox IS the bookkeeping", ex.Message);
        Assert.Contains("imports nothing new", ex.Message);
        // ... and the three valid modes plus the legacy properties that still derive them.
        Assert.Contains("MoveToFolders, Delete or MarkAsRead", ex.Message);
        Assert.Contains("deleteAfterProcessing", ex.Message);
    }

    [Fact]
    public void ImapWithAFolderConfigured_DerivesMoveToFolders()
    {
        Assert.Equal(MailPostProcessingMode.MoveToFolders,
            FromEmailNode.ResolveEffectivePostProcessingMode(Imap(doneFolder: "INBOX/Done")));
    }

    [Fact]
    public void AnExplicitImapMode_WinsOverEverythingDerived()
    {
        Assert.Equal(MailPostProcessingMode.Delete,
            FromEmailNode.ResolveEffectivePostProcessingMode(
                Imap(markAsRead: true, doneFolder: "INBOX/Done", mode: MailPostProcessingMode.Delete)));
    }

    // ---- IMAP: what each explicit mode writes back --------------------------------------

    [Fact]
    public void ExplicitMarkAsRead_WritesSeenOnly()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Imap(deleteAfterProcessing: true, mode: MailPostProcessingMode.MarkAsRead),
            writeBackAllowed: true);

        Assert.Equal(MessageFlags.Seen, decision.Flags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void ExplicitDelete_WritesDeletedAndExpunges()
    {
        var decision = FromEmailNode.ResolveFlagDecision(
            Imap(markAsRead: true, mode: MailPostProcessingMode.Delete), writeBackAllowed: true);

        Assert.Equal(MessageFlags.Deleted, decision.Flags);
        Assert.True(decision.Expunge);
    }

    [Fact]
    public void ExplicitMoveToFolders_WritesNoFlagsAtAll()
    {
        // The move is the whole post-processing; a flag written on top of it would be a second,
        // unasked-for change to the operator's mailbox.
        var decision = FromEmailNode.ResolveFlagDecision(
            Imap(markAsRead: true, deleteAfterProcessing: true,
                mode: MailPostProcessingMode.MoveToFolders), writeBackAllowed: true);

        Assert.False(decision.HasFlags);
        Assert.False(decision.Expunge);
    }

    [Fact]
    public void EveryModeTheEnumStillHas_ChangesTheMailbox()
    {
        // AB#5372 in one assertion: there is no longer a mode under which a confirmed import leaves
        // the message exactly as the server has it. MoveToFolders writes no FLAGS (the move is the
        // whole post-processing, see above), so the check is per mode rather than "some flag".
        foreach (var mode in Enum.GetValues<MailPostProcessingMode>())
        {
            var decision = FromEmailNode.ResolveFlagDecision(
                Imap(markAsRead: true, deleteAfterProcessing: true, doneFolder: "INBOX/Done",
                    mode: mode), writeBackAllowed: true);

            if (mode == MailPostProcessingMode.MoveToFolders)
            {
                Assert.False(decision.HasFlags);
                continue;
            }

            Assert.True(decision.HasFlags);
        }
    }

    [Fact]
    public void AnUnconfirmedRun_WritesNothingWhateverTheModeSays()
    {
        foreach (var mode in Enum.GetValues<MailPostProcessingMode>())
        {
            var decision = FromEmailNode.ResolveFlagDecision(
                Imap(markAsRead: true, deleteAfterProcessing: true, mode: mode),
                writeBackAllowed: false);

            Assert.False(decision.HasFlags);
            Assert.False(decision.Expunge);
        }
    }

    // ---- Graph: derived from the folder properties --------------------------------------

    [Fact]
    public void GraphWithoutFolders_FailsSpeakingInsteadOfLeavingTheMessageInTheSourceFolder()
    {
        // AB#5372. The Graph resolver's fall-through was `None` — "leave it in the source folder" —
        // and that is the same non-queue as on IMAP: the message is handed back to the next poll for
        // ever while the mail behind the cap is never reached. No live M365 tenant sits here (the
        // settings page will not activate a channel without mailbox, source and done folder), but a
        // hand-made pipeline could, and it would have failed silently instead of on deploy.
        var ex = Assert.ThrowsAny<Exception>(
            () => FromMicrosoftGraphEmailNode.ResolveEffectivePostProcessingMode(Graph()));

        Assert.Contains("FromMicrosoftGraphEmail@1", ex.Message);
        Assert.Contains("no post-processing mode is configured and none can be derived", ex.Message);
        Assert.Contains("the mailbox IS the bookkeeping", ex.Message);
        Assert.Contains("MoveToFolders, Delete or MarkAsRead", ex.Message);
        // This channel has no legacy flags — the only derivation is from a folder path.
        Assert.Contains("moveToFolderPathOnSuccess", ex.Message);
    }

    [Fact]
    public void AStoredNoneOnTheGraphChannel_FailsSpeakingToo()
    {
        // The guard sits in the shared settings reader, so both channels answer the same way — and
        // the suggestion is the channel's own: MoveToFolders is the only Graph mode with somewhere to
        // park a message whose attempts are exhausted.
        var definition = Graph("Archive/Done") with
        {
            SettingsConfiguration = GraphSettingsName,
            PostProcessingModeAttribute = "EmailImportPostProcessingMode",
        };

        var ex = Assert.ThrowsAny<Exception>(
            () => FromMicrosoftGraphEmailNode.ResolveEffectiveConfiguration(
                GraphGlobalConfigWith(
                    """{"attributes":{"EmailImportPostProcessingMode":"None"}}"""),
                definition));

        Assert.Contains("FromMicrosoftGraphEmail@1", ex.Message);
        Assert.Contains("REMOVED (AB#5372)", ex.Message);
        Assert.Contains("MoveToFolders is what this channel has always done", ex.Message);
    }

    [Theory]
    [InlineData("Archive/Done", null)]
    [InlineData(null, "Archive/Failed")]
    [InlineData("Archive/Done", "Archive/Failed")]
    public void GraphWithAnyFolderConfigured_DerivesMoveToFolders(string? done, string? failed)
    {
        Assert.Equal(MailPostProcessingMode.MoveToFolders,
            FromMicrosoftGraphEmailNode.ResolveEffectivePostProcessingMode(Graph(done, failed)));
    }

    [Fact]
    public void AnExplicitGraphMode_WinsOverTheConfiguredFolders()
    {
        Assert.Equal(MailPostProcessingMode.MarkAsRead,
            FromMicrosoftGraphEmailNode.ResolveEffectivePostProcessingMode(
                Graph("Archive/Done", "Archive/Failed", MailPostProcessingMode.MarkAsRead)));
    }
}
