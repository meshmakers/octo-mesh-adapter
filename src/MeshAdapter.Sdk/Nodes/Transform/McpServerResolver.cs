using System.Net;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using ModelContextProtocol.Client;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Shared resolution + transport construction for MCP (Model Context Protocol)
/// servers referenced by name from <c>GlobalConfiguration</c>. Extracted from
/// <see cref="LlmQueryNode"/> so both the LLM-driven path (<c>LlmQuery@1</c>,
/// which lists tools and hands them to the model) and the deterministic path
/// (<c>McpToolCall@1</c>, which invokes a single tool directly without an LLM)
/// share one battle-tested implementation of config parsing, Bearer /
/// AdditionalHeaders auth composition, and stdio / SSE / HTTP transport
/// selection.
/// </summary>
internal static class McpServerResolver
{
    // Wire-format note: McpConfiguration.transport in GlobalConfiguration JSON
    // may serialize as either the integer key (0/1/2) or the enum name string
    // ("Stdio"/"Sse"/"Http"), depending on the runtime engine's JSON converter
    // configuration. Resolve() accepts both forms.
    internal enum McpTransport { Stdio = 0, Sse = 1, Http = 2 }

    internal sealed record McpServerConfig(
        string Name,
        string? Url,
        McpTransport Transport,
        string? Command,
        string? Arguments,
        string? BearerToken,
        IReadOnlyDictionary<string, string> AdditionalHeaders,
        string? AuthServiceAccountConfigurationName)
    {
        /// <summary>
        /// Redacts secrets — the record auto-ToString would print the live bearer
        /// token and header values into any log or exception message that formats
        /// the config. Header names are safe; values never.
        /// </summary>
        public override string ToString() =>
            $"McpServerConfig {{ Name = {Name}, Transport = {Transport}, Url = {Url}, " +
            $"Command = {Command}, Arguments = {Arguments}, " +
            $"BearerToken = {(string.IsNullOrEmpty(BearerToken) ? "<none>" : "<redacted>")}, " +
            $"AdditionalHeaders = [{string.Join(", ", AdditionalHeaders.Keys)}], " +
            $"AuthServiceAccountConfigurationName = {AuthServiceAccountConfigurationName} }}";
    }

    /// <summary>
    /// Resolves the named <c>System.Communication/McpConfiguration</c> entities
    /// from <c>GlobalConfiguration</c>. Unknown names log a warning and are
    /// skipped (the caller still proceeds with the rest), mirroring the tolerant
    /// behavior of <c>LlmQueryNode.ResolveApiKey</c>.
    /// </summary>
    internal static IList<McpServerConfig> Resolve(
        IEnumerable<string> names,
        IMeshEtlContext etlContext,
        INodeContext nodeContext)
    {
        var result = new List<McpServerConfig>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!etlContext.GlobalConfiguration.IsDefined(name))
            {
                nodeContext.Warning(
                    $"McpConfiguration '{name}' not found in GlobalConfiguration; skipping. " +
                    "Create one via Studio (General → Configurations → MCP Server) or via " +
                    "runtime.systemCommunicationMcpConfigurations.create.");
                continue;
            }

            var rawJson = etlContext.GlobalConfiguration.GetRawJson(name);
            if (string.IsNullOrEmpty(rawJson)) continue;
            var doc = JObject.Parse(rawJson);

            var transport = ParseTransport(doc["transport"], name, nodeContext);

            var additionalHeaders = ParseHeaders(doc["additionalHeaders"] as JArray);

            result.Add(new McpServerConfig(
                Name: name,
                Url: ResolveUrl(doc.Value<string>("url"), etlContext.TenantId),
                Transport: transport,
                Command: doc.Value<string>("command"),
                Arguments: doc.Value<string>("arguments"),
                BearerToken: doc.Value<string>("bearerToken"),
                AdditionalHeaders: additionalHeaders,
                AuthServiceAccountConfigurationName: doc.Value<string>("authServiceAccountConfigurationName")));

