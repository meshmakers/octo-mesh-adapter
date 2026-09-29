namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     A mailbox could not be listed, with a message worded for the OPERATOR who configured the
///     connection (AB#5370): which server or mailbox, which of the credentials or permissions, and
///     what to check. The message travels through the HTTP route into the settings page verbatim, so
///     it never carries a stack trace and never a raw library message on its own. Thrown by the
///     channel helpers, translated into the pipeline exception by <c>ListMailFolders@1</c>.
/// </summary>
internal sealed class MailFolderListingException(string message, Exception? inner = null)
    : Exception(message, inner);
