using MailKit;
using MailKit.Net.Imap;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     One connected import channel as <c>ListFailedImportMails@1</c> and
///     <c>RequeueFailedImportMails@1</c> (AB#7087) see it. Created by <see cref="FailedMailChannels" />
///     from the tenant's stored configuration only.
/// </summary>
internal interface IFailedMailChannel : IAsyncDisposable
{
    /// <summary>The mailbox address (Graph) or user name (IMAP) — a label for the operator, never a secret.</summary>
    string Mailbox { get; }

    /// <summary>The configured failed folder, as stored. Null when none is configured.</summary>
    string? FailedFolder { get; }

    /// <summary>The configured source folder, as stored. Null when none is configured.</summary>
    string? SourceFolder { get; }

    /// <summary>False after <see cref="ListAsync" /> / <see cref="PrepareRequeueAsync" /> found no such failed folder.</summary>
    bool FailedFolderFound { get; }

    Task<FailedMailListing> ListAsync(int max, CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves and opens both folders. Throws <see cref="MailFolderListingException" /> when no
    ///     source folder is configured or it does not exist (the re-queue never creates folders).
    ///     A missing failed folder is not an error — see <see cref="FailedFolderFound" />.
    /// </summary>
    Task PrepareRequeueAsync(CancellationToken cancellationToken);

    Task<RequeueOutcome> RequeueAsync(string id, CancellationToken cancellationToken);
}

/// <summary>Opens the channel a node was asked for, from the pipeline's configuration entities.</summary>
internal static class FailedMailChannels
{
    internal static async Task<IFailedMailChannel> OpenAsync(string channel, FailedImportMailsNodeConfiguration c,
        INodeContext nodeContext, IGlobalConfiguration global, IHttpClientFactory httpClientFactory,
        TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        return channel == FailedImportMailsNodeConfiguration.ChannelImap
            ? await OpenImapAsync(c, nodeContext, global, timeout, logger, cancellationToken)
            : await OpenGraphAsync(c, nodeContext, global, httpClientFactory, cancellationToken);
    }

    private static async Task<IFailedMailChannel> OpenGraphAsync(FailedImportMailsNodeConfiguration c,
        INodeContext nodeContext, IGlobalConfiguration global, IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(c.GraphServerConfiguration) || !global.IsDefined(c.GraphServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.GraphServerConfiguration), c.GraphServerConfiguration ?? "");
        }

        var credentials = global.GetValue<GraphMailboxAccess.GraphAppCredentials>(c.GraphServerConfiguration);
        // AB#5538: a credential resolved from configuration is masked in every diagnostic output.
        nodeContext.RegisterSecret(credentials.ClientSecret);

        var attributes = ConfigurationSettingsReader.TryGetAttributes(global, c.GraphSettingsConfiguration);
        var mailbox = ConfigurationSettingsReader.ReadString(attributes, c.GraphMailboxAttribute);
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            throw new MailFolderListingException(
                "No Microsoft 365 mailbox is configured yet — enter the mailbox address in Settings -> Import.");
        }

        var token = await GraphMailboxAccess.AcquireAppTokenAsync(httpClientFactory, credentials, cancellationToken);
        var client = GraphMailboxAccess.CreateGraphClient(httpClientFactory, token);
        return new GraphFailedMailChannel(client, mailbox,
            ConfigurationSettingsReader.ReadString(attributes, c.GraphFailedFolderAttribute),
            ConfigurationSettingsReader.ReadString(attributes, c.GraphSourceFolderAttribute));
    }

    private static async Task<IFailedMailChannel> OpenImapAsync(FailedImportMailsNodeConfiguration c,
        INodeContext nodeContext, IGlobalConfiguration global, TimeSpan timeout, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(c.ImapServerConfiguration) || !global.IsDefined(c.ImapServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.ImapServerConfiguration), c.ImapServerConfiguration ?? "");
        }

        var settings = global.GetValue<ImapMailboxAccess.ImapServerSettings>(c.ImapServerConfiguration);
        nodeContext.RegisterSecret(settings.Password);

        var attributes = ConfigurationSettingsReader.TryGetAttributes(global, c.ImapSettingsConfiguration);
        var failed = ConfigurationSettingsReader.ReadString(attributes, c.ImapFailedFolderAttribute);
        var source = ConfigurationSettingsReader.ReadString(attributes, c.ImapSourceFolderAttribute);

        var client = await ImapMailboxAccess.ConnectAndAuthenticateAsync(settings, timeout, cancellationToken, logger);
        return new ImapFailedMailChannel(client, settings.Username, failed, source, logger);
    }
}

