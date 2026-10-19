using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration for node RequeueFailedImportMails (AB#7087): sends mails parked in the channel's
/// failed folder back into the folder the import polls, as a fresh start.
/// <para>
/// Per mail, in this order: (1) clear every well-formed <c>OctoMesh-Import-*</c> marker (Graph
/// categories; IMAP keywords — a user's own categories are kept) and, for IMAP, the <c>\Seen</c> and
/// <c>\Deleted</c> flags; (2) only if that worked, move the mail to the configured source folder. A
/// mail moved back with its markers still on would be parked again by the very next poll, because the
/// attempts are counted from them (AB#7068) — so a mail whose markers cannot be cleared is NOT moved
/// and the result says why.
/// </para>
/// <para>
/// Which mails: <see cref="MessageIdsPath" /> (an array of the ids <c>ListFailedImportMails@1</c>
/// returned) or, when <see cref="AllPath" /> resolves to <c>true</c>, every mail in the failed folder
/// up to <c>maxMails</c>. One failing mail never stops the others. Output at
/// <see cref="TargetPathNodeConfiguration.TargetPath" />:
/// <c>{ channel, mailbox, failedFolder, sourceFolder, requested, requeued, notFound, failed, truncated,
/// results: [ { id, subject, status: Requeued | NotFound | Failed, reason, internetMessageId } ] }</c>.
/// </para>
/// </summary>
[NodeName("RequeueFailedImportMails", 1)]
public record RequeueFailedImportMailsNodeConfiguration : FailedImportMailsNodeConfiguration
{
    /// <summary>JSONPath to the array of mail ids to re-queue, e.g. <c>$.body.messageIds</c>.</summary>
    [PropertyGroup("Selection", 0)]
    public string? MessageIdsPath { get; set; }

    /// <summary>JSONPath to a boolean; <c>true</c> re-queues every mail in the failed folder, e.g. <c>$.body.all</c>.</summary>
    [PropertyGroup("Selection", 1)]
    public string? AllPath { get; set; }
}
