using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node ListFailedImportMails (AB#7087): lists the mails the e-mail document
/// import gave up on and parked in the channel's failed folder, so an accounting manager can send
/// them back without anybody opening the mailbox.
/// <para>
/// Output at <see cref="TargetPathNodeConfiguration.TargetPath" />:
/// <c>{ channel, mailbox, configured, folderFound, failedFolder, sourceFolder, truncated,
/// mails: [ { id, subject, from, receivedAt, attempts, parked, failureReason, internetMessageId } ] }</c>. A channel
/// without a configured failed folder answers <c>configured: false</c> instead of an error.
/// <c>attempts</c> is the count read from the <c>OctoMesh-Import-Attempt-N</c> markers (null when the
/// channel keeps none — IMAP — or the mail was parked, which drops them); <c>failureReason</c> is null
/// because neither trigger records one. Read-only.
/// </para>
/// </summary>
[NodeName("ListFailedImportMails", 1)]
public record ListFailedImportMailsNodeConfiguration : FailedImportMailsNodeConfiguration;
