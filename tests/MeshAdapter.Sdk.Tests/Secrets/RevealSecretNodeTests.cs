using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Secrets;

/// <summary>
/// AB#5538: RevealSecret@1 decrypts one Secret attribute in process, counted, never logged, masked in
/// every diagnostic output, and fails without the value.
/// </summary>
public class RevealSecretNodeTests : SessionNodeTestBase
{
    // Obviously fake test plaintexts.
    private const string Plaintext = "fake-smtp-password-123";
    private const string RecordPlaintext = "fake-api-key-456";
    private static readonly OctoObjectId RtId = new("65d5c447b420da3fb12381bc");

    private readonly ICkCacheService _ckCache = SecretTestSupport.CreateCkCache();
    private readonly ISecretAttributeProtector _protector = SecretTestSupport.CreateProtector();

    public RevealSecretNodeTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(SecretTestSupport.TenantId);
    }

    private void StubEntity(RtEntity? entity)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(Session,
                A<RtEntityId>.That.Matches(id => id.RtId == RtId)))
            .Returns(Task.FromResult(entity));
    }

    private RtEntity EntityWithPassword(RtSecretValue? password)
    {
        var entity = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        entity.SetAttributeRawValue("Name", "smtp");
        if (password != null)
        {
            entity.SetAttributeRawValue("Password", password);
        }

        return entity;
    }

    private static (DataContextImpl DataContext, INodeContext NodeContext, DefaultPipelineDebugger Debugger) Context(
        RevealSecretNodeConfiguration config, string json = "{}")
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(json));
        var debugger = new DefaultPipelineDebugger(NullLoggerFactory.Instance);
        debugger.RegisterPipelineRtEntityId(new RtEntityId("Test/Pipeline", OctoObjectId.GenerateNewId()),
            Guid.NewGuid());
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext, debugger);
        return (dataContext, root.RegisterChildNode("RevealSecret@1", 0, config, dataContext), debugger);
    }

    private RevealSecretNode Node(NodeDelegate next, ISecretAttributeProtector? protector = null) =>
        new(next, EtlContext, _ckCache, protector ?? _protector);

    private static async Task<PipelineExecutionException> AssertFails(Func<Task> act, params string[] mustContain)
    {
        var e = await Assert.ThrowsAnyAsync<PipelineExecutionException>(act);
        Assert.Contains("RevealSecret", e.Message);
        foreach (var part in mustContain)
        {
            Assert.Contains(part, e.Message);
        }

        Assert.DoesNotContain(Plaintext, e.ToString());
        return e;
    }

    [Fact]
    public async Task Reveal_WritesThePlaintextToTheTarget_AndMasksItInTheDebugSnapshot()
    {
        StubEntity(EntityWithPassword(_protector.Protect(Plaintext)));
        var (dc, nc, debugger) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password",
            TargetPath = "$.smtp.password"
        });
        var next = A.Fake<NodeDelegate>();

        await Node(next).ProcessObjectAsync(dc, nc);

        Assert.Equal(Plaintext, dc.Get<string>("$.smtp.password"));
        A.CallTo(() => next.Invoke(dc, nc)).MustHaveHappenedOnceExactly();
        AssertScopedSessionOpened();

        // What the Studio debug panel would show after this node.
        nc.Unregister(dc);
        var output = debugger.GetDebugInformation().DebugPoints.Single(p => p.Output != null).Output!;
        Assert.DoesNotContain(Plaintext, output);
        Assert.Equal("***", JsonNode.Parse(output)!["smtp"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reveal_IsCountedAsADecryptWithTenantTypeAndAttribute()
    {
        StubEntity(EntityWithPassword(_protector.Protect(Plaintext)));
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "password", TargetPath = "$.p"
        });

        var measurements = new List<IReadOnlyDictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Meshmakers.Octo.Secrets" && instrument.Name == "octo.secrets.decrypt")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (measurements)
            {
                measurements.Add(copy);
            }
        });
        listener.Start();

        await Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc);

        lock (measurements)
        {
            Assert.Contains(measurements, m =>
                Equals(m.GetValueOrDefault("tenant"), SecretTestSupport.TenantId) &&
                Equals(m.GetValueOrDefault("ckType"), SecretTestSupport.CkTypeId.ToString()) &&
                Equals(m.GetValueOrDefault("attribute"), "Password"));
        }
    }

    [Fact]
    public async Task Reveal_NotSet_WritesNull()
    {
        StubEntity(EntityWithPassword(null));
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p"
        }, """{"p":"old"}""");

        await Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc);

        Assert.Equal(DataKind.Null, dc.GetKind("$.p"));
    }

    [Fact]
    public async Task Reveal_LegacyPlaintextSlot_IsReturned()
    {
        // A string stored before the attribute became Secret: readable during the transition.
        var entity = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        entity.SetAttributeRawValue("Password", Plaintext);
        StubEntity(entity);
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p"
        });

        await Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc);

        Assert.Equal(Plaintext, dc.Get<string>("$.p"));
    }

    [Fact]
    public async Task Reveal_FromRtIdAndCkTypePaths_InsideARecord()
    {
        var entity = EntityWithPassword(null);
        var settings = new RtRecord { CkRecordId = SecretTestSupport.SettingsRecordId };
        settings.SetAttributeRawValue("Url", "https://example.invalid");
        settings.SetAttributeRawValue("ApiKey", _protector.Protect(RecordPlaintext));
        entity.SetAttributeRawValue("Settings", settings);
        StubEntity(entity);
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeIdPath = "$.ck", RtIdPath = "$.id", AttributeName = "settings.apiKey", TargetPath = "$.key"
        }, $$"""{"ck":"{{SecretTestSupport.CkTypeId}}","id":"{{RtId}}"}""");

        await Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc);

        Assert.Equal(RecordPlaintext, dc.Get<string>("$.key"));
    }

    [Fact]
    public async Task Reveal_NonSecretAttribute_FailsWithoutDecrypting()
    {
        StubEntity(EntityWithPassword(_protector.Protect(Plaintext)));
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Name", TargetPath = "$.p"
        });

        await AssertFails(() => Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc), "not Secret");
        Assert.False(dc.Exists("$.p"));
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(A<IOctoSession>._, A<RtEntityId>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Reveal_RecordArrayPath_IsRefused()
    {
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Endpoints.ApiKey",
            TargetPath = "$.p"
        });

        await AssertFails(() => Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc), "record arrays");
    }

    [Fact]
    public async Task Reveal_UnknownKeyId_FailsNamingTheKeyIdButNotTheValue()
    {
        var foreign = SecretTestSupport.CreateProtector("k9");
        StubEntity(EntityWithPassword(foreign.Protect(Plaintext)));
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p"
        });

        await AssertFails(() => Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc), "'k9'");
        Assert.False(dc.Exists("$.p"));
    }

    [Fact]
    public async Task Reveal_WithoutKeyRing_Fails()
    {
        StubEntity(EntityWithPassword(_protector.Protect(Plaintext)));
        var unconfigured = SecretTestSupport.CreateProtector(keys: null);
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p"
        });

        Assert.False(unconfigured.IsConfigured);
        await AssertFails(() => Node(A.Fake<NodeDelegate>(), unconfigured).ProcessObjectAsync(dc, nc),
            "no secret key ring");
    }

    [Fact]
    public async Task Reveal_EntityNotFound_Fails()
    {
        StubEntity(null);
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p"
        });

        await AssertFails(() => Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc), "not found");
    }

    [Fact]
    public async Task Reveal_WithoutRtId_Fails()
    {
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, AttributeName = "Password", TargetPath = "$.p"
        });

        await AssertFails(() => Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc), "rtId");
    }

    [Fact]
    public async Task Reveal_SystemIdentity_OpensASystemSession()
    {
        GivenSystemSessionIsExpected();
        StubEntity(EntityWithPassword(_protector.Protect(Plaintext)));
        var (dc, nc, _) = Context(new RevealSecretNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtId = RtId, AttributeName = "Password", TargetPath = "$.p",
            Identity = NodeExecutionIdentity.System
        });

        await Node(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, nc);

        Assert.Equal(Plaintext, dc.Get<string>("$.p"));
    }

    [Fact]
    public async Task Configuration_DeserializesFromYaml()
    {
        // The exact shape documented for pipeline authors and the Studio handover.
        const string yaml = """
                            transformations:
                              - type: RevealSecret@1
                                ckTypeId: System.Communication/EMailSenderConfiguration
                                rtIdPath: $.config.rtId
                                attributeName: Password
                                targetPath: $.smtp.password
                                identity: ServiceAccount
                            """;
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().RegisterNode(typeof(RevealSecretNode));
        var serializer = services.BuildServiceProvider().GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync(yaml);

        var config = Assert.IsType<RevealSecretNodeConfiguration>(Assert.Single(root.Transformations!));
        Assert.Equal("System.Communication/EMailSenderConfiguration", config.CkTypeId!.ToString());
        Assert.Equal("$.config.rtId", config.RtIdPath);
        Assert.Equal("Password", config.AttributeName);
        Assert.Equal("$.smtp.password", config.TargetPath);
        Assert.Equal(NodeExecutionIdentity.ServiceAccount, config.Identity);
    }
}
