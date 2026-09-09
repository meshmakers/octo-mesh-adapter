using System.Net;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// AB#5145: SignalSender@1 resolves its sending number + bridge URL through the shared
/// resolution order — the tenant's registered System.Communication/SignalChannel (the entry the
/// communication controller always projects under "signal-channel") wins over the deprecated
/// legacy settings; with neither, the node reports and stops without an HTTP call. The full
/// resolution matrix is pinned in <see cref="SignalChannelEndpointResolverTests"/>; these tests
/// pin the node-level wiring: which endpoint the send actually goes to.
/// </summary>
public class SignalSenderNodeTests : SessionNodeTestBase
{
    private const string LegacySettingsName = "SignalImportSettings";

    private const string RegisteredChannelJson =
        """
        {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
         "RegistrationState":2}}
        """;

    private const string LegacySettingsJson =
        """
        {"attributes":{"SignalNumber":"+43677222222222","SignalApiUrl":"http://legacy.signal:8080"}}
        """;

    private readonly IGlobalConfiguration _globalConfiguration;
    private readonly RecordingHandler _handler;
    private readonly IHttpClientFactory _httpClientFactory;

    public SignalSenderNodeTests()
    {
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _globalConfiguration.IsDefined(A<string>._)).Returns(false);
        A.CallTo(() => EtlContext.GlobalConfiguration).Returns(_globalConfiguration);

        _handler = new RecordingHandler(HttpStatusCode.OK, """{"timestamp":1}""");
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => _httpClientFactory.CreateClient("Signal"))
            .Returns(new HttpClient(_handler, disposeHandler: false));
    }

    private void DefineConfiguration(string name, string rawJson)
    {
        A.CallTo(() => _globalConfiguration.IsDefined(name)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetRawJson(name)).Returns(rawJson);
    }

    private static SignalSenderNodeConfiguration LegacyWiredConfiguration() => new()
    {
        SettingsConfiguration = LegacySettingsName,
        NumberAttribute = "SignalNumber",
        ApiUrlAttribute = "SignalApiUrl",
        Recipient = "+4915777777777",
        Message = "hello"
    };

    [Fact]
    public async Task RegisteredChannel_WinsOverLegacy_SendsThroughTheChannelBridge()
    {
        DefineConfiguration("signal-channel", RegisteredChannelJson);
        DefineConfiguration(LegacySettingsName, LegacySettingsJson);
        var (dataContext, nodeContext, next) = PrepareTest(LegacyWiredConfiguration());
        var node = new SignalSenderNode(next, _httpClientFactory, EtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("http://bridge.signal:8080/v2/send", _handler.LastRequest!.RequestUri!.ToString());
        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.Equal("+43677111111111", body["number"]?.GetValue<string>());
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task LegacySettings_StillWork_WhenNoChannelIsRegistered()
    {
        DefineConfiguration(LegacySettingsName, LegacySettingsJson);
        var (dataContext, nodeContext, next) = PrepareTest(LegacyWiredConfiguration());
        var node = new SignalSenderNode(next, _httpClientFactory, EtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("http://legacy.signal:8080/v2/send", _handler.LastRequest!.RequestUri!.ToString());
        var body = JsonNode.Parse(_handler.LastBody!)!.AsObject();
        Assert.Equal("+43677222222222", body["number"]?.GetValue<string>());
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task NoEndpointConfigured_ReportsAndStops_WithoutAnyHttpCall()
    {
        var config = new SignalSenderNodeConfiguration
        {
            Recipient = "+4915777777777",
            Message = "hello"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new SignalSenderNode(next, _httpClientFactory, EtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(0, _handler.CallCount);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task NotRegisteredChannel_WithoutLegacy_ReportsAndStops()
    {
        const string codePendingJson =
            """
            {"attributes":{"Number":"+43677111111111","ApiUrl":"http://bridge.signal:8080",
             "RegistrationState":1}}
            """;
        DefineConfiguration("signal-channel", codePendingJson);
        var config = new SignalSenderNodeConfiguration
        {
            Recipient = "+4915777777777",
            Message = "hello"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var node = new SignalSenderNode(next, _httpClientFactory, EtlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(0, _handler.CallCount);
        VerifyNextNotCalled(next, dataContext, nodeContext);
    }
}
