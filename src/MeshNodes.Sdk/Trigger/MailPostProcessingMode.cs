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
/// 🔴 <b>There are exactly three modes, and every one of them takes the message OUT of the
/// search the next poll runs</b> — moved out of the source folder, deleted, or flagged
/// <c>\Seen</c> (which the IMAP channel's <c>NotSeen</c> search then skips). That is not a
/// coincidence, it is the whole mechanism: <b>nothing in OctoMesh records which mail was already
/// processed — the mailbox IS the bookkeeping.</b> A fourth member meaning "leave it where it
/// is" existed until AB#5372 (<c>None = 0</c>) and was a defect: combined with the per-poll cap
/// (<c>maxMessagesPerPoll</c>) it re-fetches the same first N messages on every poll and NEVER
/// reaches the mail behind the cap, so the import runs for ever, reports success and imports
/// nothing new — exactly the shape of AB#5336 (1697 mails, three pod restarts, no progress).
/// AB#5345 specified three modes; the fourth was an artefact of implementing them.
/// </para>
/// <para>
/// Every member is an explicit operator choice. A pipeline that names none has its mode DERIVED
/// from its own legacy properties — see the nodes' <c>ResolveEffectivePostProcessingMode</c>,
/// which now FAILS the trigger start where it used to fall through to <c>None</c>.
/// </para>
/// <para>
/// ⚠️ The members keep the numbers they had. Persistence is by NAME on both routes
/// (<c>ConfigurationSettingsReader.ReadEnum</c> refuses a purely numeric value outright, and
/// YamlDotNet reads the name), so renumbering would buy nothing and risk everything.
/// </para>
/// </summary>
public enum MailPostProcessingMode
{
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
