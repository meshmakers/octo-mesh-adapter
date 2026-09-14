using System.Text;
using System.Text.Json;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Reads the claims the adapter needs out of a JWT it has just fetched itself.
/// </summary>
/// <remarks>
///     <b>The signature is deliberately not verified.</b> The token arrives as the direct answer to
///     this process's own client-credentials request, over TLS, and never passes through a caller — so
///     there is no party between issuer and reader whose tampering a signature check could catch.
///     Verifying it would mean fetching and caching the issuer's JWKS on a path that runs inside
///     pipeline execution, for no security gain. The same reasoning is written down for the MCP
///     server's exchanged tokens in <c>octo-mcp-service</c>.
///     <para>
///         Written by hand rather than with <c>JwtSecurityTokenHandler</c>: the adapter does not
///         reference a JWT package outside the ASP.NET bearer handler, and reading three claims out of
///         a base64url segment is less code than the dependency.
///     </para>
/// </remarks>
internal static class JwtPayloadReader
{
    /// <summary>
    ///     Parses the payload segment of <paramref name="token" />.
    /// </summary>
    /// <param name="token">A compact-serialisation JWT.</param>
    /// <param name="claims">The parsed claims on success.</param>
    /// <returns><c>true</c> when the token had a readable JSON payload.</returns>
    internal static bool TryRead(string? token, out JwtClaims claims)
    {
        claims = JwtClaims.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(segments[1]));
            payload = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        claims = new JwtClaims(
            ReadString(payload, "sub"),
            ReadString(payload, "client_id"),
            ReadRoles(payload),
            ReadExpiry(payload),
            // AB#4924: the tenant the token was actually issued for. A pool member has to be able to
            // verify it before acting as a borrower — since AB#5077 a request without acr_values is
            // issued for the SYSTEM tenant, which is a 403 if you are lucky and a cross-tenant read if
            // you are not.
            ReadString(payload, "tenant_id"));
        return true;
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var builder = new StringBuilder(segment.Length + 3);
        foreach (var c in segment)
        {
            builder.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c
            });
        }

        // Base64 needs the padding the URL-safe encoding drops.
        builder.Append('=', (4 - builder.Length % 4) % 4);
        return Convert.FromBase64String(builder.ToString());
    }

    private static string? ReadString(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    ///     A single role is emitted as a bare string and several as an array — both shapes are normal
    ///     JWT, so both have to be read or an account with exactly one role would look role-less.
    /// </summary>
    private static IReadOnlyList<string> ReadRoles(JsonElement payload)
    {
        if (!payload.TryGetProperty("role", out var role))
        {
            return [];
        }

        switch (role.ValueKind)
        {
            case JsonValueKind.String:
                var single = role.GetString();
                return string.IsNullOrEmpty(single) ? [] : [single];
            case JsonValueKind.Array:
                return role.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToArray();
            default:
                return [];
        }
    }

    private static DateTime? ReadExpiry(JsonElement payload)
    {
        return payload.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    /// <summary>The claims read out of a JWT payload.</summary>
    /// <param name="Subject"><c>sub</c>, absent on a client-credentials token.</param>
    /// <param name="ClientId"><c>client_id</c>.</param>
    /// <param name="Roles"><c>role</c>, normalised to a list.</param>
    /// <param name="ExpiresAtUtc"><c>exp</c> as UTC.</param>
    /// <param name="TenantId">
    ///     <c>tenant_id</c> — the tenant the token was issued for (AB#5032 stamps it on
    ///     client-credentials tokens too). Absent when the request carried no
    ///     <c>acr_values=tenant:X</c>, in which case the identity service issued for the system tenant.
    /// </param>
    internal sealed record JwtClaims(
        string? Subject,
        string? ClientId,
        IReadOnlyList<string> Roles,
        DateTime? ExpiresAtUtc,
        string? TenantId = null)
    {
        internal static readonly JwtClaims Empty = new(null, null, [], null);
    }
}
