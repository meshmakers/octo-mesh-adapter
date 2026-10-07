using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshAdapter.Sdk.Tests.Secrets;

[CollectionDefinition(nameof(PipelineSecretMarkerRegistrationTests), DisableParallelization = true)]
public sealed class PipelineSecretMarkerRegistrationCollection;

/// <summary>
/// AB#5538: entity-reading nodes write Secret markers that reflect the adapter's key ring - a stored
/// value with an unknown key id reads as <c>{"isSet":false,"keyMissing":true}</c>, as RevealSecret@1 sees it.
/// </summary>
/// <remarks>
/// The classifier registration is process-wide. The ring here knows <c>k1</c> (the key id every other
/// test protects with, so their markers stay <c>isSet: true</c>) and the values with the unknown key id
/// <see cref="GoneKeyId" /> exist only in this class.
/// </remarks>
[Collection(nameof(PipelineSecretMarkerRegistrationTests))]
public class PipelineSecretMarkerRegistrationTests : SessionNodeTestBase
{
    private const string Plaintext = "fake-marker-password-5538";
    private const string GoneKeyId = "kgone5538";
    private static readonly OctoObjectId RtId = new("65d5c447b420da3fb12381be");

    private readonly ISecretAttributeProtector _adapterProtector = SecretTestSupport.CreateProtector();

    // A value protected with a key the adapter's ring does not hold (e.g. restored from another environment).
    private readonly RtSecretValue _unknownKidValue =
        SecretTestSupport.CreateProtector(GoneKeyId).Protect(Plaintext);

    private RtEntity Entity()
    {
        var entity = new RtEntity(SecretTestSupport.CkTypeId, RtId);
        entity.SetAttributeRawValue("Name", "svc");
        entity.SetAttributeRawValue("Password", _unknownKidValue);
        var settings = new RtRecord { CkRecordId = SecretTestSupport.SettingsRecordId };
        settings.SetAttributeRawValue("ApiKey", _adapterProtector.Protect(Plaintext));
        entity.SetAttributeRawValue("Settings", settings);
        var endpoint = new RtRecord { CkRecordId = SecretTestSupport.SettingsRecordId };
        endpoint.SetAttributeRawValue("ApiKey", SecretTestSupport.CreateProtector(GoneKeyId).Protect(Plaintext));
        entity.SetAttributeRawValue("Endpoints", new List<object?> { endpoint });
        return entity;
    }

    private async Task<JsonNode> ReadThroughGetRtEntitiesById()
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns([Entity()]);
        A.CallTo(() => resultSet.TotalCount).Returns(1);
        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(resultSet);

        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = root.RegisterChildNode("GetRtEntitiesById@1", 0, new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = SecretTestSupport.CkTypeId, RtIds = [RtId], TargetPath = "$.result"
        }, dataContext);

        await new GetRtEntitiesByIdNode(A.Fake<NodeDelegate>(), EtlContext).ProcessObjectAsync(dataContext, nodeContext);

        var json = dataContext.Get<JsonNode>("$")!;
        Assert.DoesNotContain(Plaintext, json.ToJsonString());
        Assert.DoesNotContain("enc:v", json.ToJsonString());
        return dataContext.Get<JsonNode>("$.result.items[0].attributes")
               ?? dataContext.Get<JsonNode>("$.result.Items[0].Attributes")!;
    }

    [Fact]
    public async Task Registered_UnknownKeyId_ReadsAsKeyMissing_KnownKeyAsSet()
    {
        var registration = new PipelineSecretMarkerRegistration(_adapterProtector);
        await registration.StartAsync(CancellationToken.None);
        try
        {
            var attributes = await ReadThroughGetRtEntitiesById();

            Assert.NotNull(attributes);
            Assert.Equal("""{"isSet":false,"keyMissing":true}""", attributes["Password"]!.ToJsonString());
            Assert.True(PipelineSecretValues.IsSecretMarker(attributes["Password"]));
            Assert.Contains("""{"isSet":true}""", attributes["Settings"]!.ToJsonString());
            Assert.Contains("""{"isSet":false,"keyMissing":true}""", attributes["Endpoints"]!.ToJsonString());
        }
        finally
        {
            await registration.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopped_FallsBackToTheEngineMarker()
    {
        var registration = new PipelineSecretMarkerRegistration(_adapterProtector);
        await registration.StartAsync(CancellationToken.None);
        await registration.StopAsync(CancellationToken.None);

        Assert.Null(PipelineSecretValues.ReadStateClassifier);
        var attributes = await ReadThroughGetRtEntitiesById();
        Assert.Equal("""{"isSet":true}""", attributes["Password"]!.ToJsonString());
    }

    [Fact]
    public void AdapterHost_RegistersTheMarkerClassifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["SecretEncryption:Keys:k1"] = SecretTestSupport.GenerateKey(),
                ["SecretEncryption:ActiveKeyId"] = "k1"
            }).Build());
        services.AddLogging();
        services.AddOctoMeshAdapter();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) &&
                                       d.ImplementationType == typeof(PipelineSecretMarkerRegistration));
    }
}
