using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
///     Tests one document-import channel's connection step by step, the way its trigger would
///     use it, and reports every step (see <see cref="TestImportConnectionNodeConfiguration" />).
///     The "Test connection" button of the settings page (AB#5370 follow-up).
///     <para>
///     Each channel is a short list of named checks, run in order and stopped at the first
///     failure; the result carries them all, so the operator sees how far the connection got
///     ("signed in, but the source folder does not exist" is a different fix from "the password
///     is wrong"). Every failure is worded for the person who configured the channel — the same
///     wording the triggers and the folder picker use — and is a RESULT, never a thrown node error.
///     </para>
/// </summary>
[NodeConfiguration(typeof(TestImportConnectionNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class TestImportConnectionNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    ILogger<TestImportConnectionNode> logger)
    : IPipelineNode
{
    private const string BotFrameworkScope = "https://api.botframework.com/.default";
    private const string BotFrameworkMultiTenantTokenUrl =
        "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";

    /// <summary>One step of a test: what was checked, whether it passed, and the operator-facing detail.</summary>
    internal sealed record Check(string Name, bool Ok, string Detail);

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<TestImportConnectionNodeConfiguration>();
        var channel = ResolveChannel(dataContext, c);

        var timeoutSeconds = c.TimeoutSeconds ?? TestImportConnectionNodeConfiguration.DefaultTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"timeoutSeconds must be positive, got {timeoutSeconds}");
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var cts = new CancellationTokenSource(timeout);

        var checks = new List<Check>();
        try
        {
            switch (channel)
            {
                case TestImportConnectionNodeConfiguration.ChannelImap:
                    await TestImapAsync(c, nodeContext, checks, timeout, cts.Token);
                    break;
                case TestImportConnectionNodeConfiguration.ChannelGraph:
                    await TestGraphAsync(c, nodeContext, checks, cts.Token);
                    break;
                case TestImportConnectionNodeConfiguration.ChannelTeams:
                    await TestTeamsAsync(c, nodeContext, checks, cts.Token);
                    break;
                default:
                    await TestSignalAsync(checks, cts.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            checks.Add(new Check("timeout", false,
                $"The test did not finish within {timeoutSeconds} seconds."));
        }

        var result = ToJson(channel, checks);
        logger.LogInformation("TestImportConnection: {Channel} → {Outcome} ({Summary})",
            channel, result["ok"]!.GetValue<bool>() ? "ok" : "failed", result["summary"]!.GetValue<string>());

        dataContext.Set(c.TargetPath, (JsonNode)result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        await next(dataContext, nodeContext);
    }

    // ------------------------------------------------------------------ channels

    private async Task TestImapAsync(TestImportConnectionNodeConfiguration c, INodeContext nodeContext,
        List<Check> checks, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(c.ImapServerConfiguration) ||
            !etlContext.GlobalConfiguration.IsDefined(c.ImapServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.ImapServerConfiguration), c.ImapServerConfiguration ?? "");
        }

        var settings = etlContext.GlobalConfiguration.GetValue<ImapMailboxAccess.ImapServerSettings>(
            c.ImapServerConfiguration);
        // AB#5538: a credential resolved from configuration is masked in every diagnostic output.
        nodeContext.RegisterSecret(settings.Password);
        var server = $"{settings.Host}:{settings.Port}";

        MailKit.Net.Imap.ImapClient client;
        try
        {
            client = await ImapMailboxAccess.ConnectAndAuthenticateAsync(settings, timeout, cancellationToken, logger);
        }
        catch (MailFolderListingException ex)
        {
            checks.Add(new Check("connection", false, ex.Message));
            return;
        }

        checks.Add(new Check("connection", true,
            $"Connected to {server} ({(settings.IsSslEnabled ? "SSL/TLS" : "STARTTLS")}) and signed in as '{settings.Username}'."));

        try
        {
            MailFolderTree.Listing listing;
            try
            {
                listing = await ImapMailboxAccess.ListFoldersAsync(client, 500, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(new Check("folders", false,
                    ImapMailboxAccess.DescribeConnectFailure(ex, settings, timeout)));
                return;
            }

            var delimiter = ImapMailboxAccess.ResolveDelimiter(client);
            checks.Add(new Check("folders", true,
                $"{listing.Folders.Count} folder(s) listed{(listing.Truncated ? " (first 500)" : "")}; the server's hierarchy delimiter is '{delimiter}'."));

            // The folder the trigger polls: the settings attribute, else the server
            // configuration's default folder — exactly FromEmail@1's resolution.
            var configured = ReadSetting(c.ImapSettingsConfiguration, c.ImapSourceFolderAttribute);
            var sourceFolder = string.IsNullOrWhiteSpace(configured) ? settings.Folder : configured;
            checks.Add(SourceFolderCheck(sourceFolder, listing.Folders.Select(f => f.Path),
                string.IsNullOrWhiteSpace(configured)
                    ? " (the server configuration's default — no source folder is set on the channel)"
                    : ""));
        }
        finally
        {
            try
            {
                await client.DisconnectAsync(quit: !cancellationToken.IsCancellationRequested, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "TestImportConnection: IMAP logout failed");
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private async Task TestGraphAsync(TestImportConnectionNodeConfiguration c, INodeContext nodeContext,
        List<Check> checks, CancellationToken cancellationToken)
    {
        var credentials = GraphCredentials(c, nodeContext);

        var mailbox = ResolveGraphMailbox(c);
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            checks.Add(new Check("mailbox", false,
                "No Microsoft 365 mailbox is configured yet — enter the mailbox address and save before testing."));
            return;
        }

        var token = await AcquireGraphTokenAsync(credentials, checks, cancellationToken);
        if (token == null) return;

        MailFolderTree.Listing listing;
        try
        {
            using var client = GraphMailboxAccess.CreateGraphClient(httpClientFactory, token);
            listing = await GraphMailboxAccess.ListFoldersAsync(client, mailbox, 500, cancellationToken);
        }
        catch (MailFolderListingException ex)
        {
            checks.Add(new Check("mailbox", false, ex.Message));
            return;
        }

        checks.Add(new Check("mailbox", true,
            $"Mailbox '{mailbox}' opened; {listing.Folders.Count} folder(s) listed{(listing.Truncated ? " (first 500)" : "")}."));

        var sourceFolder = ReadSetting(c.GraphSettingsConfiguration, c.GraphSourceFolderAttribute);
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            checks.Add(new Check("sourceFolder", false,
                "No source folder is configured yet — the import has nothing to poll until one is set and saved."));
            return;
        }

        checks.Add(SourceFolderCheck(sourceFolder, listing.Folders.Select(f => f.Path), ""));
    }

    private async Task TestTeamsAsync(TestImportConnectionNodeConfiguration c, INodeContext nodeContext,
        List<Check> checks, CancellationToken cancellationToken)
    {
        var credentials = GraphCredentials(c, nodeContext);

        // The bot downloads attachments with a Graph token …
        var graphToken = await AcquireGraphTokenAsync(credentials, checks, cancellationToken);
        if (graphToken == null) return;

        // … and replies with a Bot Framework token of the same app registration. Single-tenant
        // bots use the tenant authority, multi-tenant bots botframework.com — TeamsBotReply@1's rule.
        var tokenUrl = string.IsNullOrWhiteSpace(credentials.AzureTenantId)
            ? BotFrameworkMultiTenantTokenUrl
            : $"https://login.microsoftonline.com/{Uri.EscapeDataString(credentials.AzureTenantId)}/oauth2/v2.0/token";
        try
        {
            using var client = httpClientFactory.CreateClient("TeamsBot");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId,
                ["client_secret"] = credentials.ClientSecret,
                ["scope"] = BotFrameworkScope
            });
            using var response = await client.PostAsync(tokenUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                checks.Add(new Check("botToken", false,
                    "The Bot Framework refused the app registration as a bot identity: " +
                    GraphMailboxAccess.DescribeTokenFailure((int)response.StatusCode, body) +
                    " The Azure bot resource must use this very app ID and secret."));
                return;
            }

            checks.Add(new Check("botToken", true,
                $"The Bot Framework accepted app '{credentials.ClientId}' as a bot identity — replies can be sent."));
        }
        catch (HttpRequestException ex)
        {
            checks.Add(new Check("botToken", false,
                $"The Bot Framework sign-in endpoint is not reachable from the import service: {ex.Message}"));
        }
    }

    private async Task TestSignalAsync(List<Check> checks, CancellationToken cancellationToken)
    {
        var resolution = SignalChannelEndpointResolver.Resolve(etlContext.GlobalConfiguration,
            null, null, null, null, null);
        if (!resolution.IsConfigured)
        {
            checks.Add(new Check("channel", false,
                "No registered Signal number: " +
                (resolution.Warnings.Count > 0
                    ? string.Join(" ", resolution.Warnings)
                    : "the tenant has no System.Communication/SignalChannel — register the number in Refinery Studio (Communication → Signal channel).")));
            return;
        }

        checks.Add(new Check("channel", true,
            $"Signal number {resolution.Number} is registered for this tenant (bridge {resolution.ApiUrl})."));

        var apiBase = resolution.ApiUrl!.TrimEnd('/');
        var client = httpClientFactory.CreateClient("Signal");

        string aboutBody;
        try
        {
            using var about = await client.GetAsync($"{apiBase}/v1/about", cancellationToken);
            aboutBody = await about.Content.ReadAsStringAsync(cancellationToken);
            if (!about.IsSuccessStatusCode)
            {
                checks.Add(new Check("bridge", false,
                    $"The Signal bridge at {apiBase} answered HTTP {(int)about.StatusCode} to /v1/about — it is reachable but not healthy."));
                return;
            }
        }
        catch (HttpRequestException ex)
        {
            checks.Add(new Check("bridge", false,
                $"The Signal bridge at {apiBase} is not reachable from the import service: {ex.Message}"));
            return;
        }

        checks.Add(new Check("bridge", true,
            $"Signal bridge reachable at {apiBase}{DescribeBridge(aboutBody)}."));

        try
        {
            using var accounts = await client.GetAsync($"{apiBase}/v1/accounts", cancellationToken);
            var body = await accounts.Content.ReadAsStringAsync(cancellationToken);
            if (!accounts.IsSuccessStatusCode)
            {
                checks.Add(new Check("account", false,
                    $"The Signal bridge answered HTTP {(int)accounts.StatusCode} to /v1/accounts."));
                return;
            }

            checks.Add(AccountCheck(resolution.Number!, body));
        }
        catch (HttpRequestException ex)
        {
            checks.Add(new Check("account", false,
                $"The Signal bridge did not answer /v1/accounts: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------ shared steps

    private GraphMailboxAccess.GraphAppCredentials GraphCredentials(TestImportConnectionNodeConfiguration c,
        INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(c.GraphServerConfiguration) ||
            !etlContext.GlobalConfiguration.IsDefined(c.GraphServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                nameof(c.GraphServerConfiguration), c.GraphServerConfiguration ?? "");
        }

        var credentials = etlContext.GlobalConfiguration.GetValue<GraphMailboxAccess.GraphAppCredentials>(
            c.GraphServerConfiguration);
        // AB#5538: a credential resolved from configuration is masked in every diagnostic output.
        nodeContext.RegisterSecret(credentials.ClientSecret);
        return credentials;
    }

    /// <summary>The app token, as a passed check; null (and a failed check) when it was refused.</summary>
    private async Task<string?> AcquireGraphTokenAsync(GraphMailboxAccess.GraphAppCredentials credentials,
        List<Check> checks, CancellationToken cancellationToken)
    {
        try
        {
            var token = await GraphMailboxAccess.AcquireAppTokenAsync(httpClientFactory, credentials, cancellationToken);
            checks.Add(new Check("graphToken", true,
                $"Microsoft 365 accepted the app registration (tenant {credentials.AzureTenantId}, app {credentials.ClientId})."));
            return token;
        }
        catch (MailFolderListingException ex)
        {
            checks.Add(new Check("graphToken", false, ex.Message));
            return null;
        }
    }

    private string? ResolveGraphMailbox(TestImportConnectionNodeConfiguration c)
    {
        var fromSettings = ReadSetting(c.GraphSettingsConfiguration, c.GraphMailboxAttribute);
        return string.IsNullOrWhiteSpace(fromSettings) ? c.GraphMailbox : fromSettings;
    }

    private string? ReadSetting(string? configuration, string? attribute)
    {
        var attributes = ConfigurationSettingsReader.TryGetAttributes(etlContext.GlobalConfiguration, configuration);
        return ConfigurationSettingsReader.ReadString(attributes, attribute);
    }

    // ------------------------------------------------------------------ pure parts (tested)

    /// <summary>
    ///     The channel to test: <c>channelPath</c> when it resolves to a value, else <c>channel</c>.
    /// </summary>
    private static string ResolveChannel(IDataContext dataContext, TestImportConnectionNodeConfiguration c)
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

        return NormalizeChannel(raw) ?? throw MeshAdapterPipelineExecutionException.ImportConnectionChannelInvalid(raw);
    }

    /// <summary>Maps a channel name to its canonical spelling, case-insensitively; null for anything else.</summary>
    internal static string? NormalizeChannel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        foreach (var known in new[]
                 {
                     TestImportConnectionNodeConfiguration.ChannelImap,
                     TestImportConnectionNodeConfiguration.ChannelGraph,
                     TestImportConnectionNodeConfiguration.ChannelTeams,
                     TestImportConnectionNodeConfiguration.ChannelSignal
                 })
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase)) return known;
        }

        return null;
    }

    /// <summary>
    ///     Does the folder the trigger would poll exist in the mailbox? Compared verbatim — the
    ///     path IS the string the trigger hands to the server (AB#5370/AB#5385), so a near miss
    ///     (wrong delimiter, wrong case on a case-sensitive server) is reported as the miss it is,
    ///     with the closest listed path named when there is one.
    /// </summary>
    internal static Check SourceFolderCheck(string folder, IEnumerable<string> listedPaths, string origin)
    {
        var paths = listedPaths.ToList();
        if (paths.Contains(folder, StringComparer.Ordinal))
        {
            return new Check("sourceFolder", true, $"Source folder '{folder}' exists{origin}.");
        }

        var nearest = paths.FirstOrDefault(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase))
                      ?? paths.FirstOrDefault(p =>
                          string.Equals(Fold(p), Fold(folder), StringComparison.OrdinalIgnoreCase));
        var hint = nearest == null
            ? " Pick it from the folder list, or create it in the mailbox first."
            : $" The mailbox has '{nearest}' — the trigger needs the path exactly as the server spells it.";
        return new Check("sourceFolder", false, $"Source folder '{folder}' was not found in the mailbox{origin}.{hint}");
    }

    /// <summary>Delimiter-insensitive form for the "nearest path" hint only.</summary>
    private static string Fold(string path) => path.Replace('.', '/').Replace('\\', '/').Trim('/');

    /// <summary>Version and mode out of signal-cli-rest-api's <c>/v1/about</c>, when the body has them.</summary>
    internal static string DescribeBridge(string aboutBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(aboutBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            var parts = new List<string>();
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                parts.Add($"version {version.GetString()}");
            if (root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String)
                parts.Add($"mode {mode.GetString()}");
            return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    ///     Is the registered number one of the bridge's accounts (<c>/v1/accounts</c> answers a JSON
    ///     array of numbers)? A bridge that holds other numbers but not this one is the case the
    ///     trigger would poll forever without a message.
    /// </summary>
    internal static Check AccountCheck(string number, string accountsBody)
    {
        List<string> accounts;
        try
        {
            using var doc = JsonDocument.Parse(accountsBody);
            accounts = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return new Check("account", false,
                "The Signal bridge answered /v1/accounts with something that is not a list of numbers.");
        }

        var normalized = NormalizeNumber(number);
        if (accounts.Any(a => NormalizeNumber(a) == normalized))
        {
            return new Check("account", true, $"Number {number} is linked on the bridge — messages can be received and sent.");
        }

        return new Check("account", false,
            $"Number {number} is not linked on the bridge" +
            (accounts.Count == 0
                ? " (the bridge holds no account at all)."
                : $" (it holds {string.Join(", ", accounts)}).") +
            " Link the number on the bridge or register the right one in Refinery Studio.");
    }

    private static string NormalizeNumber(string value) =>
        new(value.Where(ch => char.IsDigit(ch) || ch == '+').ToArray());

    /// <summary>The output shape: channel, overall verdict, one-line summary, and the checks in order.</summary>
    internal static JsonObject ToJson(string channel, IReadOnlyList<Check> checks)
    {
        var ok = checks.Count > 0 && checks.All(x => x.Ok);
        var failed = checks.FirstOrDefault(x => !x.Ok);
        var array = new JsonArray();
        foreach (var check in checks)
        {
            array.Add(new JsonObject
            {
                ["name"] = check.Name,
                ["ok"] = check.Ok,
                ["detail"] = check.Detail
            });
        }

        return new JsonObject
        {
            ["channel"] = channel,
            ["ok"] = ok,
            ["summary"] = ok
                ? $"{channel}: every check passed ({checks.Count})."
                : failed == null
                    ? $"{channel}: nothing was checked."
                    : $"{channel}: {failed.Detail}",
            ["checks"] = array
        };
    }
}
