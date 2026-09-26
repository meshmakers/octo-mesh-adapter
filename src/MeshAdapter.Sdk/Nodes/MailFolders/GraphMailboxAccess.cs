using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     App-only access to a Microsoft 365 mailbox through Microsoft Graph, shared by
///     <c>ListMailFolders@1</c> and adoptable by <c>FromMicrosoftGraphEmail@1</c> (AB#5370): the
///     client-credentials token the trigger acquires, repeated line for line, plus a folder walk that
///     spells every path the way <see cref="MailFolderPathSyntax" /> defines it. A failure is a
///     <see cref="MailFolderListingException" /> naming its cause — wrong app credentials, missing
///     <c>Mail.Read</c> consent, unknown mailbox — rather than a bare HTTP status.
/// </summary>
internal static class GraphMailboxAccess
{
    internal const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>
    ///     Graph lists at most this many folders per page; the walk follows <c>@odata.nextLink</c>
    ///     for the rest.
    /// </summary>
    private const int PageSize = 100;

    /// <summary>
    ///     The <c>System.Communication/MicrosoftGraphConfiguration</c> entity as the trigger reads it —
    ///     the same property names, so <c>IGlobalConfiguration.GetValue</c> binds the same entity.
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Global
    internal record GraphAppCredentials
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        public required string AzureTenantId { get; init; }
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global

