using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
///     AB#9776: which issuer names count as the adapter's own identity service, and where token requests go.
///     The bypass attempts are the abuse cases of the bug: a name that only LOOKS like the own issuer.
/// </summary>
public class OwnIssuerPolicyTests
{
    private static OwnIssuerPolicy Policy(string? issuerUri = "https://identity.example.com",
        string authorityUrl = "https://identity.example.com/", params string[] additional)
    {
        return new OwnIssuerPolicy(new AdapterOptions { IssuerUri = issuerUri },
            new MeshAdapterConfiguration { AuthorityUrl = authorityUrl, AdditionalValidIssuers = additional });
    }

    [Theory]
    [InlineData("https://identity.example.com")]
    [InlineData("https://identity.example.com/")]
    [InlineData("https://identity.example.com//")]
    [InlineData("HTTPS://Identity.Example.COM")]
    [InlineData("https://identity.example.com:443/")]
    [InlineData("  https://identity.example.com  ")]
    [InlineData("https://localhost:5003/")] // split-horizon name from Adapter:AdditionalValidIssuers
    public void TheOwnIssuerIsRecognisedUnderEveryEquivalentSpelling(string issuer)
    {
        Assert.True(Policy(additional: "https://localhost:5003").IsOwnIssuer(issuer));
    }

    [Theory]
    [InlineData("http://identity.example.com")]
    [InlineData("https://identity.example.com:8443")]
    [InlineData("https://user:pass@identity.example.com")]
    [InlineData("https://identity.example.com@evil.example.net")]
    [InlineData("https://identity.example.com.evil.example.net")]
    [InlineData("https://evil.example.net/identity.example.com")]
    [InlineData("https://identity.example.com/tenant")]
    [InlineData("https://identity.example.com/?x=1")]
    [InlineData("https://identity.example.com/#x")]
    [InlineData("ftp://identity.example.com")]
    [InlineData("file:///identity.example.com")]
    [InlineData("/connect/token")]
    [InlineData("identity.example.com")]
    [InlineData("{{service.authority}}")] // the template means "own" only as a CONFIGURED value, never as iss
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotTheOwnIssuer(string? issuer)
    {
        Assert.False(Policy().IsOwnIssuer(issuer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{{service.authority}}")]
    [InlineData("{{Service.Authority}}")]
    [InlineData(" {{service.authority}} ")]
    [InlineData("{{ service.authority }}")] // the controller's IssuerUriTokenPattern accepts inner blanks too
    [InlineData("https://identity.example.com/")]
    public void AConfiguredIssuerMeaningTheOwnInstallationIsAccepted(string? configured)
    {
        Assert.True(Policy().IsOwnConfiguredIssuer(configured));
    }

    [Theory]
    [InlineData("{{service.authority}}/x")]
    [InlineData("{{service.authority}}{{service.authority}}")]
    [InlineData("{{service.identity}}")]
    [InlineData("https://evil.example.net")]
    public void AConfiguredForeignIssuerIsRefused(string configured)
    {
        Assert.False(Policy().IsOwnConfiguredIssuer(configured));
    }

    [Fact]
    public void RequestsGoToAdapterIssuerUriFirst()
    {
        Assert.Equal("https://internal.example.com",
            Policy(issuerUri: " https://internal.example.com ", authorityUrl: "https://identity.example.com").Endpoint);
    }

    [Fact]
    public void WithoutAdapterIssuerUriRequestsGoToTheAuthority()
    {
        Assert.Equal("https://identity.example.com", Policy(issuerUri: null,
            authorityUrl: "https://identity.example.com").Endpoint);
    }

    [Fact]
    public void WithoutAnyOwnIssuerThereIsNoEndpoint()
    {
        var policy = Policy(issuerUri: "", authorityUrl: "");

        Assert.Null(policy.Endpoint);
        Assert.False(policy.IsOwnIssuer("https://identity.example.com"));
    }
}
