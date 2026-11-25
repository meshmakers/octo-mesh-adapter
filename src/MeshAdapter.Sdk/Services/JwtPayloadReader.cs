using System.Text;
using System.Text.Json;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Reads the claims the adapter needs out of a JWT it has just fetched itself.
/// </summary>
/// <remarks>
///     <b>This reader does not verify the signature.</b> Service-account tokens are verified before it is called:
///     <c>ServiceAccountTokenService</c> sends every request to the adapter's own identity service only, checks the
///     signature against that service's published keys and compares <c>iss</c> and <c>tenant_id</c> (AB#9776).
///     Until AB#9776 the configured issuer was tenant-editable, so "the answer to our own request" was no proof of
///     origin. Other callers (the borrower token of a lease) read a token the adapter requested from its own,
///     configured identity service with its own credentials.
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
            ReadString(payload, "tenant_id"),
            // AB#9776: the issuer that minted the token — compared with the adapter's own issuer
            // before any other claim of a service-account token is used.
            ReadString(payload, "iss"),
            // AB#9767: the generation of the tenant the token was issued for — the cross-tenant guard compares it
            // with the target tenant's current generation. Emitted as a string or a number.
            ReadStringOrNumber(payload, "tenant_gen"));
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

    private static string? ReadStringOrNumber(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
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
    /// <param name="Issuer"><c>iss</c> — the issuer that minted the token (AB#9776).</param>
    /// <param name="TenantGeneration"><c>tenant_gen</c> — the generation of the token's tenant (AB#9767).</param>
    internal sealed record JwtClaims(
        string? Subject,
        string? ClientId,
        IReadOnlyList<string> Roles,
        DateTime? ExpiresAtUtc,
        string? TenantId = null,
        string? Issuer = null,
        string? TenantGeneration = null)
    {
        internal static readonly JwtClaims Empty = new(null, null, [], null);
    }
}
