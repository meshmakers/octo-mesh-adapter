using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public class VerifyCaptchaSecretResolutionTests : VerifyCaptchaTestBase
{
    [Fact]
    public async Task TenantSecretSet_VerifiesWithIt_AndNeverReadsTheDefault()
    {
        StubTenantEntity(TenantEntity(FakeTurnstileHandler.AlwaysPassSecret));
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysFailSecret));

        var result = await Run(Config(TenantSecretByRtId()));

        AssertResult(result, true, "tenant", null);
        Assert.Equal(FakeTurnstileHandler.AlwaysPassSecret, Assert.Single(Handler.Requests).Form["secret"]);
        AssertScopedSessionOpened();
        AssertDefaultNotRead();
    }

    [Fact]
    public async Task TenantSecretNotSet_UsesTheSecretDefault()
    {
        StubTenantEntity(TenantEntity(null));
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config(TenantSecretByRtId()));

        AssertResult(result, true, "default", null);
        Assert.Equal(FakeTurnstileHandler.AlwaysPassSecret, Assert.Single(Handler.Requests).Form["secret"]);
        AssertSystemSessionOpened();
    }

    [Fact]
    public async Task TenantEntityMissing_UsesTheSecretDefault()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config(TenantSecretByRtId()));

        AssertResult(result, true, "default", null);
    }

    [Fact]
    public async Task NoTenantSecretConfigured_UsesTheSecretDefault()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config());

        AssertResult(result, true, "default", null);
        AssertNoScopedRead();
    }

    [Fact]
    public async Task TenantSecretByWellKnownName_IsFoundThroughTheExecutionIdentity()
    {
        StubTenantEntitiesByWellKnownName(TenantEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var tenantSecret = new VerifyCaptchaTenantSecretConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, WellKnownName = TenantWellKnownName, AttributeName = "password"
        };

        var result = await Run(Config(tenantSecret, NodeExecutionIdentity.ServiceAccount));

        AssertResult(result, true, "tenant", null);
        AssertServiceAccountSessionOpened();
    }

    [Fact]
    public async Task TenantSecretByRtIdPath_IsRead()
    {
        StubTenantEntity(TenantEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var tenantSecret = new VerifyCaptchaTenantSecretConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtIdPath = "$.config.rtId", AttributeName = "Password"
        };
        var json = $$$"""{"config":{"rtId":"{{{TenantRtId}}}"},"body":{"captchaToken":"t"}}""";

        var result = await Run(Config(tenantSecret), json);

        AssertResult(result, true, "tenant", null);
    }

    [Fact]
    public async Task NeitherSecret_IsNotConfigured_WithoutAnHttpCall()
    {
        StubTenantEntity(TenantEntity(null));

        var result = await Run(Config(TenantSecretByRtId()));

        AssertResult(result, false, "none", "not-configured");
        Assert.Empty(Handler.Requests);
    }

    [Fact]
    public async Task DefaultEntityWithoutValue_IsNotConfigured()
    {
        StubDefaults(DefaultEntity(null));

        var result = await Run(Config());

        AssertResult(result, false, "none", "not-configured");
        Assert.Empty(Handler.Requests);
    }

    [Fact]
    public async Task SecretDefaultTypeUnknownInTheTenant_IsNotConfigured_WithoutAQuery()
    {
        var inner = SecretTestSupport.CreateCkCache();
        var cache = A.Fake<ICkCacheService>(options => options.Wrapping(inner));
        CkTypeGraph? none = null;
        A.CallTo(() => cache.TryGetRtCkType(SecretTestSupport.TenantId, PlatformOnlySecrets.SecretDefaultCkTypeId,
            out none!)).Returns(false);
        CkCacheService = cache;

        var result = await Run(Config());

        AssertResult(result, false, "none", "not-configured");
        AssertDefaultNotRead();
    }

    [Fact]
    public async Task DefaultIsLookedUpByItsWellKnownNameOnly()
    {
        RtEntityQueryOptions? captured = null;
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(SystemSession, PlatformOnlySecrets.SecretDefaultCkTypeId,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Invokes((IOctoSession _, RtCkId<CkTypeId> _, RtEntityQueryOptions queryOptions, int? _, int? _) => captured = queryOptions)
            .Returns(ResultSet(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret)));

        await Run(Config());

        Assert.NotNull(captured);
        var filter = Assert.Single(captured!.FieldFilters!);
        Assert.Equal(nameof(RtEntity.RtWellKnownName), filter.AttributePath);
        Assert.Equal(DefaultKey, filter.ComparisonValue?.ToString());
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller)]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    public async Task TenantIdentity_StillGetsTheDefault_WhileRevealSecretOnItStaysRefused(NodeExecutionIdentity identity)
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config(identity: identity));

        AssertResult(result, true, "default", null);
        AssertSystemSessionOpened();

        var (dataContext, revealContext, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = PlatformOnlySecrets.SecretDefaultCkTypeId, RtId = DefaultRtId, AttributeName = "Value",
            TargetPath = "$.p", Identity = identity
        }, "{}", nodeName: "RevealSecret@1");
        var reveal = new RevealSecretNode(A.Fake<NodeDelegate>(), EtlContext, CkCacheService, Protector);

        var pipelineExecutionException = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => reveal.ProcessObjectAsync(dataContext, revealContext));

        Assert.Contains("platform-only", pipelineExecutionException.Message);
        Assert.False(dataContext.Exists("$.p"));
    }

    private void AssertNoScopedRead()
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(Session, A<RtEntityId>._)).MustNotHaveHappened();
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(Session, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }
}
