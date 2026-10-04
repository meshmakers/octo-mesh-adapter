using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.MailFolders;

/// <summary>
/// The mail clients soft-fail an UNAVAILABLE revocation service and nothing else: a developer Mac
/// reports only <c>RevocationStatusUnknown</c> for imap.gmail.com and used to be refused outright,
/// while a revoked, untrusted, expired or misnamed certificate must stay refused.
/// </summary>
public class MailServerCertificateValidationTests
{
    [Fact]
    public void CleanChain_IsAccepted()
    {
        Assert.True(MailServerCertificateValidation.Validate(null, null, SslPolicyErrors.None, NullLogger.Instance));
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown)]
    [InlineData(X509ChainStatusFlags.OfflineRevocation)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)]
    public void OnlyUnavailableRevocation_IsAccepted(X509ChainStatusFlags status)
    {
        Assert.True(MailServerCertificateValidation.IsOnlyUnavailableRevocation(
            SslPolicyErrors.RemoteCertificateChainErrors, [status, status]));
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.Revoked)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot)]
    [InlineData(X509ChainStatusFlags.NotTimeValid)]
    [InlineData(X509ChainStatusFlags.PartialChain)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.Revoked)]
    [InlineData(X509ChainStatusFlags.NoError)]
    public void AnyOtherChainStatus_IsRefused(X509ChainStatusFlags status)
    {
        Assert.False(MailServerCertificateValidation.IsOnlyUnavailableRevocation(
            SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.RevocationStatusUnknown, status]));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void AnyOtherPolicyError_IsRefused_EvenWithUnavailableRevocation(SslPolicyErrors errors)
    {
        Assert.False(MailServerCertificateValidation.IsOnlyUnavailableRevocation(
            errors, [X509ChainStatusFlags.RevocationStatusUnknown]));
    }

    [Fact]
    public void ChainErrorWithoutAnyStatus_IsRefused()
    {
        Assert.False(MailServerCertificateValidation.IsOnlyUnavailableRevocation(
            SslPolicyErrors.RemoteCertificateChainErrors, []));
        Assert.False(MailServerCertificateValidation.IsOnlyUnavailableRevocation(
            SslPolicyErrors.RemoteCertificateChainErrors, null));
        Assert.False(MailServerCertificateValidation.Validate(null, null,
            SslPolicyErrors.RemoteCertificateChainErrors, NullLogger.Instance));
    }
}