            // Header *names* are safe to log (values are secrets and are never logged).
            var headerInfo = additionalHeaders.Count > 0
                ? $", additionalHeaders=[{string.Join(", ", additionalHeaders.Keys)}]"
                : string.Empty;
            nodeContext.Debug($"McpConfiguration '{name}' loaded: transport={transport}{headerInfo}");
        }
        return result;
    }

    /// <summary>
    /// Both wire forms are accepted (integer key or enum name). An unknown value falls back to
    /// Sse — the documented default — with a warning, instead of producing an undefined enum
    /// member that <see cref="BuildTransport"/> would reject later with a less useful message.
    /// </summary>
    internal static McpTransport ParseTransport(JToken? transportToken, string name, INodeContext nodeContext)
    {
        switch (transportToken?.Type)
        {
            case JTokenType.Integer:
                var key = transportToken.Value<int>();
                if (Enum.IsDefined(typeof(McpTransport), key))
                {
                    return (McpTransport)key;
                }

                nodeContext.Warning(
                    $"McpConfiguration '{name}': unknown transport key {key}; falling back to Sse.");
                return McpTransport.Sse;
            case JTokenType.String:
                var text = transportToken.Value<string>()!;
                if (Enum.TryParse<McpTransport>(text, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(typeof(McpTransport), parsed))
                {
                    return parsed;
                }

                nodeContext.Warning(
                    $"McpConfiguration '{name}': unknown transport '{text}'; falling back to Sse.");
                return McpTransport.Sse;
            default:
                return McpTransport.Sse;
        }
    }

    /// <summary>
    /// Substitutes the <c>{tenantId}</c> placeholder the CK attribute description promises, so a
    /// seeded configuration can name the tenant-routed MCP endpoint without baking a tenant in.
    /// </summary>
    internal static string? ResolveUrl(string? url, string tenantId) =>
        string.IsNullOrEmpty(url) ? url : url.Replace("{tenantId}", tenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the static <c>BearerToken</c> of every server that references a
    /// <c>ServiceAccountConfiguration</c> (via <c>AuthServiceAccountConfigurationName</c>)
    /// with a freshly acquired client-credentials token. Precedence, matching DD-4:
    /// service-account token &gt; static BearerToken; an explicit <c>Authorization</c>
    /// entry in AdditionalHeaders still overrides both (applied later in
    /// <see cref="BuildTransport"/>). A configured service account is a required execution
    /// identity: when the token cannot be acquired the server is rejected (fail closed) rather
    /// than called with the static token or anonymously under a different identity.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The referenced ServiceAccountConfiguration yields no token (misconfigured or identity
    /// server unavailable).
    /// </exception>
    internal static async Task<IList<McpServerConfig>> ApplyServiceAccountTokensAsync(
        IList<McpServerConfig> servers,
        IServiceAccountTokenService tokenService,
        IMeshEtlContext etlContext,
        INodeContext nodeContext,
        CancellationToken ct)
    {
        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            if (string.IsNullOrWhiteSpace(server.AuthServiceAccountConfigurationName))
            {
                continue;
            }

            string? token;
            try
            {
                token = await tokenService.GetAccessTokenAsync(
                    etlContext.TenantRepository, etlContext.TenantId,
                    server.AuthServiceAccountConfigurationName, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail closed: never call the server under the static token or anonymously when it
                // was configured to run as the service account.
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}': acquiring a service-account token from " +
                    $"'{server.AuthServiceAccountConfigurationName}' failed ({ex.GetType().Name}: {ex.Message}). " +
                    "The configured service account is required; the server is not called under another identity.",
                    ex);
            }

            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}': no service-account token could be acquired from " +
                    $"'{server.AuthServiceAccountConfigurationName}' (configuration missing, secret unusable, or the grant failed; " +
                    "see the adapter log for the cause). " +
                    "The configured service account is required; the server is not called under another identity.");
            }

            servers[i] = server with { BearerToken = token };
            nodeContext.Debug(
                $"MCP server '{server.Name}': using service-account bearer from " +
                $"'{server.AuthServiceAccountConfigurationName}' (overrides static BearerToken)");
        }

        return servers;
    }

    /// <summary>
    /// Builds the MCP client transport for a resolved server: a spawned stdio
    /// subprocess, or an HTTP/SSE transport carrying the composed auth headers
    /// (Bearer token plus any custom AdditionalHeaders). The caller opens the
    /// <c>McpClient</c> from the returned transport.
    /// </summary>
    internal static IClientTransport BuildTransport(McpServerConfig server) =>
        server.Transport switch
        {
            McpTransport.Stdio => BuildStdioTransport(server),
            McpTransport.Sse or McpTransport.Http => BuildHttpTransport(server),
            _ => throw new InvalidOperationException(
                $"Unsupported MCP transport: {server.Transport}")
        };

    private static StdioClientTransport BuildStdioTransport(McpServerConfig server)
    {
        if (string.IsNullOrEmpty(server.Command))
        {
            throw new InvalidOperationException(
                $"McpConfiguration '{server.Name}' uses Stdio transport but Command is empty. " +
                "Set Command to the executable (e.g. 'dotnet', 'npx') and Arguments to its " +
                "command-line arguments (one per line, or JSON array).");
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = ParseStdioArguments(server.Arguments),
            // SDK default ShutdownTimeout (5s) is fine — tool-call duration is bounded by
            // the outer cancellation token from the calling pipeline node.
        });
    }

    private static HttpClientTransport BuildHttpTransport(McpServerConfig server)
    {
        if (string.IsNullOrEmpty(server.Url))
        {
            throw new InvalidOperationException(
                $"McpConfiguration '{server.Name}' uses {server.Transport} transport but Url is empty. " +
                "Set Url to the MCP server endpoint (e.g. https://mcp.example.com).");
        }

        var endpoint = new Uri(server.Url);
        RequireHttpsForCredentials(server, endpoint);

        // HttpClientTransport unifies SSE and Streamable HTTP — TransportMode picks
        // between them. McpTransport.Http → Streamable HTTP (the newer spec, faster);
        // McpTransport.Sse → legacy Server-Sent Events. Default AutoDetect tries
        // Streamable HTTP first and falls back to SSE if the server doesn't support it.
        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
            TransportMode = server.Transport switch
            {
                McpTransport.Http => HttpTransportMode.StreamableHttp,
                McpTransport.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect,
            },
            // SDK default ConnectionTimeout (30s) is fine — tool-call duration is bounded
            // by the outer cancellation token from the calling pipeline node.
        };
        // Compose request headers from two sources:
        //   1. BearerToken  → Authorization: Bearer {token}   (the common case)
        //   2. AdditionalHeaders → arbitrary header map        (custom API-key
        //      headers like Exa's `x-api-key`, Devin's `X-Org-Id`, mTLS-proxy
        //      identity headers, etc.)
        // AdditionalHeaders is applied last so an explicit "Authorization" entry
        // there can override the BearerToken-derived one if a server needs a
        // non-Bearer scheme. Header values are secrets — never logged.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(server.BearerToken))
        {
            headers["Authorization"] = $"Bearer {server.BearerToken}";
        }
        foreach (var (key, value) in server.AdditionalHeaders)
        {
            headers[key] = value;
        }
        if (headers.Count > 0)
        {
            options.AdditionalHeaders = headers;
        }
        // Own HttpClient without automatic redirects: the SDK copies the credential headers onto
        // every request, so a redirect to another origin would hand them to that origin.
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        return new HttpClientTransport(options, httpClient, NullLoggerFactory.Instance, ownsHttpClient: true);
    }

    /// <summary>
    /// A bearer token or custom headers travel on every MCP request, so they may only go over
    /// TLS. Loopback stays allowed for local development servers; everything else must be https.
    /// Credential-free endpoints keep working over plain http.
    /// </summary>
    internal static void RequireHttpsForCredentials(McpServerConfig server, Uri endpoint)
    {
        var hasCredentials = !string.IsNullOrEmpty(server.BearerToken) || server.AdditionalHeaders.Count > 0;
        if (!hasCredentials || endpoint.Scheme == Uri.UriSchemeHttps || IsLoopback(endpoint))
        {
            return;
        }

        throw new InvalidOperationException(
            $"McpConfiguration '{server.Name}' sends credentials (BearerToken, AdditionalHeaders or a " +
            $"service-account token) to '{endpoint.Scheme}://{endpoint.Host}', which is not TLS. Use an https " +
            "URL, or remove the credentials for an unauthenticated endpoint.");
    }

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback || (IPAddress.TryParse(endpoint.Host, out var ip) && IPAddress.IsLoopback(ip));

    private static IList<string> ParseStdioArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new List<string>();
        var trimmed = arguments.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            // JSON-array form: ["--project", "./my-mcp"]
            return JArray.Parse(trimmed)
                .Select(t => t.ToString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        // One argument per line
        return arguments
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    /// <summary>
    /// Parses the <c>additionalHeaders</c> RecordArray attribute (a JSON array of
    /// <c>HttpHeader</c> records: <c>{ name, value, isSecret }</c>) into a header
    /// map, mirroring the <c>ValueOverride</c> shape. Entries with a blank name are
    /// skipped; later duplicates win. Header values are never logged.
    /// <para>
    /// At-rest encryption note: like <c>BearerToken</c>, header values are currently
    /// stored and shipped as plaintext (the Communication Controller serializes the
    /// config entity raw in <c>AdapterService</c>). The <c>isSecret</c> flag is
    /// carried for the planned encryption-at-rest slice — at which point the
    /// controller will decrypt secret values before shipping (the same
    /// <c>enc:v1</c> pattern <c>PoolService</c> uses for Helm <c>ValueOverride</c>),
    /// and this method will keep treating <c>value</c> as the resolved plaintext.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseHeaders(JArray? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return result;

        foreach (var entry in headers.OfType<JObject>())
        {
            var name = entry.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            result[name.Trim()] = entry.Value<string>("value") ?? string.Empty;
        }
        return result;
    }
}
