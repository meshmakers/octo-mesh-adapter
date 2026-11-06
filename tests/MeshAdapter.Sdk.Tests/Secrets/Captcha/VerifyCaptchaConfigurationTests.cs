using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public class VerifyCaptchaConfigurationTests : VerifyCaptchaTestBase
{
    private const string Yaml = """
                                transformations:
                                  - type: VerifyCaptcha@1
                                    provider: Turnstile
                                    tokenPath: $.body.captchaToken
                                    remoteIpPath: $.request.remoteIp
                                    tenantSecret:
                                      ckTypeId: EnergyCommunity.Registration/RegistrationConfiguration
                                      wellKnownName: RegistrationConfiguration
                                      attributeName: CaptchaSecret
                                    defaultKey: ec-captcha-secret
                                    targetPath: $.captcha
                                    identity: ServiceAccount
                                """;

    private static async Task<NodeDefinitionRoot> Deserialize(string yaml)
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();
        return await serializer.DeserializeAsync(yaml);
    }

    private static string AllMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    [Fact]
    public async Task TheNodeIsInThePipelineSchema_AndDeserializesFromYaml()
    {
        var root = await Deserialize(Yaml);

        var config = Assert.IsType<VerifyCaptchaNodeConfiguration>(Assert.Single(root.Transformations!));
        Assert.Equal(CaptchaProvider.Turnstile, config.Provider);
        Assert.Equal("$.body.captchaToken", config.TokenPath);
        Assert.Equal("$.request.remoteIp", config.RemoteIpPath);
        Assert.Equal("EnergyCommunity.Registration/RegistrationConfiguration", config.TenantSecret!.CkTypeId.ToString());
        Assert.Equal("RegistrationConfiguration", config.TenantSecret.WellKnownName);
        Assert.Equal("CaptchaSecret", config.TenantSecret.AttributeName);
        Assert.Equal("ec-captcha-secret", config.DefaultKey);
        Assert.Equal("$.captcha", config.TargetPath);
        Assert.Equal(NodeExecutionIdentity.ServiceAccount, config.Identity);
        Assert.Equal(10, config.TimeoutSeconds);
    }

    [Fact]
    public async Task AnUnknownProvider_IsRefusedByTheSerializer()
    {
        var e = await Assert.ThrowsAsync<PipelineSerializationException>(() =>
            Deserialize(Yaml.Replace("provider: Turnstile", "provider: Recaptcha")));

        Assert.Contains("Recaptcha", AllMessages(e));
    }

    [Fact]
    public async Task AnUndefinedProviderValue_FailsTheNode()
    {
        var (dc, nc, _) = Context(Config() with { Provider = (CaptchaProvider)7 }, Input());

        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));

        Assert.Contains("provider", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Handler.Requests);
    }

    [Fact]
    public void TheConfiguration_HasNoPlaceForAnEndpoint()
    {
        var forbidden = new[] { "url", "uri", "endpoint", "host", "server", "address", "baseurl" };
        var properties = typeof(VerifyCaptchaNodeConfiguration).GetProperties()
            .Concat(typeof(VerifyCaptchaTenantSecretConfiguration).GetProperties())
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.DoesNotContain(properties, name => forbidden.Any(name.Contains));
    }

    [Fact]
    public async Task AnEndpointInTheYaml_IsRefusedByTheSerializer()
    {
        var yaml = Yaml + "\n    url: https://attacker.example/collect\n";

        var e = await Assert.ThrowsAsync<PipelineSerializationException>(() => Deserialize(yaml));

        Assert.Contains("url", AllMessages(e));
    }

    [Fact]
    public async Task SystemIdentity_IsRefusedBeforeAnythingIsRead()
    {
        var (dc, nc, _) = Context(Config(TenantSecretByRtId(), NodeExecutionIdentity.System), Input());

        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));

        Assert.Contains("'System' is not allowed", e.Message);
        AssertNoSessionOpened();
    }

    [Fact]
    public async Task ASecretDefaultAsTenantSecret_IsRefusedAsPlatformOnly()
    {
        var protector = A.Fake<ISecretAttributeProtector>();
        var tenantSecret = new VerifyCaptchaTenantSecretConfiguration
        {
            CkTypeId = PlatformOnlySecrets.SecretDefaultCkTypeId, WellKnownName = DefaultKey, AttributeName = "Value"
        };
        var (dc, nc, _) = Context(Config(tenantSecret), Input());

        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() =>
            Node(protector: protector).ProcessObjectAsync(dc, nc));

        Assert.Contains("platform-only", e.Message);
        AssertNoSessionOpened();
        A.CallTo(protector).MustNotHaveHappened();
    }

    public static TheoryData<string, string, string> SecretDefaultVariants => new()
    {
        { "system.communication/secretdefault", "Value", "is unknown in tenant" },
        { "System.Communication-2.0/SecretDefault", "Value", "is unknown in tenant" },
        { "System.Communication/SecretDefault-1", "Value", "platform-only" },
        { "System.Communication/Application", "SecretDefaults[0].Value", "platform-only" }
    };

    [Theory]
    [MemberData(nameof(SecretDefaultVariants))]
    public async Task ASecretDefaultVariantUnknownToTheTenant_IsRefusedBeforeAnythingIsRead(string ckTypeId,
        string attributeName, string expected)
    {
        CkCacheService = A.Fake<ICkCacheService>();

        await AssertRefusedBeforeAnythingIsRead(ckTypeId, attributeName, expected);
    }

    [Theory]
    [MemberData(nameof(SecretDefaultVariants))]
    public async Task ASecretDefaultVariantTheTenantResolves_IsRefusedAsPlatformOnly(string ckTypeId,
        string attributeName, string _)
    {
        CkCacheService = CacheResolvingEveryTypeToSecretDefault();

        await AssertRefusedBeforeAnythingIsRead(ckTypeId, attributeName, "platform-only");
    }

    private async Task AssertRefusedBeforeAnythingIsRead(string ckTypeId, string attributeName, string expected)
    {
        var protector = A.Fake<ISecretAttributeProtector>();
        var tenantSecret = new VerifyCaptchaTenantSecretConfiguration
        {
            CkTypeId = new RtCkId<CkTypeId>(ckTypeId), WellKnownName = DefaultKey, AttributeName = attributeName
        };
        var (dc, nc, _) = Context(Config(tenantSecret), Input());

        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() =>
            Node(protector: protector).ProcessObjectAsync(dc, nc));

        Assert.True(e.Message.Contains(expected), e.Message);
        AssertNoSessionOpened();
        A.CallTo(protector).MustNotHaveHappened();
        Assert.Empty(Handler.Requests);
    }

    private static ICkCacheService CacheResolvingEveryTypeToSecretDefault()
    {
        var self = new CkId<CkTypeId>("System.Communication-2.0.0/SecretDefault");
        var value = new CkTypeAttributeGraph(ckAttributeId: new CkId<CkAttributeId>("System.Communication", new CkAttributeId("Value")),
            attributeName: "Value", autoCompleteValues: null, valueType: AttributeValueTypesDto.Secret,
            valueCkRecordId: null, valueCkEnumId: null, autoIncrementReference: null, metaData: null,
            defaultValues: null, isOptional: false, description: null);
        var graph = new CkTypeGraph(self, isAbstract: false, isFinal: false, isCollectionRoot: false, baseTypes: [],
            derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [], definedAttributes: [],
            allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> { [value.CkAttributeId] = value },
            indexes: [], associations: new CkGraphDirectedAssociations([]), description: string.Empty,
            enableChangeStreamPreAndPostImages: false);
        var graphOut = (CkTypeGraph?)graph;
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._, out graphOut)).Returns(true)
            .AssignsOutAndRefParameters(graph);
        return cache;
    }

    [Fact]
    public async Task ATenantSecretAttributeThatIsNoSecret_FailsTheNode()
    {
        var tenantSecret = TenantSecretByRtId() with { AttributeName = "Name" };
        var (dc, nc, _) = Context(Config(tenantSecret), Input());

        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));

        Assert.Contains("not Secret", e.Message);
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._, A<RtEntityId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ATenantSecretWithoutAnEntityReference_FailsTheNode()
    {
        var tenantSecret = TenantSecretByRtId() with { RtId = null };
        var (dc, nc, _) = Context(Config(tenantSecret), Input());

        await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task ATimeoutOutsideTheRange_FailsTheNode(int seconds)
    {
        var (dc, nc, _) = Context(Config() with { TimeoutSeconds = seconds }, Input());

        await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));
    }

    [Theory]
    [InlineData("tokenPath")]
    [InlineData("defaultKey")]
    public async Task ARequiredFieldLeftBlank_FailsTheNode(string field)
    {
        var config = field == "tokenPath" ? Config() with { TokenPath = " " } : Config() with { DefaultKey = "" };
        var (dc, nc, _) = Context(config, Input());

        await Assert.ThrowsAnyAsync<PipelineExecutionException>(() => Node().ProcessObjectAsync(dc, nc));
    }

    [Fact]
    public async Task TheNextNodeRuns_AfterTheVerdictIsWritten()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var next = A.Fake<NodeDelegate>();
        var (dc, nc, _) = Context(Config(), Input());

        await Node(next).ProcessObjectAsync(dc, nc);

        A.CallTo(() => next.Invoke(dc, nc)).MustHaveHappenedOnceExactly();
    }
}
