using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
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
        IReadOnlyDictionary<string, string> AdditionalHeaders);

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

            var transportToken = doc["transport"];
            var transport = transportToken?.Type switch
            {
                JTokenType.Integer => (McpTransport)transportToken.Value<int>(),
                JTokenType.String => Enum.TryParse<McpTransport>(
                    transportToken.Value<string>()!, ignoreCase: true, out var t) ? t : McpTransport.Sse,
                _ => McpTransport.Sse
            };

            var additionalHeaders = ParseHeaders(doc["additionalHeaders"] as JArray);

            result.Add(new McpServerConfig(
                Name: name,
                Url: doc.Value<string>("url"),
                Transport: transport,
                Command: doc.Value<string>("command"),
                Arguments: doc.Value<string>("arguments"),
                BearerToken: doc.Value<string>("bearerToken"),
                AdditionalHeaders: additionalHeaders));

            // Header *names* are safe to log (values are secrets and are never logged).
            var headerInfo = additionalHeaders.Count > 0
                ? $", additionalHeaders=[{string.Join(", ", additionalHeaders.Keys)}]"
                : string.Empty;
            nodeContext.Debug($"McpConfiguration '{name}' loaded: transport={transport}{headerInfo}");
        }
        return result;
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
        return new HttpClientTransport(options);
    }

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
