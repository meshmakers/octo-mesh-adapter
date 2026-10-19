using System.Globalization;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     The IMAP half of listing and re-queueing failed import mails (AB#7087). Works on
///     <see cref="IMailFolder" /> so the clear-before-move order and the flag rules are tested without
///     a mail server.
///     <para>
///     What a re-queue removes: <c>\Seen</c> and <c>\Deleted</c> — with <c>EmailImportOnlyUnread</c> a
///     read mail is never offered to the import again, and a deleted-flagged one disappears on the next
///     expunge — and every keyword that is a well-formed <c>OctoMesh-Import-*</c> marker, decided by
///     <see cref="FromMicrosoftGraphEmailNode.WithoutImportMarkers" />, the same rule the Graph side
///     uses. <c>FromEmail@1</c> writes no such keywords today; this is for mails that carry one anyway
///     (moved over from a Graph mailbox, or a later trigger version).
///     </para>
///     <para>
///     A mail's id is <c>UidValidity:Uid</c>. The UID alone means nothing after the server renumbered
///     the folder, so an id from before that is "not found" instead of silently addressing another mail.
///     </para>
/// </summary>
internal static class ImapFailedMailAccess
{
    private const MessageFlags FlagsToClear = MessageFlags.Seen | MessageFlags.Deleted;

    internal static string ToId(uint uidValidity, UniqueId uid) =>
        string.Create(CultureInfo.InvariantCulture, $"{uidValidity}:{uid.Id}");

    internal static bool TryParseId(string id, out uint uidValidity, out UniqueId uid)
    {
        uidValidity = 0;
        uid = UniqueId.Invalid;
        var parts = id.Split(':');
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uidValidity) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw == 0)
        {
            return false;
        }

        uid = new UniqueId(raw);
        return true;
    }

    /// <summary>
    ///     Resolves a folder path the way <c>FromEmail@1</c> does — verbatim first (a name that carries
    ///     the server's own delimiter), then walked by <c>/</c> segments from the personal namespace —
    ///     but never creates a folder. Null when it does not exist.
    /// </summary>
    internal static async Task<IMailFolder?> ResolveFolderAsync(ImapClient client, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFolderAsync(path, cancellationToken);
        }
        catch (FolderNotFoundException)
        {
            // Not there under that literal name — walk it.
        }

        var segments = FromEmailNode.SplitFolderPath(path);
        if (segments.Count == 0 || client.PersonalNamespaces.Count == 0)
        {
            return null;
        }

        var current = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (var segment in segments)
        {
            try
            {
                current = await current.GetSubfolderAsync(segment, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>The mails of the folder, newest first, at most <paramref name="max" />.</summary>
    internal static async Task<FailedMailListing> ListAsync(IMailFolder folder, int max,
        CancellationToken cancellationToken)
    {
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var uids = await folder.SearchAsync(SearchQuery.All, cancellationToken);
        var newest = uids.OrderByDescending(u => u.Id).Take(max + 1).ToList();
        var truncated = newest.Count > max;
        newest = newest.Take(max).ToList();
        if (newest.Count == 0)
        {
            return new FailedMailListing([], false);
        }

        var summaries = await folder.FetchAsync(newest,
            new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                             MessageSummaryItems.InternalDate),
            cancellationToken);
        var mails = summaries
            .OrderByDescending(s => s.UniqueId.Id)
            .Select(s => new FailedMail(
                ToId(folder.UidValidity, s.UniqueId),
                s.Envelope?.Subject,
                FromText(s),
                s.InternalDate ?? s.Envelope?.Date,
                null,
                true,
                null))
            .ToList();
        return new FailedMailListing(mails, truncated);
    }

    /// <summary>
    ///     Re-queues one mail: clear the flags and marker keywords in the failed folder, THEN move it to
    ///     <paramref name="source" />. If clearing fails the mail is not moved. Only cancellation escapes.
    /// </summary>
    internal static async Task<RequeueOutcome> RequeueAsync(IMailFolder failed, IMailFolder source, string id,
        CancellationToken cancellationToken)
    {
        if (!TryParseId(id, out var validity, out var uid) || validity != failed.UidValidity)
        {
            return RequeueOutcome.Missing(id);
        }

        var summaries = await failed.FetchAsync(new[] { uid },
            new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                             MessageSummaryItems.Flags),
            cancellationToken);
        var summary = summaries.FirstOrDefault(s => s.UniqueId == uid);
        if (summary == null)
        {
            return RequeueOutcome.Missing(id);
        }

        var subject = summary.Envelope?.Subject;
        try
        {
            var keywords = (summary.Keywords ?? new HashSet<string>()).ToList();
            var keep = FromMicrosoftGraphEmailNode.WithoutImportMarkers(keywords);
            var markers = new HashSet<string>(keywords.Except(keep), StringComparer.OrdinalIgnoreCase);

            // StoreAsync, not the RemoveFlagsAsync extension: the interface member is the one a test can fake.
            await failed.StoreAsync(uid,
                new StoreFlagsRequest(StoreAction.Remove, FlagsToClear, markers) { Silent = true },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RequeueOutcome.Error(id, subject,
                "The read/deleted flags and import markers could not be cleared, so the mail was NOT moved " +
                $"(it would not be offered to the import again): {ex.Message}");
        }

        try
        {
            await failed.MoveToAsync(uid, source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RequeueOutcome.Error(id, subject,
                "The flags were cleared but moving the mail failed; it is still in the failed folder " +
                $"and can be re-queued again: {ex.Message}");
        }

        return RequeueOutcome.Ok(id, subject);
    }

    private static string? FromText(IMessageSummary summary)
    {
        var from = summary.Envelope?.From?.Mailboxes.FirstOrDefault();
        if (from == null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>";
    }
}
