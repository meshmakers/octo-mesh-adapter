namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// What a mail trigger does with a message once the pipeline run for it is over (AB#5345).
/// Shared vocabulary of the IMAP (<c>FromEmail@1</c>) and Microsoft Graph
/// (<c>FromMicrosoftGraphEmail@1</c>) channels, so the same three choices mean the same thing
/// on both and a settings page can offer one list instead of two sets of switches.
/// <para>
/// 🔴 The post-processing is applied ONLY on success, and success is a BUSINESS outcome — an
/// inbox item was created — reported by the pipeline through its <c>successPath</c>, never
/// "the execution did not throw". A run can end perfectly normally while a node reported an
/// error and stopped its branch, and no per-node status reaches a trigger. A message whose
/// import is not confirmed is left exactly as the server has it, which is the only durable
/// record that it was never imported.
/// </para>
/// <para>
/// Every member is an explicit operator choice. A pipeline that names none keeps the
/// pre-AB#5345 behaviour, which each node derives from its own legacy properties — see
/// the nodes' <c>ResolveEffectivePostProcessingMode</c>.
/// </para>
/// </summary>
public enum MailPostProcessingMode
{
    /// <summary>
    /// Leave the message where it is and as it is. The derived mode of a pipeline that
    /// configured no folders and no flags, and the only mode under which a mailbox is never
    /// written to at all.
    /// </summary>
    None = 0,

    /// <summary>
    /// Mode A — source / done / failed folders. A message whose import was confirmed is moved to
    /// the done folder, one whose import was not is moved to the failed folder (when configured;
    /// otherwise it stays in the source folder). This is the mode that gives an operator a way
    /// BACK: moving a message out of the failed folder into the source folder offers it again.
    /// </summary>
    MoveToFolders = 1,

    /// <summary>
    /// Mode B — source folder only, the message is DELETED once its import was confirmed. The
    /// mailbox is the queue and nothing is kept; an unconfirmed message stays.
    /// </summary>
    Delete = 2,

    /// <summary>
    /// Mode C — source folder only, the message is marked as READ once its import was confirmed.
    /// Combined with the IMAP channel's <c>onlyUnread</c> search this is the lightest queue: an
    /// unconfirmed message stays unread and is offered again.
    /// </summary>
    MarkAsRead = 3
}
