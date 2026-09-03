using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
///     The identity precedence of a pipeline execution (AB#5028): verified caller ▶ the adapter's
///     service account (AB#5027) ▶ the system context — resolved once, lazily, per execution.
/// </summary>
public class PipelineIdentityResolverTests
{
    private const string TenantId = "test-tenant";
    private const string ClientId = "octo-pipeline-sa-abc";
    private const string Issuer = "https://identity.example.com";

    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();

    private PipelineIdentityResolver CreateResolver(VerifiedPrincipal? verifiedPrincipal = null)
    {
        return new PipelineIdentityResolver(TenantId, verifiedPrincipal, _globalConfiguration, _tokenService,
            NullLogger.Instance);
    }

    private void GivenServiceAccountConfiguration(params string[] rawJson)
    {
        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(
                PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId))
            .Returns(rawJson);
    }

    private static string ServiceAccountJson(string clientId = ClientId, string issuer = Issuer,
        string? tenantId = TenantId)
    {
        var tenant = tenantId == null ? "null" : $"\"{tenantId}\"";
        return $$"""
                 {
                   "issuerUri": "{{issuer}}",
                   "clientId": "{{clientId}}",
                   "clientSecret": "s3cr3t",
                   "tenantId": {{tenant}}
                 }
                 """;
    }

    private void GivenIdentity(string subjectId, params string[] roles)
    {
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity(subjectId, roles, DateTime.UtcNow.AddMinutes(5))));
    }

    [Fact]
    public async Task VerifiedCallerWins_AndCostsNoTokenRequest()
    {
        GivenServiceAccountConfiguration(ServiceAccountJson());
        GivenIdentity("must-not-be-used", "ServiceRole");

        var principal = new VerifiedPrincipal("user-42", TenantId, "u@example.com", "U",
            ["Accounting", "Reader"]);

        var context = await CreateResolver(principal).ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.Equal("user-42", context.SubjectId);
        Assert.Equal(["Accounting", "Reader"], context.Roles);

        // The caller is already on the execution options; resolving them must never cost a round trip.
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WithoutACaller_TheServiceAccountIdentityIsUsed_RolesIncluded()
    {
        GivenServiceAccountConfiguration(ServiceAccountJson());
        GivenIdentity(ClientId, "CommunicationManagement", "Accounting");

        var context = await CreateResolver().ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.Equal(ClientId, context.SubjectId);
        Assert.Equal(["CommunicationManagement", "Accounting"], context.Roles);
    }

    [Fact]
    public async Task TheCredentialsHandedToTheTokenServiceComeFromTheProjectedConfiguration()
    {
        GivenServiceAccountConfiguration(ServiceAccountJson());
        GivenIdentity(ClientId, "Role");

        ServiceAccountCredentials? captured = null;
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Invokes((ServiceAccountCredentials c, CancellationToken _) => captured = c)
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity(ClientId, ["Role"], DateTime.UtcNow.AddMinutes(5))));

        await CreateResolver().ResolveAsync();

        Assert.NotNull(captured);
        Assert.Equal(Issuer, captured!.IssuerUri);
        Assert.Equal(ClientId, captured.ClientId);
        Assert.Equal("s3cr3t", captured.ClientSecret);
        Assert.Equal(TenantId, captured.TenantId);
    }

    [Fact]
    public async Task WithoutATenantIdOnTheConfiguration_ThePipelinesOwnTenantIsUsed()
    {
        // The grant needs acr_values=tenant:X, and an unset optional CK attribute arrives as null.
        GivenServiceAccountConfiguration(ServiceAccountJson(tenantId: null));
        GivenIdentity(ClientId, "Role");

        ServiceAccountCredentials? captured = null;
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Invokes((ServiceAccountCredentials c, CancellationToken _) => captured = c)
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity(ClientId, ["Role"], DateTime.UtcNow.AddMinutes(5))));

        await CreateResolver().ResolveAsync();

        Assert.Equal(TenantId, captured!.TenantId);
    }

    [Fact]
    public async Task ResolvingTwiceCostsOneTokenRequest()
    {
        GivenServiceAccountConfiguration(ServiceAccountJson());
        GivenIdentity(ClientId, "Role");

        var resolver = CreateResolver();
        var first = await resolver.ResolveAsync();
        var second = await resolver.ResolveAsync();

        Assert.Same(first, second);
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NothingIsResolvedUntilTheFirstSession()
    {
        GivenServiceAccountConfiguration(ServiceAccountJson());
        GivenIdentity(ClientId, "Role");

        // Constructing the resolver is what every execution does; an event trigger firing hundreds of
        // times a second must not pay for an identity it never uses.
        _ = CreateResolver();

        A.CallTo(() => _globalConfiguration.GetAllRawJsonByCkTypeId(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task WithoutACallerAndWithoutAServiceAccount_TheSystemContextIsUsed()
    {
        // The pre-AB#5027 fleet, and every tenant until provisioning has run. Changing this would take
        // the whole fleet down.
        GivenServiceAccountConfiguration();

        var context = await CreateResolver().ResolveAsync();

        Assert.True(context.IsSystem);
    }

    [Fact]
    public async Task AnIncompleteServiceAccountConfigurationIsIgnored()
    {
        // Only a missing ClientId makes a configuration unusable (AB#5115) — there is no account to
        // act as. Everything else has a default.
        GivenServiceAccountConfiguration("""{ "issuerUri": "https://identity.example.com" }""");

        var context = await CreateResolver().ResolveAsync();

        Assert.True(context.IsSystem);
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
            A<ServiceAccountCredentials>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AConfigurationWithoutAnIssuerIsNotIgnored()
    {
        // AB#5115: an empty IssuerUri means "the adapter's own installation" and is resolved by the
        // token service — treating it as damage here would silently drop the configured identity and
        // fall back to the system context, which bypasses data permissions entirely.
        GivenServiceAccountConfiguration($$"""
            { "clientId": "{{ClientId}}", "clientSecret": null, "tenantId": null }
            """);
        GivenIdentity(ClientId, "Accounting");

        ServiceAccountCredentials? captured = null;
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Invokes((ServiceAccountCredentials c, CancellationToken _) => captured = c)
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity(ClientId, ["Accounting"], DateTime.UtcNow.AddMinutes(5))));

        var context = await CreateResolver().ResolveAsync();

        Assert.False(context.IsSystem);
        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured!.IssuerUri);
        Assert.Null(captured.ClientSecret);
        Assert.Equal(TenantId, captured.TenantId);
    }

    [Fact]
    public async Task AFailedTokenAcquisitionFailsTheExecutionInsteadOfFallingBackToSystem()
    {
        // 🔴 The load-bearing decision. RtSecurityContext.System bypasses data-level permissions
        // entirely, so a fallback would fail OPEN: an identity-service outage would silently widen
        // every read and leave every write unstamped, indistinguishable from a correct run.
        GivenServiceAccountConfiguration(ServiceAccountJson());
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(null));

        var exception = await Assert.ThrowsAnyAsync<PipelineExecutionException>(
            async () => await CreateResolver().ResolveAsync());

        Assert.Contains(ClientId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("system context", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeveralServiceAccountsArePickedDeterministically()
    {
        // The controller normally projects exactly one, but a hand-linked pipeline can carry more —
        // and every pod must then agree on the same one.
        GivenServiceAccountConfiguration(
            ServiceAccountJson(clientId: "sa-zulu"),
            ServiceAccountJson(clientId: "sa-alpha"));
        GivenIdentity("resolved", "Role");

        ServiceAccountCredentials? captured = null;
        A.CallTo(() => _tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Invokes((ServiceAccountCredentials c, CancellationToken _) => captured = c)
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity("resolved", ["Role"], DateTime.UtcNow.AddMinutes(5))));

        await CreateResolver().ResolveAsync();

        Assert.Equal("sa-alpha", captured!.ClientId);
    }

    [Fact]
    public async Task AnUnreadableConfigurationDoesNotFailTheExecution()
    {
        // A configuration that cannot be parsed is not the same statement as "this pipeline runs under
        // an account": nothing usable was configured, so the legacy system path applies.
        GivenServiceAccountConfiguration("{ this is not json");

        var context = await CreateResolver().ResolveAsync();

        Assert.True(context.IsSystem);
    }
}
