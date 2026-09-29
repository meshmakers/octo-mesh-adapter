using System.Text;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     What one poll of a mail trigger did, as the trigger itself can count it (AB#5385). The
///     sender allowlist runs INSIDE the pipeline, so "rejected by the allowlist" is not something a
///     trigger can know; what it knows is what it fetched, what it handed to the pipeline and what
///     came back.
/// </summary>
internal sealed class MailPollCounts
{
    /// <summary>Messages the poll fetched from the source folder (after the server-side filters).</summary>
    public int Seen;

    /// <summary>
    ///     Messages whose pipeline run confirmed the import — or completed, where no
    ///     <c>successPath</c> is configured and the trigger cannot tell the two apart.
    /// </summary>
    public int Imported;

    /// <summary>Messages handed to the pipeline whose run threw or did not confirm the import.</summary>
    public int Failed;

    /// <summary>
    ///     Messages never handed to the pipeline: client-side sender/subject filter, caller binding
    ///     rejected, attempt budget exhausted / parked.
    /// </summary>
    public int Skipped;

    /// <summary>Messages the batch cap left for the next poll (IMAP only; 0 when unknown).</summary>
    public int Backlog;
}

/// <summary>
///     Builds the ONE status line a mail trigger reports through
///     <see cref="ITriggerContext.ReportStatusAsync" /> after every poll and from its failure path
///     (AB#5385), shared by <c>FromEmail@1</c> and <c>FromMicrosoftGraphEmail@1</c>. The line is what
///     the import card shows, so its shape is a contract with the app: it starts with an ISO-8601
///     UTC timestamp — or with <see cref="ErrorPrefix" /> followed by one — so the card can compute
///     the age of the last poll and pick the error style; then the mailbox and the effective source
///     folder, then the counts or the error text, <c>" · "</c>-separated.
/// </summary>
/// <example>
///     <c>2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox/Eingangsrechnungen · seen 12, imported 12, failed 0, skipped 0</c><br />
///     <c>ERROR 2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox.02_Steuern · Mail folder 'Inbox.02_Steuern' (path '…') not found in mailbox …; available: …</c>
/// </example>
internal static class MailPollStatusLine
{
    /// <summary>The line is capped here, tail first; the controller caps again at 1000.</summary>
    public const int MaxLength = 500;

    /// <summary>What a failed poll's line starts with — the app's error-style trigger.</summary>
    public const string ErrorPrefix = "ERROR ";

    private const string Separator = " · ";
    private const string Unknown = "?";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The line for a poll that completed, with what it counted.</summary>
    public static string Success(DateTime utcNow, string? mailbox, string? folder, MailPollCounts counts)
    {
        var text = new StringBuilder()
            .Append("seen ").Append(counts.Seen)
            .Append(", imported ").Append(counts.Imported)
            .Append(", failed ").Append(counts.Failed)
            .Append(", skipped ").Append(counts.Skipped);
        if (counts.Backlog > 0)
        {
            text.Append(", ").Append(counts.Backlog).Append(" left for the next poll");
        }

        return Build(null, utcNow, mailbox, folder, text.ToString());
    }

    /// <summary>The line for a poll that ran but fetched nothing on purpose (e.g. the import window is closed).</summary>
    public static string Info(DateTime utcNow, string? mailbox, string? folder, string text)
    {
        return Build(null, utcNow, mailbox, folder, text);
    }

    /// <summary>
    ///     The line for a poll that threw. <paramref name="errorMessage" /> is the exception's
    ///     message verbatim — the folder-not-found text lists the available folders, which is
    ///     exactly what the operator needs — with line breaks collapsed and the tail cut.
    /// </summary>
    public static string Error(DateTime utcNow, string? mailbox, string? folder, string? errorMessage)
    {
        return Build(ErrorPrefix, utcNow, mailbox, folder,
            string.IsNullOrWhiteSpace(errorMessage) ? "(no message)" : errorMessage);
    }

    private static string Build(string? prefix, DateTime utcNow, string? mailbox, string? folder, string text)
    {
        var line = new StringBuilder()
            .Append(prefix)
            .Append(utcNow.ToUniversalTime().ToString(TimestampFormat))
            .Append(Separator).Append(Clean(mailbox))
            .Append(Separator).Append(Clean(folder))
            .Append(Separator).Append(Whitespace.Replace(text, " ").Trim())
            .ToString();

        return Truncate(line);
    }

    private static string Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? Unknown : Whitespace.Replace(value, " ").Trim();
    }

    /// <summary>Cuts the tail of an over-long line and marks the cut.</summary>
    internal static string Truncate(string line)
    {
        return line.Length <= MaxLength ? line : string.Concat(line.AsSpan(0, MaxLength - 1), "…");
    }
}
