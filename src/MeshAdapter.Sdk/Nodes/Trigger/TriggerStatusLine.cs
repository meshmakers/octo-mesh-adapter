using System.Text;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     The status line a non-mail trigger (<c>FromSignal@1</c>, <c>FromTeamsBot@1</c>) reports through
///     <see cref="ITriggerContext.ReportStatusAsync" /> (AB#5619 / AB#5620). Same contract as
///     <see cref="MailPollStatusLine" />, which the accounting app's cards already parse: a leading
///     ISO-8601 UTC timestamp — or <see cref="MailPollStatusLine.ErrorPrefix" /> followed by one —
///     then the channel's subject (the bridge number, the messaging route) and the text,
///     <c>" · "</c>-separated, whitespace collapsed, capped at <see cref="MailPollStatusLine.MaxLength" />.
///     Never credentials, never message bodies.
/// </summary>
/// <example>
///     <c>2026-10-07T04:20:00Z · +4366012345678 · received 2, processed 2, rejected 0</c><br />
///     <c>ERROR 2026-10-07T04:20:00Z · +4366012345678 · Connection refused (bridge.signal:8080)</c>
/// </example>
internal static class TriggerStatusLine
{
    private const string Separator = " · ";
    private const string Unknown = "?";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>A line for something that went right (or a heartbeat that nothing went wrong).</summary>
    public static string Success(DateTime utcNow, string? subject, string text)
    {
        return Build(null, utcNow, subject, text);
    }

    /// <summary>A line for a failure; <paramref name="errorMessage" /> is carried verbatim (collapsed, cut).</summary>
    public static string Error(DateTime utcNow, string? subject, string? errorMessage)
    {
        return Build(MailPollStatusLine.ErrorPrefix, utcNow, subject,
            string.IsNullOrWhiteSpace(errorMessage) ? "(no message)" : errorMessage);
    }

    /// <summary>The counts part of a Signal receive line.</summary>
    public static string SignalCounts(int received, int processed, int rejected)
    {
        return $"received {received}, processed {processed}, rejected {rejected}";
    }

    private static string Build(string? prefix, DateTime utcNow, string? subject, string text)
    {
        var line = new StringBuilder()
            .Append(prefix)
            .Append(utcNow.ToUniversalTime().ToString(TimestampFormat))
            .Append(Separator)
            .Append(string.IsNullOrWhiteSpace(subject) ? Unknown : Whitespace.Replace(subject, " ").Trim())
            .Append(Separator)
            .Append(Whitespace.Replace(text, " ").Trim())
            .ToString();

        return MailPollStatusLine.Truncate(line);
    }
}
