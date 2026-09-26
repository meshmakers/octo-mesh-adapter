using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     Connecting to an IMAP mailbox and listing its folders, shared by <c>ListMailFolders@1</c> and
///     adoptable by <c>FromEmail@1</c> (AB#5370). Deliberately a separate helper rather than a change
///     to the trigger: the trigger is release content another change is touching, and the folder
///     picker needs nothing from it beyond the connection recipe — which is repeated here line for
///     line (SSL on connect vs. STARTTLS when available, then a plain login), so the picker lists
///     exactly the mailbox the import will poll.
/// </summary>
internal static class ImapMailboxAccess
{
    /// <summary>
    ///     The <c>System.Communication/EMailReceiverConfiguration</c> entity as the trigger reads it —
    ///     the same property names, so <c>IGlobalConfiguration.GetValue</c> binds the same entity.
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Global
    internal record ImapServerSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        public required string Host { get; init; }
        public required int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public required bool IsSslEnabled { get; init; }
        public string Folder { get; init; } = "INBOX";
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    /// <summary>
    ///     Opens and authenticates a client, the way <c>FromEmail@1</c> does. A failure is rethrown
    ///     as a <see cref="MailFolderListingException" /> that names its cause — see
    ///     <see cref="DescribeConnectFailure" />.
    /// </summary>
    internal static async Task<ImapClient> ConnectAndAuthenticateAsync(ImapServerSettings settings,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue) };
        try
        {
            var options = settings.IsSslEnabled
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(settings.Host, settings.Port, options, cancellationToken);
            await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new MailFolderListingException(DescribeConnectFailure(ex, settings, timeout), ex);
        }
    }

    /// <summary>
    ///     One sentence per cause, for the operator who typed the connection: rejected credentials,
    ///     a refused TLS handshake, an unreachable host, no answer in time — and the library's own
    ///     text only where none of those applies.
    /// </summary>
    internal static string DescribeConnectFailure(Exception ex, ImapServerSettings settings, TimeSpan timeout)
    {
        var server = $"{settings.Host}:{settings.Port}";
        return ex switch
        {
            AuthenticationException => $"IMAP authentication failed for user '{settings.Username}' at {server}: " +
                                       "the mail server rejected the user name or password.",
            SslHandshakeException => $"The TLS handshake with {server} was refused: {ex.Message} " +
                                     "Check the SSL/TLS switch and the port (993 for SSL/TLS, 143 for STARTTLS).",
            SocketException => $"The mail server {server} is not reachable from the adapter: {ex.Message}",
            IOException { InnerException: SocketException inner } =>
                $"The mail server {server} is not reachable from the adapter: {inner.Message}",
            OperationCanceledException or TimeoutException =>
                $"The mail server {server} did not answer within {timeout.TotalSeconds:0} seconds.",
            ImapCommandException or ImapProtocolException =>
                $"The mail server {server} answered with an IMAP error: {ex.Message}",
            _ => $"Connecting to the mail server {server} failed: {ex.Message}"
        };
    }

    /// <summary>
    ///     Lists every folder of the personal namespace(s), recursively through
    ///     <see cref="IMailFolder.GetSubfoldersAsync(StatusItems, bool, CancellationToken)" />, so a
    ///     folder's <c>path</c> is its <see cref="IMailFolder.FullName" /> EXACTLY as the server
    ///     reported it — with the server's own hierarchy delimiter and no client-side reassembly.
    ///     That is the string <c>FromEmail@1</c> hands to <c>GetFolderAsync</c> verbatim. Subscribed
    ///     and unsubscribed folders alike; the picker is about what exists.
    /// </summary>
    internal static async Task<MailFolderTree.Listing> ListFoldersAsync(ImapClient client, int maxFolders,
        CancellationToken cancellationToken)
    {
        var roots = new List<IMailFolder>();
        foreach (var ns in client.PersonalNamespaces)
        {
            var root = client.GetFolder(ns);
            if (string.IsNullOrEmpty(root.FullName))
            {
                // A namespace with an empty prefix ("" on Dovecot, Exchange and most others) is a
                // virtual root that is not itself a folder; its subfolders — INBOX among them — are
                // the top level.
                roots.AddRange(await root.GetSubfoldersAsync(StatusItems.None, false, cancellationToken));
            }
            else
            {
                // A prefixed namespace ("INBOX." on Courier-style servers) IS a folder — the inbox —
                // and everything else hangs beneath it.
                roots.Add(root);
            }
        }

        if (roots.Count == 0)
        {
            // No NAMESPACE support and nothing listed at the top: the inbox always exists.
            roots.Add(client.Inbox);
        }

        return await MailFolderTree.WalkDepthFirstAsync<IMailFolder>(
            roots,
            async folder => folder.Attributes.HasFlag(FolderAttributes.HasNoChildren)
                ? []
                : (await folder.GetSubfoldersAsync(StatusItems.None, false, cancellationToken)).ToList(),
            folder => folder.Name,
            (folder, _) => folder.FullName,
            maxFolders);
    }

    /// <summary>
    ///     The hierarchy delimiter the server uses, for the output's <c>delimiter</c>: the personal
    ///     namespace's, or the first listed folder's when the server announced no namespace.
    /// </summary>
    internal static string? ResolveDelimiter(ImapClient client)
    {
        if (client.PersonalNamespaces.Count > 0)
        {
            return client.PersonalNamespaces[0].DirectorySeparator.ToString();
        }

        return client.Inbox.DirectorySeparator == '\0' ? null : client.Inbox.DirectorySeparator.ToString();
    }
}
