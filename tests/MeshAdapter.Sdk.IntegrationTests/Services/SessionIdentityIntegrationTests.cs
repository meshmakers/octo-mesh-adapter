using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.IntegrationTests.Services;

/// <summary>
///     The AB#5028 session contract against the <b>real</b> <see cref="ITenantRepository" />.
/// </summary>
/// <remarks>
///     The unit suite fakes the repository with an <see cref="ISecureSessionFactory" /> face because
///     the production one has it — an assumption worth checking against the real thing, since
///     <see cref="TenantRepositorySecurityExtensions" /> degrades into a system session in silence for
///     a repository that lacks it. Here <c>session.GetSecurityContext()</c> is the truth, so these
///     tests read the identity back off the session the engine actually produced rather than off a
///     recorded call.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class SessionIdentityIntegrationTests(SystemFixture fixture) : IClassFixture<SystemFixture>
{
    private const string SubjectId = "integration-subject";
    private static readonly string[] Roles = ["Accounting", "Reader"];

    [Fact]
    public void TheProductionTenantRepositoryReallyImplementsTheSecureSessionFactory()
    {
        fixture.EnsureInitialized();

        // If this ever stops holding, every caller-scoped call site in the adapter silently degrades
        // to the system context and the unit suite keeps passing.
        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();

        tenantRepository.Should().BeAssignableTo<ISecureSessionFactory>();
    }

    [Fact]
    public async Task GetSystemSessionAsync_ProducesTheSystemContext()
    {
        fixture.EnsureInitialized();

        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository());

        using var session = await context.GetSystemSessionAsync();

        session.GetSecurityContext().IsSystem.Should().BeTrue();
    }

    [Fact]
    public void GetSystemSession_ProducesTheSystemContext()
    {
        fixture.EnsureInitialized();

        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository());

        using var session = context.GetSystemSession();

        session.GetSecurityContext().IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task GetScopedSessionAsync_CarriesTheVerifiedCallerOntoTheSession()
    {
        fixture.EnsureInitialized();

        var principal = new VerifiedPrincipal(SubjectId, "system", "u@example.com", "U", Roles);
        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository(),
            verifiedPrincipal: principal);

        using var session = await context.GetScopedSessionAsync();

        var securityContext = session.GetSecurityContext();
        securityContext.IsSystem.Should().BeFalse();
        securityContext.SubjectId.Should().Be(SubjectId);
        securityContext.Roles.Should().BeEquivalentTo(Roles);
    }

    [Fact]
    public void GetScopedSession_CarriesTheVerifiedCallerOntoTheSession()
    {
        fixture.EnsureInitialized();

        // The synchronous face the two Excel-import call sites use.
        var principal = new VerifiedPrincipal(SubjectId, "system", null, null, Roles);
        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository(),
            verifiedPrincipal: principal);

        using var session = context.GetScopedSession();

        session.GetSecurityContext().SubjectId.Should().Be(SubjectId);
    }

    [Fact]
    public async Task GetScopedSessionAsync_CarriesTheServiceAccountIdentityWhenThereIsNoCaller()
    {
        fixture.EnsureInitialized();

        // The AB#5027 case: no trigger-verified caller, so the execution acts as the adapter's own
        // service account — subject AND roles, both read off its token.
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity("octo-pipeline-sa-integration", Roles,
                    DateTime.UtcNow.AddMinutes(5))));

        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.GetAllRawJsonByCkTypeId(
                PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId))
            .Returns([
                """
                {
                  "issuerUri": "https://identity.example.com",
                  "clientId": "octo-pipeline-sa-integration",
                  "clientSecret": "s3cr3t",
                  "tenantId": "system"
                }
                """
            ]);

        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        var resolver = new PipelineIdentityResolver(tenantRepository.TenantId, null, globalConfiguration,
            tokenService, NullLogger.Instance);
        var context = CreateContext(tenantRepository, globalConfiguration, identityResolver: resolver);

        using var session = await context.GetScopedSessionAsync();

        var securityContext = session.GetSecurityContext();
        securityContext.IsSystem.Should().BeFalse();
        securityContext.SubjectId.Should().Be("octo-pipeline-sa-integration");
        securityContext.Roles.Should().BeEquivalentTo(Roles);
    }

    [Fact]
    public async Task WithoutACallerAndWithoutAResolver_TheScopedSessionIsTheSystemContext()
    {
        fixture.EnsureInitialized();

        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository());

        using var session = await context.GetScopedSessionAsync();

        session.GetSecurityContext().IsSystem.Should().BeTrue();
    }

    // =============================================================================================
    // AB#5029 — the identity semantics against the real session, where session.GetSecurityContext()
    // is the truth rather than a recorded call on a fake.
    // =============================================================================================

    [Fact]
    public async Task ACallerWithoutRolesProducesANonSystemSessionWithNoRoles()
    {
        fixture.EnsureInitialized();

        // The empty-intersection shape as the engine sees it. ForUser(sub, []) is NOT the system
        // context: it stamps RtCreatedBy and is subject to data permissions, so on a protected type it
        // delivers only what needs no role. A guard written as `if (!context.IsSystem)` would wave it
        // through, so both halves are asserted — this is the property the resolver's fail-closed
        // behaviour ultimately rests on.
        var principal = new VerifiedPrincipal(SubjectId, "system", "u@example.com", "U", []);
        var context = CreateContext(fixture.GetSystemContext().GetSystemTenantRepository(),
            verifiedPrincipal: principal);

        using var session = await context.GetScopedSessionAsync();

        var securityContext = session.GetSecurityContext();
        securityContext.IsSystem.Should().BeFalse();
        securityContext.SubjectId.Should().Be(SubjectId);
        securityContext.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task AServiceAccountWithoutRolesProducesANonSystemSessionWithNoRoles()
    {
        fixture.EnsureInitialized();

        // Same shape from the other direction: an account whose token carries no role claims resolves
        // to a real, restricted identity rather than to an error or to the system context. The
        // "repair" this pins against is falling back to System when the roles come back empty — which
        // would turn the most restricted configuration into the least restricted one.
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity("octo-pipeline-sa-roleless", [], DateTime.UtcNow.AddMinutes(5))));

        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        var globalConfiguration = GivenServiceAccountConfiguration("octo-pipeline-sa-roleless");
        var resolver = new PipelineIdentityResolver(tenantRepository.TenantId, null, globalConfiguration,
            tokenService, NullLogger.Instance);
        var context = CreateContext(tenantRepository, globalConfiguration, identityResolver: resolver);

        using var session = await context.GetScopedSessionAsync();

        var securityContext = session.GetSecurityContext();
        securityContext.IsSystem.Should().BeFalse();
        securityContext.SubjectId.Should().Be("octo-pipeline-sa-roleless");
        securityContext.Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task WithBothACallerAndAServiceAccount_TheSessionActsAsTheCaller()
    {
        fixture.EnsureInitialized();

        // The precedence, read off the session the engine produced. It matters here rather than only
        // in the resolver unit test because the SUBJECT is what owner-scoped filtering keys on
        // (RtCreatedBy / ownerAttributePath, AB#4978): if the account's subject reached the session,
        // every owner-scoped read would silently be about the service instead of about the user.
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(
                new ServiceAccountIdentity("octo-pipeline-sa-integration", ["CommunicationManagement"],
                    DateTime.UtcNow.AddMinutes(5))));

        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        var globalConfiguration = GivenServiceAccountConfiguration("octo-pipeline-sa-integration");
        var principal = new VerifiedPrincipal(SubjectId, "system", "u@example.com", "U", Roles);
        var resolver = new PipelineIdentityResolver(tenantRepository.TenantId, principal, globalConfiguration,
            tokenService, NullLogger.Instance);
        var context = CreateContext(tenantRepository, globalConfiguration, principal, resolver);

        using var session = await context.GetScopedSessionAsync();

        var securityContext = session.GetSecurityContext();
        securityContext.SubjectId.Should().Be(SubjectId);
        securityContext.Roles.Should().BeEquivalentTo(Roles);
    }

    [Fact]
    public async Task AConfiguredServiceAccountWhoseTokenIsUnavailableOpensNoSessionAtAll()
    {
        fixture.EnsureInitialized();

        // 🔴 Fail-closed, proven where it counts: no session is handed out, so no read is widened and
        // no write goes in unstamped. A fallback to the system context would produce a perfectly
        // usable session here that bypasses data permissions entirely (AB#4969).
        var tokenService = A.Fake<IServiceAccountTokenService>();
        A.CallTo(() => tokenService.AcquireServiceAccountIdentityAsync(
                A<ServiceAccountCredentials>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ServiceAccountIdentity?>(null));

        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        var globalConfiguration = GivenServiceAccountConfiguration("octo-pipeline-sa-unavailable");
        var resolver = new PipelineIdentityResolver(tenantRepository.TenantId, null, globalConfiguration,
            tokenService, NullLogger.Instance);
        var context = CreateContext(tenantRepository, globalConfiguration, identityResolver: resolver);

        var act = async () => await context.GetScopedSessionAsync();

        await act.Should().ThrowAsync<PipelineExecutionException>();
    }

    private static IGlobalConfiguration GivenServiceAccountConfiguration(string clientId)
    {
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfiguration.GetAllRawJsonByCkTypeId(
                PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId))
            .Returns([
                $$"""
                  {
                    "issuerUri": "https://identity.example.com",
                    "clientId": "{{clientId}}",
                    "clientSecret": "s3cr3t",
                    "tenantId": "system"
                  }
                  """
            ]);
        return globalConfiguration;
    }

    private static MeshEtlContext CreateContext(ITenantRepository tenantRepository,
        IGlobalConfiguration? globalConfiguration = null,
        VerifiedPrincipal? verifiedPrincipal = null,
        IPipelineIdentityResolver? identityResolver = null)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfiguration ?? A.Fake<IGlobalConfiguration>(),
            properties: new Dictionary<string, object?>(),
            verifiedPrincipal: verifiedPrincipal,
            callerAccessToken: null,
            identityResolver: identityResolver);
    }
}
