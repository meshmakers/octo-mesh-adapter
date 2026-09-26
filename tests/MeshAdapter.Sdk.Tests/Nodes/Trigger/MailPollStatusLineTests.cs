using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5385: the status line both mail triggers report after every poll. Its shape is a contract
/// with the import card (leading UTC timestamp, <c>ERROR </c> prefix on a failed poll, " · "
/// separators), so the format is pinned here.
/// </summary>
public class MailPollStatusLineTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 17, 40, 12, DateTimeKind.Utc);

    [Fact]
    public void Success_HasTimestampMailboxFolderAndCounts()
    {
        var line = MailPollStatusLine.Success(Now, "kbernkopf@tecob.at", "Inbox/Eingangsrechnungen",
            new MailPollCounts { Seen = 12, Imported = 10, Failed = 1, Skipped = 1 });

        Assert.Equal(
            "2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox/Eingangsrechnungen · seen 12, imported 10, failed 1, skipped 1",
            line);
    }

    [Fact]
    public void Success_NamesTheBacklogOnlyWhenThereIsOne()
    {
        var withBacklog = MailPollStatusLine.Success(Now, "user", "INBOX",
            new MailPollCounts { Seen = 25, Imported = 25, Backlog = 40 });
        var without = MailPollStatusLine.Success(Now, "user", "INBOX", new MailPollCounts { Seen = 3, Imported = 3 });

        Assert.EndsWith("seen 25, imported 25, failed 0, skipped 0, 40 left for the next poll", withBacklog);
        Assert.EndsWith("seen 3, imported 3, failed 0, skipped 0", without);
    }

    [Fact]
    public void Success_LocalTimeIsWrittenAsUtc()
    {
        var local = new DateTime(2026, 9, 26, 19, 40, 12, DateTimeKind.Local);

        var line = MailPollStatusLine.Success(local, "m", "f", new MailPollCounts());

        Assert.StartsWith(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + " · ", line);
    }

    [Fact]
    public void Error_StartsWithTheErrorPrefixAndCarriesTheMessageVerbatim()
    {
        var line = MailPollStatusLine.Error(Now, "kbernkopf@tecob.at", "Inbox.02_Steuern",
            "Mail folder 'Inbox.02_Steuern' (path 'Inbox.02_Steuern') not found in mailbox 'kbernkopf@tecob.at'; available: Inbox, 02_Steuern / Finanzen");

        Assert.Equal(
            "ERROR 2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox.02_Steuern · Mail folder 'Inbox.02_Steuern' (path 'Inbox.02_Steuern') not found in mailbox 'kbernkopf@tecob.at'; available: Inbox, 02_Steuern / Finanzen",
            line);
        Assert.StartsWith(MailPollStatusLine.ErrorPrefix, line);
    }

    [Fact]
    public void Error_CollapsesLineBreaksSoTheLineStaysOneLine()
    {
        var line = MailPollStatusLine.Error(Now, "m", "f", "first line\r\n  second\tline\n");

        Assert.Equal("ERROR 2026-09-26T17:40:12Z · m · f · first line second line", line);
    }

    [Fact]
    public void Error_WithoutMessage_SaysSo()
    {
        Assert.EndsWith(" · (no message)", MailPollStatusLine.Error(Now, "m", "f", null));
        Assert.EndsWith(" · (no message)", MailPollStatusLine.Error(Now, "m", "f", "   "));
    }

    [Fact]
    public void UnknownMailboxOrFolder_IsAQuestionMark()
    {
        var line = MailPollStatusLine.Success(Now, null, "  ", new MailPollCounts());

        Assert.StartsWith("2026-09-26T17:40:12Z · ? · ? · seen 0", line);
    }

    [Fact]
    public void Info_IsATimestampedLineWithoutCounts()
    {
        var line = MailPollStatusLine.Info(Now, "user", "INBOX", "import window closed, nothing fetched");

        Assert.Equal("2026-09-26T17:40:12Z · user · INBOX · import window closed, nothing fetched", line);
    }

    [Fact]
    public void LongLine_IsCutAtTheTailAndMarked()
    {
        var available = string.Join(", ", Enumerable.Range(1, 200).Select(i => $"Folder{i}"));

        var line = MailPollStatusLine.Error(Now, "kbernkopf@tecob.at", "Inbox.02_Steuern",
            $"Mail folder 'Inbox.02_Steuern' not found; available: {available}");

        Assert.Equal(MailPollStatusLine.MaxLength, line.Length);
        Assert.EndsWith("…", line);
        // The head — timestamp, mailbox, folder and the start of the message — survives the cut.
        Assert.StartsWith("ERROR 2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox.02_Steuern · Mail folder 'Inbox.02_Steuern' not found", line);
    }

    [Fact]
    public void LineAtTheCap_IsNotTouched()
    {
        var line = new string('x', MailPollStatusLine.MaxLength);

        Assert.Same(line, MailPollStatusLine.Truncate(line));
    }
}
