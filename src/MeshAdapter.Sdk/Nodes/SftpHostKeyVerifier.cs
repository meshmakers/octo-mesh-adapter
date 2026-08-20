namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Compares a configured host key fingerprint against the one a server presented. Base64 is
/// case sensitive, so the comparison is ordinal; only the decorations people copy along with a
/// fingerprint are normalised away.
/// </summary>
public static class SftpHostKeyVerifier
{
    private const string Sha256Prefix = "SHA256:";

    /// <summary>
    /// True when the presented key may be trusted. An unset expectation trusts any key, which
    /// keeps configurations written before this option existed working unchanged.
    /// </summary>
    /// <param name="expectedFingerprint">The configured fingerprint, or null when none is configured</param>
    /// <param name="presentedFingerprintSha256">The SHA-256 fingerprint the server presented</param>
    /// <returns>True when the connection may proceed</returns>
    public static bool IsTrusted(string? expectedFingerprint, string presentedFingerprintSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return true;
        }

        return string.Equals(Normalize(expectedFingerprint), Normalize(presentedFingerprintSha256),
            StringComparison.Ordinal);
    }

    private static string Normalize(string fingerprint)
    {
        var value = fingerprint.Trim();

        if (value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[Sha256Prefix.Length..];
        }

        return value.TrimEnd('=');
    }
}
