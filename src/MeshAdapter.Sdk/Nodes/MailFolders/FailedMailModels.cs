namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     One mail parked in a channel's failed folder, as <c>ListFailedImportMails@1</c> reports it
///     (AB#7087). <see cref="Attempts" /> is read from the <c>OctoMesh-Import-Attempt-N</c> markers and
///     is null where there are none to read (IMAP keeps none; a parked Graph mail has traded them for
///     the single parked marker). <see cref="FailureReason" /> is null: neither trigger records why a
///     mail failed.
/// </summary>
internal sealed record FailedMail(
    string Id,
    string? Subject,
    string? From,
    DateTimeOffset? ReceivedAt,
    int? Attempts,
    bool Parked,
    string? FailureReason);

/// <summary>The mails of a failed folder, newest first, capped.</summary>
internal sealed record FailedMailListing(IReadOnlyList<FailedMail> Mails, bool Truncated);

/// <summary>What happened to one mail asked to be re-queued.</summary>
internal sealed record RequeueOutcome(string Id, string? Subject, string Status, string? Reason)
{
    /// <summary>Markers cleared, mail moved into the source folder.</summary>
    public const string Requeued = "Requeued";

    /// <summary>The mail is not in the failed folder (any more) — nothing was changed.</summary>
    public const string NotFound = "NotFound";

    /// <summary>Something went wrong; <see cref="Reason" /> says what and whether the mail moved.</summary>
    public const string Failed = "Failed";

    public static RequeueOutcome Ok(string id, string? subject) => new(id, subject, Requeued, null);

    public static RequeueOutcome Missing(string id) =>
        new(id, null, NotFound, "The mail is no longer in the failed folder — nothing was changed.");

    public static RequeueOutcome Error(string id, string? subject, string reason) =>
        new(id, subject, Failed, reason);
}
