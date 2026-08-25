using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

public class HttpApiSettingsResolverTests : NodeTestBase
{
    private const string Entry = "WeClappApi";
    private const string Key = "super-secret-token-value";

    private readonly IMeshEtlContext _etlContext = A.Fake<IMeshEtlContext>();
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    public HttpApiSettingsResolverTests()
    {
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);
    }

    private INodeContext NodeContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.result", ApiConfiguration = Entry
        };
        var (_, nodeContext, _) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        return nodeContext;
    }

    [Fact]
    public void Resolve_EntryDefined_ReturnsSettings()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry))
            .Returns(new HttpApiSettings { BaseUrl = "https://host/api/v1", ApiKey = Key });

        var settings = HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext());

        Assert.Equal("https://host/api/v1", settings.BaseUrl);
        Assert.Equal(Key, settings.ApiKey);
    }

    [Fact]
    public void Resolve_EntryNotDefined_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(false);

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
        Assert.Contains(Entry, ex.Message);
    }

    [Theory]
    [InlineData("", Key)]
    [InlineData("   ", Key)]
    [InlineData("https://host/api/v1", "")]
    [InlineData("https://host/api/v1", "   ")]
    public void Resolve_IncompleteEntry_Throws(string baseUrl, string apiKey)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry))
            .Returns(new HttpApiSettings { BaseUrl = baseUrl, ApiKey = apiKey });

        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
        Assert.Contains(Entry, ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public void Resolve_NullPayload_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(Entry)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<HttpApiSettings>(Entry)).Returns(null!);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => HttpApiSettingsResolver.Resolve(_etlContext, Entry, NodeContext()));
    }

    [Fact]
    public void ToString_MasksTheKey()
    {
        var text = new HttpApiSettings { BaseUrl = "https://host/api/v1", ApiKey = Key }.ToString();

        Assert.DoesNotContain(Key, text);
        Assert.Contains("https://host/api/v1", text);
    }
}
