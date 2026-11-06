using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public abstract class VerifyCaptchaTestBase : SessionNodeTestBase
{
    protected const string DefaultKey = "ec-captcha-secret";
    protected const string TenantWellKnownName = "RegistrationConfiguration";
    protected static readonly OctoObjectId TenantRtId = new("65d5c447b420da3fb12381c1");
    protected static readonly OctoObjectId DefaultRtId = new("65d5c447b420da3fb12381c2");

    protected VerifyCaptchaTestBase()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(SecretTestSupport.TenantId);
        SystemSession = A.Fake<IOctoSession>();
        GivenSystemSessionIsExpected(SystemSession);
        Handler = new FakeTurnstileHandler();
        StubTenantEntity(null);
        StubTenantEntitiesByWellKnownName();
        StubDefaults();
    }

    protected IOctoSession SystemSession { get; }

    protected ICkCacheService CkCacheService { get; set; } = SecretTestSupport.CreateCkCache();

    protected ISecretAttributeProtector Protector { get; } = SecretTestSupport.CreateProtector();

    protected FakeTurnstileHandler Handler { get; set; }

    protected TimeProvider? Clock { get; set; }

    protected static VerifyCaptchaTenantSecretConfiguration TenantSecretByRtId() => new()
    {
        CkTypeId = SecretTestSupport.CkTypeId, RtId = TenantRtId, AttributeName = "Password"
    };

    protected static VerifyCaptchaNodeConfiguration Config(VerifyCaptchaTenantSecretConfiguration? tenantSecret = null,
        NodeExecutionIdentity identity = NodeExecutionIdentity.Caller) => new()
    {
        Provider = CaptchaProvider.Turnstile,
        TokenPath = "$.body.captchaToken",
        RemoteIpPath = "$.body.remoteIp",
        TenantSecret = tenantSecret,
        DefaultKey = DefaultKey,
        TargetPath = "$.captcha",
        Identity = identity
    };

    protected static string Input(string? token = FakeTurnstileHandler.DummyToken) =>
        token == null
            ? """{"body":{}}"""
            : $$$"""{"body":{"captchaToken":"{{{token}}}","remoteIp":"203.0.113.7"}}""";

    protected RtEntity TenantEntity(string? secret)
    {
        var entity = new RtEntity(SecretTestSupport.CkTypeId, TenantRtId) { RtWellKnownName = TenantWellKnownName };
        if (secret != null)
        {
            entity.SetAttributeRawValue("Password", Protector.Protect(secret));
        }

        return entity;
    }

    protected RtEntity DefaultEntity(string? secret)
    {
        var entity = new RtEntity(PlatformOnlySecrets.SecretDefaultCkTypeId, DefaultRtId) { RtWellKnownName = DefaultKey };
        if (secret != null)
        {
            entity.SetAttributeRawValue("Value", Protector.Protect(secret));
        }

        return entity;
    }

    protected void StubTenantEntity(RtEntity? entity)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(Session,
                A<RtEntityId>.That.Matches(id => id.RtId == TenantRtId)))
            .Returns(Task.FromResult(entity));
    }

    protected void StubTenantEntitiesByWellKnownName(params RtEntity[] entities)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(Session, SecretTestSupport.CkTypeId,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(ResultSet(entities));
    }

    protected void StubDefaults(params RtEntity[] entities)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(SystemSession, PlatformOnlySecrets.SecretDefaultCkTypeId,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(ResultSet(entities));
    }

    protected static IResultSet<RtEntity> ResultSet(params RtEntity[] entities)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Length);
        return resultSet;
    }

    protected static (DataContextImpl DataContext, INodeContext NodeContext, DefaultPipelineDebugger Debugger) Context(
        NodeConfiguration config, string json, IPipelineLogger? logger = null, string nodeName = "VerifyCaptcha@1")
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(json));
        var debugger = new DefaultPipelineDebugger(NullLoggerFactory.Instance);
        debugger.RegisterPipelineRtEntityId(new RtEntityId("Test/Pipeline", OctoObjectId.GenerateNewId()),
            Guid.NewGuid());
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            logger ?? A.Fake<IPipelineLogger>(), dataContext, debugger);
        return (dataContext, root.RegisterChildNode(nodeName, 0, config, dataContext), debugger);
    }

    protected IHttpClientFactory HttpClientFactory()
    {
        var factory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => factory.CreateClient(A<string>._)).ReturnsLazily(() => new HttpClient(Handler, false));
        return factory;
    }

    protected VerifyCaptchaNode Node(NodeDelegate? next = null, ISecretAttributeProtector? protector = null) =>
        new(next ?? A.Fake<NodeDelegate>(), EtlContext, CkCacheService, protector ?? Protector, HttpClientFactory(),
            Clock);

    protected async Task<JsonObject> Run(VerifyCaptchaNodeConfiguration config, string? json = null,
        IPipelineLogger? logger = null)
    {
        var (dc, nc, _) = Context(config, json ?? Input(), logger);
        await Node().ProcessObjectAsync(dc, nc);
        return dc.Get<JsonNode>("$.captcha")!.AsObject();
    }

    protected static void AssertResult(JsonObject result, bool valid, string source, string? reason)
    {
        Assert.Equal(valid, result["valid"]!.GetValue<bool>());
        Assert.Equal(source, result["source"]!.GetValue<string>());
        if (reason == null)
        {
            Assert.False(result.ContainsKey("reason"));
            Assert.Equal(["source", "valid"], result.Select(p => p.Key).Order());
        }
        else
        {
            Assert.Equal(reason, result["reason"]!.GetValue<string>());
            Assert.Equal(["reason", "source", "valid"], result.Select(p => p.Key).Order());
        }
    }

    protected void AssertDefaultNotRead()
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(SystemSession, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }
}
