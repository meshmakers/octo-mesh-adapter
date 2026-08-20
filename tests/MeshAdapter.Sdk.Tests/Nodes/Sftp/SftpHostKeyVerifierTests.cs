using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Sftp;

namespace MeshAdapter.Sdk.Tests.Nodes.Sftp;

public class SftpHostKeyVerifierTests
{
    // Shape of a real SHA-256 fingerprint: 43 characters of non-padded base64, which is what
    // ssh-keygen -lf prints after the SHA256: prefix.
    private const string Presented = "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTrusted_NoFingerprintConfigured_AcceptsAnyKey(string? expected)
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(expected, Presented));
    }

    [Fact]
    public void IsTrusted_ExactMatch_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(Presented, Presented));
    }

    [Fact]
    public void IsTrusted_SshPrefixedFingerprint_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted("SHA256:" + Presented, Presented));
    }

    [Fact]
    public void IsTrusted_PaddedFingerprint_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted(Presented + "=", Presented));
    }

    [Fact]
    public void IsTrusted_SurroundingWhitespace_Accepts()
    {
        Assert.True(SftpHostKeyVerifier.IsTrusted("  " + Presented + "  ", Presented));
    }

    [Fact]
    public void IsTrusted_DifferentFingerprint_Refuses()
    {
        Assert.False(SftpHostKeyVerifier.IsTrusted("2Fx1PLbtSbXBRCGCXFYRVJHhWkmB4CvKjTuIhFR2hAo", Presented));
    }

    [Fact]
    public void IsTrusted_CaseDiffersInBase64Body_Refuses()
    {
        // Base64 is case sensitive: two fingerprints differing only in case are different keys.
        Assert.False(SftpHostKeyVerifier.IsTrusted(Presented.ToLowerInvariant(), Presented));
    }
}
