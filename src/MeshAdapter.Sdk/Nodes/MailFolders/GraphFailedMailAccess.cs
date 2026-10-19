using System.Net;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     The Microsoft 365 half of listing and re-queueing failed import mails (AB#7087), on top of
///     <see cref="GraphMailboxAccess" />. The markers are NOT redefined here: the prefix, the parked
///     marker and the "only well-formed markers, never a user's own category" rule are
///     <see cref="FromMicrosoftGraphEmailNode" />'s, called directly, so the trigger that writes the
///     markers and the re-queue that removes them can never disagree about what a marker is.
/// </summary>
internal static class GraphFailedMailAccess
{
    private const int PageSize = 100;

    /// <summary>
    ///     Resolves a folder path to its id the way <c>FromMicrosoftGraphEmail@1</c> does — segments from
    ///     <see cref="MailFolderPathSyntax.SplitGraphPath" />, the first one also tried as a well-known
    ///     name — but NEVER creates anything. Null when a segment is missing.
    /// </summary>
    internal static async Task<string?> ResolveFolderIdAsync(HttpClient client, string mailbox, string folderPath,
        CancellationToken cancellationToken)
    {
        var segments = MailFolderPathSyntax.SplitGraphPath(folderPath);
        if (segments.Count == 0)
        {
            return null;
        }

        var user = Uri.EscapeDataString(mailbox);
        string? parentId = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var escaped = segments[i].Replace("'", "''");
            var filter = $"$filter=displayName eq '{Uri.EscapeDataString(escaped)}'&$select=id";
            var url = parentId == null
                ? $"{GraphMailboxAccess.GraphBaseUrl}/users/{user}/mailFolders?{filter}"
                : $"{GraphMailboxAccess.GraphBaseUrl}/users/{user}/mailFolders/{Uri.EscapeDataString(parentId)}/childFolders?{filter}";

            var (status, body) = await SendAsync(client, HttpMethod.Get, url, null, cancellationToken);
            if (!IsSuccess(status))
            {
                throw new MailFolderListingException(GraphMailboxAccess.DescribeGraphFailure((int)status, body, mailbox));
            }

            string? folderId = FirstId(body);
            if (folderId == null && parentId == null)
            {
                // The first segment may be a well-known name (archive, inbox, …) whose display name
                // is localized per mailbox language.
                var wellKnown = $"{GraphMailboxAccess.GraphBaseUrl}/users/{user}/mailFolders/" +
                                $"{Uri.EscapeDataString(segments[i].ToLowerInvariant().Replace(" ", ""))}?$select=id";
                var (wkStatus, wkBody) = await SendAsync(client, HttpMethod.Get, wellKnown, null, cancellationToken);
                if (IsSuccess(wkStatus))
                {
                    using var doc = JsonDocument.Parse(wkBody);
                    folderId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
                }
            }

            if (string.IsNullOrEmpty(folderId))
            {
                return null;
            }

            parentId = folderId;
        }

        return parentId;
    }

    /// <summary>The mails of a folder, newest first, at most <paramref name="max" />.</summary>
    internal static async Task<FailedMailListing> ListAsync(HttpClient client, string mailbox, string folderId,
        int max, CancellationToken cancellationToken)
    {
        var user = Uri.EscapeDataString(mailbox);
        string? next = $"{GraphMailboxAccess.GraphBaseUrl}/users/{user}/mailFolders/{Uri.EscapeDataString(folderId)}/messages" +
                       $"?$top={Math.Min(PageSize, max + 1)}&$orderby=receivedDateTime desc" +
                       "&$select=id,subject,from,receivedDateTime,categories";
        var mails = new List<FailedMail>();
        var truncated = false;
        while (next != null && !truncated)
        {
            var (status, body) = await SendAsync(client, HttpMethod.Get, next, null, cancellationToken);
            if (!IsSuccess(status))
            {
                throw new MailFolderListingException(GraphMailboxAccess.DescribeGraphFailure((int)status, body, mailbox));
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in value.EnumerateArray())
                {
                    if (mails.Count >= max)
                    {
                        truncated = true;
                        break;
                    }

                    var mail = ToFailedMail(message);
                    if (mail != null)
                    {
                        mails.Add(mail);
                    }
                }
            }

            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var link) &&
                   link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
        }

        return new FailedMailListing(mails, truncated);
    }

    internal static FailedMail? ToFailedMail(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var categories = GetCategories(message);
        var attempts = FromMicrosoftGraphEmailNode.GetAttemptCount(categories);
        DateTimeOffset? received = message.TryGetProperty("receivedDateTime", out var dt) &&
                                   dt.ValueKind == JsonValueKind.String && dt.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
        return new FailedMail(
            id,
            message.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
            FromAddress(message),
            received,
            attempts > 0 ? attempts : null,
            FromMicrosoftGraphEmailNode.HasFailedCategory(categories),
            null);
    }

    /// <summary>
    ///     Re-queues one mail: clear the import markers, THEN move. A mail moved back with its markers
    ///     still on is parked again by the next poll (AB#7068), so a failed clear means no move.
    ///     A mail-level problem comes back in the outcome; an unreachable Graph throws
    ///     <see cref="MailFolderListingException" />, which the node turns into that mail's failure.
    /// </summary>
    internal static async Task<RequeueOutcome> RequeueAsync(HttpClient client, string mailbox,
        string failedFolderId, string sourceFolderId, string messageId, CancellationToken cancellationToken)
    {
        var user = Uri.EscapeDataString(mailbox);
        var messageUrl = $"{GraphMailboxAccess.GraphBaseUrl}/users/{user}/messages/{Uri.EscapeDataString(messageId)}";

        // 1. Is it (still) a mail of the failed folder? The id alone addresses a message anywhere in
        //    the mailbox, so the parent folder is checked — a caller must not be able to move an
        //    arbitrary mail out of the inbox through this route.
        var (getStatus, getBody) = await SendAsync(client, HttpMethod.Get,
            $"{messageUrl}?$select=id,subject,categories,parentFolderId", null, cancellationToken);
        if (getStatus == HttpStatusCode.NotFound)
        {
            return RequeueOutcome.Missing(messageId);
        }

        if (!IsSuccess(getStatus))
        {
            return RequeueOutcome.Error(messageId, null,
                "The mail could not be read, nothing was changed. " +
                GraphMailboxAccess.DescribeGraphFailure((int)getStatus, getBody, mailbox));
        }

        string? subject;
        IReadOnlyList<string> categories;
        string? parentFolderId;
        using (var doc = JsonDocument.Parse(getBody))
        {
            var root = doc.RootElement;
            subject = root.TryGetProperty("subject", out var s) ? s.GetString() : null;
            categories = GetCategories(root);
            parentFolderId = root.TryGetProperty("parentFolderId", out var p) ? p.GetString() : null;
        }

        if (!string.Equals(parentFolderId, failedFolderId, StringComparison.Ordinal))
        {
            return RequeueOutcome.Missing(messageId);
        }

        // 2. Clear the markers on the message's CURRENT categories (what we just read), keeping the
        //    user's own. Skipped when there is nothing to clear.
        var cleared = FromMicrosoftGraphEmailNode.WithoutImportMarkers(categories);
        if (cleared.Count != categories.Count)
        {
            var payload = JsonSerializer.Serialize(new { categories = cleared });
            var (patchStatus, patchBody) = await SendAsync(client, HttpMethod.Patch, messageUrl, payload, cancellationToken);
            if (!IsSuccess(patchStatus))
            {
                return RequeueOutcome.Error(messageId, subject,
                    "The import markers could not be cleared, so the mail was NOT moved (it would be parked " +
                    "again by the next poll). " + GraphMailboxAccess.DescribeGraphFailure((int)patchStatus, patchBody, mailbox));
            }
        }

        // 3. Move it back to the folder the import polls.
        var movePayload = JsonSerializer.Serialize(new { destinationId = sourceFolderId });
        var (moveStatus, moveBody) = await SendAsync(client, HttpMethod.Post, $"{messageUrl}/move", movePayload, cancellationToken);
        if (!IsSuccess(moveStatus))
        {
            return RequeueOutcome.Error(messageId, subject,
                "The import markers were cleared but moving the mail failed; it is still in the failed folder " +
                "and can be re-queued again. " + GraphMailboxAccess.DescribeGraphFailure((int)moveStatus, moveBody, mailbox));
        }

        return RequeueOutcome.Ok(messageId, subject);
    }

    private static IReadOnlyList<string> GetCategories(JsonElement message)
    {
        if (!message.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return categories.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString()!)
            .ToList();
    }

    private static string? FromAddress(JsonElement message)
    {
        if (message.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object &&
            from.TryGetProperty("emailAddress", out var email) && email.ValueKind == JsonValueKind.Object)
        {
            var address = email.TryGetProperty("address", out var a) ? a.GetString() : null;
            var name = email.TryGetProperty("name", out var n) ? n.GetString() : null;
            return string.IsNullOrWhiteSpace(name) || name == address ? address : $"{name} <{address}>";
        }

        return null;
    }

    private static string? FirstId(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                return item.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
        }

        return null;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method,
        string url, string? jsonBody, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new MailFolderListingException(
                $"Microsoft Graph is not reachable from the import service: {ex.Message}", ex);
        }
    }
}
