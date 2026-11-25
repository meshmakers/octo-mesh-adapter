using System.Security.Cryptography;
using System.Text;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     Signed RS256 tokens for identity-service stubs (AB#9776: the adapter verifies the signature, issuer and tenant
///     of every service-account token). A stub publishes <see cref="Jwks" /> at its <c>jwks_uri</c>. Signatures are
///     deterministic (PKCS#1 v1.5), so the same claims always give the same token string.
/// </summary>
internal static class SignedTestJwt
{
    private const string KeyId = "stub-signing-key";

    /// <summary>A far-future expiry, fixed so that equal claims give equal tokens.</summary>
    internal static readonly long FixedExpiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();

    private static readonly RSA SigningKey = RSA.Create(2048);

    /// <summary>The JWKS document of the stub's signing key.</summary>
    internal static string Jwks()
    {
        var parameters = SigningKey.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{KeyId}}","n":"{{Segment(parameters.Modulus!)}}","e":"{{Segment(parameters.Exponent!)}}"}]}""";
    }

    /// <summary>A signed token with the given claims; <paramref name="id" /> becomes <c>jti</c>.</summary>
    internal static string Create(string issuer, string tenantId, string clientId, string id)
    {
        var header = Segment(Encoding.UTF8.GetBytes($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}"""));
        var payload = Segment(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{issuer}}","tenant_id":"{{tenantId}}","client_id":"{{clientId}}","jti":"{{id}}","exp":{{FixedExpiry}}}"""));
        var signingInput = header + "." + payload;
        var signature = SigningKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Segment(signature);
    }

    /// <summary>
    ///     A token with any payload (serialized as JSON), signed with the stub's key — or, with
    ///     <paramref name="foreignKey" />, with a key the stub's <see cref="Jwks" /> does not publish.
    /// </summary>
    internal static string Sign(object payload, bool foreignKey = false)
    {
        var header = Segment(Encoding.UTF8.GetBytes($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}"""));
        var body = Segment(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = header + "." + body;
        var signature = (foreignKey ? ForeignKey : SigningKey).SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Segment(signature);
    }

    private static readonly RSA ForeignKey = RSA.Create(2048);

    private static string Segment(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
