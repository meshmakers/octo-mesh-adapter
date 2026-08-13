using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.IdentityModel.Tokens;

namespace MeshAdapter.Sdk.Tests.Services.HttpRequests;

/// <summary>
/// The codes are a wire contract: a client decides whether refreshing its access token can
/// possibly help by reading them, so they are pinned here rather than left to whatever wording
/// Microsoft.IdentityModel currently uses.
/// </summary>
public class BearerChallengeTests
{
    [Fact]
    public void CodeFor_ExpiredToken_IsTheOnlyRefreshableOne()
    {
        Assert.Equal("token_expired", BearerChallenge.CodeFor(new SecurityTokenExpiredException()));
    }

    [Theory]
    [InlineData(typeof(SecurityTokenInvalidIssuerException), "issuer_invalid")]
    [InlineData(typeof(SecurityTokenInvalidAudienceException), "audience_invalid")]
    [InlineData(typeof(SecurityTokenSignatureKeyNotFoundException), "signature_key_not_found")]
    [InlineData(typeof(SecurityTokenInvalidSignatureException), "signature_invalid")]
    [InlineData(typeof(SecurityTokenNotYetValidException), "token_not_yet_valid")]
    public void CodeFor_ServerSideCauses_AreNamedAndAreNotTokenExpired(Type failureType, string expected)
    {
        var failure = (Exception)Activator.CreateInstance(failureType)!;

        var code = BearerChallenge.CodeFor(failure);

        Assert.Equal(expected, code);
        // A client refreshing on any of these would spend one grant per user action with no way
        // of ever succeeding - that is the defect this challenge exists to end.
        Assert.NotEqual(BearerChallenge.TokenExpired, code);
    }

    [Fact]
    public void CodeFor_AnUnknownFailure_FallsBackToAGenericCode()
    {
        Assert.Equal("token_invalid", BearerChallenge.CodeFor(new InvalidOperationException()));
    }

    [Fact]
    public void ForInvalidToken_WithoutAFailure_IsABareChallenge()
    {
        // No failure means nothing rejected a token, i.e. none was sent. RFC 6750 §3.1 asks for
        // a bare challenge there rather than an error code describing a token that never existed.
        Assert.Equal("Bearer", BearerChallenge.ForInvalidToken(null));
    }

    /// <remarks>
    /// The adapter answers this when no identity service is configured: the scheme is left unwired,
    /// so a presented token reaches the gate unevaluated and without a failure to describe. It must
    /// not fall back to the bare challenge - a client reading no code treats the denial as "reason
    /// unknown" and refreshes, which is precisely the wasted grant per user action this all exists
    /// to stop, against an adapter that cannot validate any token at all.
    /// </remarks>
    [Fact]
    public void ForInvalidToken_CredentialsNobodyEvaluated_IsNamedRatherThanLeftBare()
    {
        var challenge = BearerChallenge.ForInvalidToken(null, credentialsPresented: true);

        Assert.Contains("error_code=\"token_not_evaluated\"", challenge);
        Assert.NotEqual(BearerChallenge.NoCredentials, challenge);
    }

    [Fact]
    public void ForInvalidToken_CarriesBothTheProseAndTheStableCode()
    {
        var challenge = BearerChallenge.ForInvalidToken(new SecurityTokenExpiredException());

        Assert.StartsWith("Bearer ", challenge);
        Assert.Contains("error=\"invalid_token\"", challenge);
        Assert.Contains("error_description=\"The access token has expired\"", challenge);
        Assert.Contains("error_code=\"token_expired\"", challenge);
    }

    [Fact]
    public void ForInsufficientScope_NamesTheRequiredRoles()
    {
        var challenge = BearerChallenge.ForInsufficientScope(BearerChallenge.RoleMissing,
            ["TenantAdmin", "Operator"]);

        Assert.Contains("error=\"insufficient_scope\"", challenge);
        Assert.Contains("error_code=\"role_missing\"", challenge);
        Assert.Contains("required_roles=\"TenantAdmin Operator\"", challenge);
    }

    [Fact]
    public void ForInsufficientScope_SkipsBlankRoles()
    {
        // The gate skips a blank entry when deciding, so naming it here would send the operator
        // looking for a role that grants nothing.
        var challenge = BearerChallenge.ForInsufficientScope(BearerChallenge.RoleMissing,
            ["  ", "TenantAdmin"]);

        Assert.Contains("required_roles=\"TenantAdmin\"", challenge);
    }

    [Fact]
    public void ForInsufficientScope_WithoutRoles_OmitsTheParameter()
    {
        var challenge = BearerChallenge.ForInsufficientScope(BearerChallenge.TenantMismatch, []);

        Assert.Contains("error_code=\"tenant_mismatch\"", challenge);
        Assert.DoesNotContain("required_roles", challenge);
    }
}
