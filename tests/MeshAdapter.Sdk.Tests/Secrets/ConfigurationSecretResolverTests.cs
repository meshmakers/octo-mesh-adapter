using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Secrets;

/// <summary>
/// AB#5538 review: configurations shipped by the controller carry revealed Secret values. The CK-cache
/// resolver names the Secret attributes of a configuration type, and GetPipelineConfigByCkTypeId@1
/// registers their values before writing the configurations into the data context.
/// </summary>
public class ConfigurationSecretResolverTests
{
    // Obviously fake test values, never real credentials.
    private const string Password = "fake-config-password-7";
    private const string ApiKey = "fake-config-api-key-8";

    private static readonly string ConfigJson = $$"""
        {
          "attributes": {
            "Name": "cfg",
            "Password": "{{Password}}",
            "Endpoints": [ { "Url": "https://example.com", "ApiKey": "{{ApiKey}}" } ]
          }
        }
        """;

    [Fact]
    public void Resolver_NamesTopLevelAndRecordSecrets()
    {
        var resolver = new CkConfigurationSecretAttributeResolver(SecretTestSupport.CreateCkCache());

        var names = resolver.GetSecretAttributeNames(SecretTestSupport.TenantId, SecretTestSupport.CkTypeId);

        Assert.NotNull(names);
        Assert.Equal(["ApiKey", "Password"], names!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Resolver_UnknownType_AnswersNull()
    {
        var resolver = new CkConfigurationSecretAttributeResolver(SecretTestSupport.CreateCkCache());

        Assert.Null(resolver.GetSecretAttributeNames("other-tenant", SecretTestSupport.CkTypeId));
    }

    [Fact]
    public async Task GetPipelineConfigByCkTypeId_MasksTheRevealedSecretsInTheSnapshot()
    {
        var etl = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etl.TenantId).Returns(SecretTestSupport.TenantId);
        var global = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etl.GlobalConfiguration).Returns(global);
        A.CallTo(() => global.GetAllRawJsonByCkTypeId(A<string>._)).Returns([ConfigJson]);

        var dataContext = new DataContextImpl();
        var debugger = new DefaultPipelineDebugger(NullLoggerFactory.Instance);
        debugger.RegisterPipelineRtEntityId(new RtEntityId("Test/Pipeline", OctoObjectId.GenerateNewId()),
            Guid.NewGuid());
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext, debugger);
        var nodeContext = root.RegisterChildNode("GetPipelineConfigByCkTypeId@1", 0,
            new GetPipelineConfigByCkTypeIdNodeConfiguration
            {
                CkTypeId = SecretTestSupport.CkTypeId.ToString(), TargetPath = "$.configs"
            }, dataContext);
        var resolver = new CkConfigurationSecretAttributeResolver(SecretTestSupport.CreateCkCache());

        await new GetPipelineConfigByCkTypeIdNode(A.Fake<NodeDelegate>(), etl, resolver)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(Password, dataContext.Get<string>("$.configs[0].attributes.Password"));
        nodeContext.Unregister(dataContext);
        var output = debugger.GetDebugInformation().DebugPoints.Single(p => p.Output != null).Output!;
        Assert.DoesNotContain(Password, output);
        Assert.DoesNotContain(ApiKey, output);
        Assert.Contains("https://example.com", output);
        Assert.Equal("***",
            JsonNode.Parse(output)!["configs"]![0]!["attributes"]!["Password"]!.GetValue<string>());
    }

    [Fact]
    public void AdapterHost_RegistersTheCkCacheResolver()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddOctoMeshAdapter();

        var descriptors = services.Where(d => d.ServiceType == typeof(IConfigurationSecretAttributeResolver)).ToList();

        var descriptor = Assert.Single(descriptors);
        Assert.Equal(typeof(CkConfigurationSecretAttributeResolver), descriptor.ImplementationType);
    }
}
