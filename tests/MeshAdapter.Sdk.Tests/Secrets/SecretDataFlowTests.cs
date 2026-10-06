using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Secrets;

/// <summary>
/// AB#5538: how Secret attributes travel through the data context - read as marker only, legacy strings
/// masked by CK type, plaintext write-back survives the CreateUpdateInfo → ApplyChanges hop.
/// </summary>
public class SecretDataFlowTests : SessionNodeTestBase
{
    private const string Plaintext = "fake-refresh-token-789";
    private static readonly OctoObjectId RtId = new("65d5c447b420da3fb12381bd");

    private readonly ISecretAttributeProtector _protector = SecretTestSupport.CreateProtector();

    public SecretDataFlowTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(SecretTestSupport.TenantId);
        A.CallTo(() => EtlContext.Properties).Returns(new Dictionary<string, object?>());
    }

    private static (DataContextImpl DataContext, INodeContext Root, DefaultPipelineDebugger Debugger) Root(
        string json = "{}")
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(json));
        var debugger = new DefaultPipelineDebugger(NullLoggerFactory.Instance);
        debugger.RegisterPipelineRtEntityId(new RtEntityId("Test/Pipeline", OctoObjectId.GenerateNewId()),
            Guid.NewGuid());
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext, debugger);
        return (dataContext, root, debugger);
    }

    [Fact]
    public void EntityWrittenToTheDataContext_CarriesOnlyTheMarker()
    {
        // The path every read node uses (GetRtEntitiesById/ByType, GetOrCreate, queries): dataContext.Set
        // with the repository's entities.
        var entity = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        entity.SetAttributeRawValue("Name", "svc");
        entity.SetAttributeRawValue("Password", _protector.Protect(Plaintext));
        var settings = new RtRecord { CkRecordId = SecretTestSupport.SettingsRecordId };
        settings.SetAttributeRawValue("ApiKey", RtSecretValue.LegacyPlaintext(Plaintext));
        entity.SetAttributeRawValue("Settings", settings);
        var (dc, _, _) = Root();

        dc.Set("$.items", new List<RtEntity> { entity });

        var json = dc.Get<JsonNode>("$")!.ToJsonString();
        Assert.DoesNotContain(Plaintext, json);
        Assert.DoesNotContain("enc:v", json);
        var attributes = dc.Get<JsonNode>("$.items[0].attributes") ?? dc.Get<JsonNode>("$.items[0].Attributes");
        Assert.NotNull(attributes);
        Assert.True(PipelineSecretValues.IsSecretMarker(attributes!["Password"]));
        Assert.True(attributes["Password"]!["isSet"]!.GetValue<bool>());
        Assert.Contains("\"isSet\":true", attributes["Settings"]!.ToJsonString());
    }

    [Fact]
    public void ChangeStreamDocument_LegacyStringsInSecretSlots_AreMaskedByCkType()
    {
        var document = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        document.SetAttributeRawValue("Name", "svc");
        document.SetAttributeRawValue("Password", Plaintext);
        var endpoint = new RtRecord { CkRecordId = SecretTestSupport.SettingsRecordId };
        endpoint.SetAttributeRawValue("Url", "https://example.invalid");
        endpoint.SetAttributeRawValue("ApiKey", "enc:v1:fake-legacy-envelope");
        document.SetAttributeRawValue("Endpoints", new List<object?> { endpoint });
        var before = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        before.SetAttributeRawValue("Password", "fake-old-password");
        var update = A.Fake<IUpdateInfo<RtEntity>>();
        A.CallTo(() => update.Document).Returns(document);
        A.CallTo(() => update.DocumentBeforeChange).Returns(before);

        var node = new FromWatchRtEntityNode(A.Fake<ISystemContext>(), SecretTestSupport.CreateCkCache());
        node.MaskSecrets(SecretTestSupport.TenantId, update);

        // The trigger serialises the update into the data context of the execution.
        var json = JsonSerializer.Serialize(new { document, before }, SystemTextJsonOptions.Default);
        Assert.DoesNotContain(Plaintext, json);
        Assert.DoesNotContain("enc:v1", json);
        Assert.DoesNotContain("fake-old-password", json);
        Assert.Contains("svc", json);
        Assert.Contains("https://example.invalid", json);
        Assert.IsType<RtSecretValue>(document.Attributes["Password"]);
    }

    [Fact]
    public void MaskLegacyValues_UnknownCkType_LeavesTheEntityAlone()
    {
        var entity = new RtEntity(new RtCkId<CkTypeId>("Other/Type"), RtId);
        entity.SetAttributeRawValue("Password", "plain");

        Assert.Equal(0, SecretAttributes.MaskLegacyValues(SecretTestSupport.CreateCkCache(), "other-tenant", entity));
        Assert.Equal("plain", entity.Attributes["Password"]);
    }

    private async Task<IReadOnlyList<IEntityUpdateInfo<RtEntity>>> RunCreateUpdateThenApply(string json,
        string valuePath, DefaultPipelineDebugger? debuggerCheck = null)
    {
        var (dc, root, debugger) = Root(json);
        var createConfig = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = RtId,
            CkTypeId = SecretTestSupport.CkTypeId,
            TargetPath = "$.updates",
            TargetValueKind = ValueKinds.Array,
            TargetValueWriteMode = TargetValueWriteModes.Append,
            AttributeUpdates =
            [
                new AttributeUpdateConfiguration
                {
                    AttributeName = "Password", AttributeValueType = AttributeValueTypesDto.Secret,
                    ValuePath = valuePath
                }
            ]
        };
        var createContext = root.RegisterChildNode("CreateUpdateInfo@1", 0, createConfig, dc);
        await new CreateUpdateInfoNode(A.Fake<NodeDelegate>(), EtlContext, SecretTestSupport.CreateCkCache())
            .ProcessObjectAsync(dc, createContext);

        createContext.Unregister(dc);
        var snapshot = debugger.GetDebugInformation().DebugPoints.Single(p => p.Output != null).Output!;
        Assert.DoesNotContain(Plaintext, snapshot);

        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => captured = e);
        var applyContext = root.RegisterChildNode("ApplyChanges@2", 1,
            new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = "$.updates" }, dc);
        await new ApplyChangesNode2(A.Fake<NodeDelegate>(), EtlContext).ProcessObjectAsync(dc, applyContext);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task WriteBack_PlaintextIntoASecretAttribute_ReachesTheRepositoryAsAString()
    {
        // The Tesla refresh-token pattern: a new token from an HTTP response is written back.
        var updates = await RunCreateUpdateThenApply($$"""{"token":"{{Plaintext}}"}""", "$.token");

        var entity = Assert.Single(updates).RtEntity!;
        // A plain string in a Secret slot is new input for the engine's write step, which encrypts it.
        Assert.Equal(Plaintext, entity.Attributes["Password"]);
    }

    [Fact]
    public async Task WriteBack_AMarkerFromARead_MeansUnchanged()
    {
        // Copying an entity read earlier: the marker must never become a value.
        var updates = await RunCreateUpdateThenApply("""{"read":{"isSet":true}}""", "$.read");

        var value = Assert.Single(updates).RtEntity!.Attributes["Password"];
        Assert.IsNotType<string>(value);
        // The engine's AttributeValueConverter reads an object as Pending("") = unchanged.
        Assert.True(value is IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or RtSecretValue);
    }

    [Fact]
    public async Task ApplyChanges_MergingUpdates_KeepsAndReconcilesClearSecretAttributes()
    {
        var id = new RtEntityId(SecretTestSupport.CkTypeId, RtId);
        var first = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        first.SetAttributeRawValue("Name", "a");
        var second = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        second.SetAttributeRawValue("Password", new Dictionary<string, object?> { ["isSet"] = true });
        var third = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        third.SetAttributeRawValue("Name", "b");
        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, first, ["Password", "Token"]),
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, second),
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, third, null)
        };
        var (dc, root, _) = Root();
        dc.Set("$.updates", data);
        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => captured = e);
        var applyContext = root.RegisterChildNode("ApplyChanges@2", 0,
            new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = "$.updates" }, dc);

        await new ApplyChangesNode2(A.Fake<NodeDelegate>(), EtlContext).ProcessObjectAsync(dc, applyContext);

        var merged = Assert.Single(captured!);
        Assert.Equal(["Password", "Token"], merged.ClearSecretAttributes!.OrderBy(x => x));
        Assert.False(merged.RtEntity!.Attributes.ContainsKey("Password"));
        Assert.Equal("b", merged.RtEntity.Attributes["Name"]);
    }

    [Fact]
    public async Task ApplyChanges_ALaterPlaintextWithdrawsAnEarlierClear()
    {
        var id = new RtEntityId(SecretTestSupport.CkTypeId, RtId);
        var first = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        var second = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        second.SetAttributeRawValue("Password", Plaintext);
        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, first, ["Password"]),
            EntityUpdateInfo<RtEntity>.CreateUpdate(id, second)
        };
        var (dc, root, _) = Root();
        dc.Set("$.updates", data);
        IReadOnlyList<IEntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => captured = e);
        var applyContext = root.RegisterChildNode("ApplyChanges@2", 0,
            new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = "$.updates" }, dc);

        await new ApplyChangesNode2(A.Fake<NodeDelegate>(), EtlContext).ProcessObjectAsync(dc, applyContext);

        var merged = Assert.Single(captured!);
        Assert.Null(merged.ClearSecretAttributes);
        Assert.Equal(Plaintext, merged.RtEntity!.Attributes["Password"]);
    }

    [Fact]
    public async Task DataMapping_RefusesSecrets()
    {
        var (dc, root, _) = Root("""{"p":{"isSet":true},"v":"x"}""");
        var markerContext = root.RegisterChildNode("DataMapping@1", 0, new DataMappingNodeConfiguration
        {
            Path = "$.p", TargetPath = "$.t", SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.String, Mappings = []
        }, dc);
        var typeContext = root.RegisterChildNode("DataMapping@1", 1, new DataMappingNodeConfiguration
        {
            Path = "$.v", TargetPath = "$.t", SourceValueType = AttributeValueTypesDto.String,
            TargetValueType = AttributeValueTypesDto.Secret, Mappings = []
        }, dc);

        var e1 = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() =>
            new DataMappingNode(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, markerContext));
        var e2 = await Assert.ThrowsAnyAsync<PipelineExecutionException>(() =>
            new DataMappingNode(A.Fake<NodeDelegate>()).ProcessObjectAsync(dc, typeContext));
        Assert.Contains("Secret not supported", e1.Message);
        Assert.Contains("Secret not supported", e2.Message);
    }

    [Fact]
    public void AdapterHost_BindsTheSecretKeyRingFromConfiguration()
    {
        // AB#5536 renders OCTO_SECRETENCRYPTION__* into the pod; the host reads OCTO_-prefixed
        // environment variables, so they arrive as SecretEncryption:* - bound by AddOctoMeshAdapter.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["SecretEncryption:Keys:k1"] = SecretTestSupport.GenerateKey(),
                ["SecretEncryption:ActiveKeyId"] = "k1"
            }).Build());
        services.AddLogging();
        services.AddOctoMeshAdapter();

        var protector = services.BuildServiceProvider().GetRequiredService<ISecretAttributeProtector>();

        Assert.True(protector.IsConfigured);
        Assert.Equal("k1", protector.ActiveKeyId);
        Assert.Equal(Plaintext, protector.Unprotect(protector.Protect(Plaintext)));
    }
}
