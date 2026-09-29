using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
///     Lists the folders of a mailbox in exactly the syntax the channel's trigger accepts (AB#5370),
///     so an operator picks a folder from the server's own list instead of typing a path whose
///     delimiter they have to guess. Two channels:
///     <list type="bullet">
///         <item>
///             <c>Imap</c> — connects with the <c>EMailReceiverConfiguration</c> that
///             <c>FromEmail@1</c> polls and returns every folder's full name AS THE SERVER REPORTS
///             IT, with the server's own hierarchy delimiter (<c>INBOX.Finanzen.Rechnungen</c> on
///             Dovecot, <c>INBOX/Finanzen/Rechnungen</c> elsewhere). No client-side reassembly.
///         </item>
///         <item>
///             <c>Graph</c> — signs in with the <c>MicrosoftGraphConfiguration</c> and lists the
///             folder tree of the mailbox <c>FromMicrosoftGraphEmail@1</c> reads (resolved from the
///             same settings entity and attribute), spelled by <see cref="MailFolderPathSyntax" />:
///             display names joined with <c>/</c>, a <c>/</c> inside a name escaped as <c>\/</c>.
///         </item>
///     </list>
///     Output at <c>TargetPath</c>: <c>{ channel, delimiter, folders: [ { path, displayName, depth } ],
///     truncated }</c> — depth-first, in server order, capped at <c>maxFolders</c>. Every failure
///     throws with a message worded for the operator (which credential, which permission, which
///     host), because the HTTP route hands it straight to the settings page — listing the folders is
///     also the first honest connection test that page has. Read-only: nothing is created, selected
///     or moved.
/// </summary>
[NodeConfiguration(typeof(ListMailFoldersNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ListMailFoldersNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    ILogger<ListMailFoldersNode> logger)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ListMailFoldersNodeConfiguration>();

        var channel = ResolveChannel(dataContext, c);
        var maxFolders = c.MaxFolders ?? ListMailFoldersNodeConfiguration.DefaultMaxFolders;
        if (maxFolders <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"maxFolders must be positive, got {maxFolders}");
        }

        var timeoutSeconds = c.TimeoutSeconds ?? ListMailFoldersNodeConfiguration.DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"timeoutSeconds must be positive, got {timeoutSeconds}");
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var cts = new CancellationTokenSource(timeout);

        JsonObject result;
        try
        {
            result = channel == ListMailFoldersNodeConfiguration.ChannelImap
                ? await ListImapAsync(c, nodeContext, maxFolders, timeout, cts.Token)
                : await ListGraphAsync(c, nodeContext, maxFolders, cts.Token);
        }
        catch (MailFolderListingException ex)
        {
            logger.LogWarning(ex, "ListMailFolders: listing the {Channel} mailbox failed", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel, ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ListMailFolders: listing the {Channel} mailbox timed out", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel,
                $"Listing the mailbox did not finish within {timeoutSeconds} seconds.", ex);
        }

        nodeContext.Debug("ListMailFolders: {0} folder(s) listed for channel {1}{2}",
            result["folders"]!.AsArray().Count, channel,
            result["truncated"]!.GetValue<bool>() ? $" (truncated at {maxFolders})" : "");

        dataContext.Set(c.TargetPath, (JsonNode)result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    ///     The channel to list: <c>channelPath</c> when it resolves to a value, else <c>channel</c>.
    ///     Anything but the two known names is a configuration (or request) error and says so.
    /// </summary>
    private static string ResolveChannel(IDataContext dataContext, ListMailFoldersNodeConfiguration c)
    {
        string? raw = null;
        if (!string.IsNullOrWhiteSpace(c.ChannelPath))
        {
            raw = dataContext.Get<string>(c.ChannelPath);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = c.Channel;
        }

        return NormalizeChannel(raw) ?? throw MeshAdapterPipelineExecutionException.MailFolderChannelInvalid(raw);
    }

    /// <summary>
    ///     Maps a channel name to its canonical spelling, case-insensitively; null for anything else.
    /// </summary>
    internal static string? NormalizeChannel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, ListMailFoldersNodeConfiguration.ChannelImap, StringComparison.OrdinalIgnoreCase))
        {
            return ListMailFoldersNodeConfiguration.ChannelImap;
        }

        if (string.Equals(trimmed, ListMailFoldersNodeConfiguration.ChannelGraph, StringComparison.OrdinalIgnoreCase))
        {
            return ListMailFoldersNodeConfiguration.ChannelGraph;
        }

        return null;
    }

    /// <summary>
    ///     The Graph mailbox, resolved the way <c>FromMicrosoftGraphEmail@1</c> resolves it: a value
    ///     on the settings configuration (by attribute name) wins, the node property is the fallback.
    /// </summary>
    internal static string? ResolveGraphMailbox(IGlobalConfiguration globalConfiguration,
        ListMailFoldersNodeConfiguration c)
    {
        var attributes = ConfigurationSettingsReader.TryGetAttributes(globalConfiguration,
            c.GraphSettingsConfiguration);
        var fromSettings = ConfigurationSettingsReader.ReadString(attributes, c.GraphMailboxAttribute);
        return string.IsNullOrWhiteSpace(fromSettings) ? c.GraphMailbox : fromSettings;
    }

    private async Task<JsonObject> ListImapAsync(ListMailFoldersNodeConfiguration c, INodeContext nodeContext,
        int maxFolders, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(c.ImapServerConfiguration) ||
            !etlContext.GlobalConfiguration.IsDefined(c.ImapServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.ImapServerConfiguration), c.ImapServerConfiguration ?? "");
        }

        var settings = etlContext.GlobalConfiguration.GetValue<ImapMailboxAccess.ImapServerSettings>(
            c.ImapServerConfiguration);

        using var client = await ImapMailboxAccess.ConnectAndAuthenticateAsync(settings, timeout, cancellationToken);
        try
        {
            var listing = await ImapMailboxAccess.ListFoldersAsync(client, maxFolders, cancellationToken);
            var result = ToJson(ListMailFoldersNodeConfiguration.ChannelImap,
                ImapMailboxAccess.ResolveDelimiter(client), listing);
            result["mailbox"] = settings.Username;
            return result;
        }
        catch (Exception ex) when (ex is not MailFolderListingException and not OperationCanceledException)
        {
            throw new MailFolderListingException(
                ImapMailboxAccess.DescribeConnectFailure(ex, settings, timeout), ex);
        }
        finally
        {
            try
            {
                // A polite LOGOUT only while the budget is not spent: after the timeout fired the
                // server may never answer it, and the route would wait a second budget for nothing.
                await client.DisconnectAsync(quit: !cancellationToken.IsCancellationRequested,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The list is already in hand; a failed LOGOUT is not worth failing the picker for.
                logger.LogDebug(ex, "ListMailFolders: IMAP logout failed");
            }
        }
    }

    private async Task<JsonObject> ListGraphAsync(ListMailFoldersNodeConfiguration c, INodeContext nodeContext,
        int maxFolders, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(c.GraphServerConfiguration) ||
            !etlContext.GlobalConfiguration.IsDefined(c.GraphServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.GraphServerConfiguration), c.GraphServerConfiguration ?? "");
        }

        var credentials = etlContext.GlobalConfiguration.GetValue<GraphMailboxAccess.GraphAppCredentials>(
            c.GraphServerConfiguration);

        var mailbox = ResolveGraphMailbox(etlContext.GlobalConfiguration, c);
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            throw new MailFolderListingException(
                "No Microsoft 365 mailbox is configured yet — enter the mailbox address and save before choosing a folder.");
        }

        var token = await GraphMailboxAccess.AcquireAppTokenAsync(httpClientFactory, credentials, cancellationToken);
        using var client = GraphMailboxAccess.CreateGraphClient(httpClientFactory, token);
        var listing = await GraphMailboxAccess.ListFoldersAsync(client, mailbox, maxFolders, cancellationToken);

        var result = ToJson(ListMailFoldersNodeConfiguration.ChannelGraph,
            MailFolderPathSyntax.GraphSeparator.ToString(), listing);
        result["mailbox"] = mailbox;
        return result;
    }

    /// <summary>The output shape, shared by both channels.</summary>
    internal static JsonObject ToJson(string channel, string? delimiter, MailFolderTree.Listing listing)
    {
        var folders = new JsonArray();
        foreach (var entry in listing.Folders)
        {
            folders.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["displayName"] = entry.DisplayName,
                ["depth"] = entry.Depth
            });
        }

        return new JsonObject
        {
            ["channel"] = channel,
            ["delimiter"] = delimiter,
            ["folders"] = folders,
            ["truncated"] = listing.Truncated
        };
    }
}