internal sealed class GraphFailedMailChannel(HttpClient client, string mailbox, string? failedFolder,
    string? sourceFolder) : IFailedMailChannel
{
    private string? _failedFolderId;
    private string? _sourceFolderId;

    public string Mailbox => mailbox;
    public string? FailedFolder => failedFolder;
    public string? SourceFolder => sourceFolder;
    public bool FailedFolderFound { get; private set; } = true;

    public async Task<FailedMailListing> ListAsync(int max, CancellationToken cancellationToken)
    {
        await ResolveFailedAsync(cancellationToken);
        return _failedFolderId == null
            ? new FailedMailListing([], false)
            : await GraphFailedMailAccess.ListAsync(client, mailbox, _failedFolderId, max, cancellationToken);
    }

    public async Task PrepareRequeueAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            throw new MailFolderListingException(
                "No import folder is configured, so there is nowhere to send the mails back to — set it in Settings -> Import.");
        }

        await ResolveFailedAsync(cancellationToken);
        _sourceFolderId = await GraphFailedMailAccess.ResolveFolderIdAsync(client, mailbox, sourceFolder, cancellationToken)
                          ?? throw new MailFolderListingException(
                              $"The import folder '{sourceFolder}' does not exist in mailbox '{mailbox}' — check Settings -> Import.");
    }

    public Task<RequeueOutcome> RequeueAsync(string id, CancellationToken cancellationToken)
    {
        if (_failedFolderId == null || _sourceFolderId == null)
        {
            return Task.FromResult(RequeueOutcome.Missing(id));
        }

        return GraphFailedMailAccess.RequeueAsync(client, mailbox, _failedFolderId, _sourceFolderId, id,
            cancellationToken);
    }

    private async Task ResolveFailedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failedFolder))
        {
            throw new MailFolderListingException(
                "No failed folder is configured — set one in Settings -> Import.");
        }

        if (_failedFolderId != null)
        {
            return;
        }

        _failedFolderId = await GraphFailedMailAccess.ResolveFolderIdAsync(client, mailbox, failedFolder, cancellationToken);
        FailedFolderFound = _failedFolderId != null;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ImapFailedMailChannel(ImapClient client, string mailbox, string? failedFolder,
    string? sourceFolder, ILogger logger) : IFailedMailChannel
{
    private IMailFolder? _failed;
    private IMailFolder? _source;
    private bool _failedResolved;

    public string Mailbox => mailbox;
    public string? FailedFolder => failedFolder;
    public string? SourceFolder => sourceFolder;
    public bool FailedFolderFound { get; private set; } = true;

    public async Task<FailedMailListing> ListAsync(int max, CancellationToken cancellationToken)
    {
        await ResolveFailedAsync(cancellationToken);
        return _failed == null
            ? new FailedMailListing([], false)
            : await ImapFailedMailAccess.ListAsync(_failed, max, cancellationToken);
    }

    public async Task PrepareRequeueAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            throw new MailFolderListingException(
                "No import folder is configured, so there is nowhere to send the mails back to — set it in Settings -> Import.");
        }

        await ResolveFailedAsync(cancellationToken);
        _source = await ImapFailedMailAccess.ResolveFolderAsync(client, sourceFolder, cancellationToken)
                  ?? throw new MailFolderListingException(
                      $"The import folder '{sourceFolder}' does not exist on the mail server — check Settings -> Import.");
        if (_failed != null)
        {
            await _failed.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        }
    }

    public Task<RequeueOutcome> RequeueAsync(string id, CancellationToken cancellationToken)
    {
        if (_failed == null || _source == null)
        {
            return Task.FromResult(RequeueOutcome.Missing(id));
        }

        return ImapFailedMailAccess.RequeueAsync(_failed, _source, id, cancellationToken);
    }

    private async Task ResolveFailedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failedFolder))
        {
            throw new MailFolderListingException(
                "No failed folder is configured — set one in Settings -> Import.");
        }

        if (_failedResolved)
        {
            return;
        }

        _failed = await ImapFailedMailAccess.ResolveFolderAsync(client, failedFolder, cancellationToken);
        _failedResolved = true;
        FailedFolderFound = _failed != null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed import mails: IMAP logout failed");
        }

        client.Dispose();
    }
}