        /// <summary>A record prints its members; the secret is never one of them.</summary>
        public override string ToString() =>
            $"{nameof(GraphAppCredentials)} {{ {nameof(AzureTenantId)} = {AzureTenantId}, {nameof(ClientId)} = {ClientId}, {nameof(ClientSecret)} = *** }}";
    }

    /// <summary>One folder as Graph reports it, with what the walk needs to go on.</summary>
    internal sealed record GraphFolder(string Id, string DisplayName, int ChildFolderCount);

    /// <summary>
    ///     Acquires an app-only token with the client-credentials grant, the way the trigger does.
    /// </summary>
    internal static async Task<string> AcquireAppTokenAsync(IHttpClientFactory httpClientFactory,
        GraphAppCredentials credentials, CancellationToken cancellationToken)
    {
        using var client = CreateClient(httpClientFactory);
        var tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(credentials.AzureTenantId)}/oauth2/v2.0/token";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["scope"] = GraphScope,
            ["grant_type"] = "client_credentials"
        });

        HttpResponseMessage response;
        string body;
        try
        {
            response = await client.PostAsync(tokenUrl, content, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new MailFolderListingException(
                $"Microsoft 365 sign-in (login.microsoftonline.com) is not reachable from the adapter: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MailFolderListingException(DescribeTokenFailure((int)response.StatusCode, body));
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("access_token", out var token) &&
                token.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(token.GetString()))
            {
                return token.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Reported below as an answer without a token.
        }

        throw new MailFolderListingException(
            "Microsoft 365 sign-in answered without an access token — check the Azure tenant ID, client ID and client secret.");
    }

    /// <summary>
    ///     The token endpoint's refusal, for the operator who typed the app registration: the
    ///     <c>error</c> code and its description name the wrong one of the three values (an unknown
    ///     tenant, an unknown client ID, an invalid or expired secret).
    /// </summary>
    internal static string DescribeTokenFailure(int statusCode, string body)
    {
        var (code, description) = ParseOAuthError(body);
        var detail = string.IsNullOrWhiteSpace(description) ? code : description;
        return $"Microsoft 365 rejected the app registration's credentials (HTTP {statusCode}" +
               (string.IsNullOrWhiteSpace(code) ? "" : $", {code}") + "): " +
               (string.IsNullOrWhiteSpace(detail) ? "no details were given. " : $"{detail} ") +
               "Check the Azure tenant ID, client ID and client secret.";
    }

    /// <summary>
    ///     A Graph refusal while listing <paramref name="mailbox" />: 401 is the token, 403 is the
    ///     missing <c>Mail.Read</c> application permission or its admin consent, 404 is a mailbox that
    ///     does not exist in that tenant.
    /// </summary>
    internal static string DescribeGraphFailure(int statusCode, string body, string mailbox)
    {
        var (code, message) = ParseGraphError(body);
        var detail = string.IsNullOrWhiteSpace(message) ? "" : $" ({message})";
        return statusCode switch
        {
            401 => $"Microsoft Graph refused the access token while reading mailbox '{mailbox}' (401){detail}. " +
                   "Check the Azure tenant ID, client ID and client secret of the app registration.",
            403 => $"Microsoft Graph denied access to mailbox '{mailbox}' (403" +
                   (string.IsNullOrWhiteSpace(code) ? "" : $", {code}") + $"){detail}. " +
                   "The app registration needs the Mail.Read APPLICATION permission with admin consent granted.",
            404 => $"Mailbox '{mailbox}' was not found in the Microsoft 365 tenant (404" +
                   (string.IsNullOrWhiteSpace(code) ? "" : $", {code}") + $"){detail}. " +
                   "Check the address — it has to be a user or shared mailbox of the tenant the app registration belongs to.",
            _ => $"Microsoft Graph answered HTTP {statusCode} while listing the folders of mailbox '{mailbox}'{detail}."
        };
    }

    /// <summary>
    ///     Walks the mailbox's folder tree depth-first (root folders, then each one's
    ///     <c>childFolders</c>), following Graph's paging, and spells each path with
    ///     <see cref="MailFolderPathSyntax.JoinGraphPath(string?, string)" />. <paramref name="client" />
    ///     carries the bearer token.
    /// </summary>
    internal static async Task<MailFolderTree.Listing> ListFoldersAsync(HttpClient client, string mailbox,
        int maxFolders, CancellationToken cancellationToken)
    {
        var user = Uri.EscapeDataString(mailbox);
        var select = $"$top={PageSize}&$select=id,displayName,childFolderCount";

        var roots = await ReadAllPagesAsync(client, $"{GraphBaseUrl}/users/{user}/mailFolders?{select}", mailbox,
            cancellationToken);

        return await MailFolderTree.WalkDepthFirstAsync<GraphFolder>(
            roots,
            async folder => folder.ChildFolderCount <= 0
                ? []
                : await ReadAllPagesAsync(client,
                    $"{GraphBaseUrl}/users/{user}/mailFolders/{Uri.EscapeDataString(folder.Id)}/childFolders?{select}",
                    mailbox, cancellationToken),
            folder => folder.DisplayName,
            (folder, parent) => MailFolderPathSyntax.JoinGraphPath(parent?.Path, folder.DisplayName),
            maxFolders);
    }

    internal static HttpClient CreateGraphClient(IHttpClientFactory httpClientFactory, string accessToken)
    {
        var client = CreateClient(httpClientFactory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>
    ///     The node's cancellation token is the ONLY time budget. <see cref="HttpClient.Timeout" />
    ///     defaults to 100 s and cancels with a bare <see cref="TaskCanceledException" /> that
    ///     nobody can tell from a caller's cancellation — with a configured budget above 100 s the
    ///     operator would have seen that instead of the "did not finish within …" wording.
    /// </summary>
    private static HttpClient CreateClient(IHttpClientFactory httpClientFactory)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static async Task<IReadOnlyList<GraphFolder>> ReadAllPagesAsync(HttpClient client, string url,
        string mailbox, CancellationToken cancellationToken)
    {
        var folders = new List<GraphFolder>();
        string? next = url;
        while (next != null)
        {
            string body;
            HttpStatusCode status;
            try
            {
                using var response = await client.GetAsync(next, cancellationToken);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new MailFolderListingException(
                    $"Microsoft Graph is not reachable from the adapter: {ex.Message}", ex);
            }

            if ((int)status < 200 || (int)status >= 300)
            {
                throw new MailFolderListingException(DescribeGraphFailure((int)status, body, mailbox));
            }

            next = ParseFolderPage(body, folders);
        }

        return folders;
    }

    /// <summary>Appends a page's folders and returns its <c>@odata.nextLink</c>, if any.</summary>
    internal static string? ParseFolderPage(string body, List<GraphFolder> folders)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var name = item.TryGetProperty("displayName", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(id) || name == null)
                {
                    continue;
                }

                var childCount = item.TryGetProperty("childFolderCount", out var countProp) &&
                                 countProp.ValueKind == JsonValueKind.Number
                    ? countProp.GetInt32()
                    // Unknown means "look": a folder whose count is missing is asked for children
                    // rather than silently treated as a leaf.
                    : 1;
                folders.Add(new GraphFolder(id, name, childCount));
            }
        }

        return root.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String
            ? link.GetString()
            : null;
    }

    private static (string? Code, string? Description) ParseOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            var description = root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            return (code, FirstLine(description));
        }
        catch (JsonException)
        {
            return (null, FirstLine(body));
        }
    }

    private static (string? Code, string? Message) ParseGraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                return (code, FirstLine(message));
            }

            return (null, null);
        }
        catch (JsonException)
        {
            return (null, FirstLine(body));
        }
    }

    /// <summary>
    ///     Only the first line, and not more than a few hundred characters: an AADSTS description
    ///     carries a trace id, a timestamp and a documentation link after its first sentence, and a
    ///     raw HTML error page would otherwise land under a button on the settings page.
    /// </summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n', '\r')[0].Trim();
        return line.Length <= 300 ? line : line[..300] + "…";
    }
}
