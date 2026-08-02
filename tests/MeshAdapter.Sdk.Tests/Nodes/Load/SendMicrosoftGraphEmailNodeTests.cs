using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

public class SendMicrosoftGraphEmailNodeTests : NodeTestBase
{
    private const string ServerConfig = "graph-1";
    private const string Mailbox = "accounting@meshmakers.io";
    private const string TenantId = "tenant-guid";
    private const string ClientId = "client-guid";
    private const string ClientSecret = "secret";
    private const string AccessToken = "graph-access-token";

    private readonly IMeshEtlContext _etlContext;
    private readonly IGlobalConfiguration _globalConfiguration;
    private readonly QueueHandler _handler;
    private readonly IHttpClientFactory _httpClientFactory;

    public SendMicrosoftGraphEmailNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => _etlContext.GlobalConfiguration).Returns(_globalConfiguration);

        // First call = token endpoint, second call = sendMail (202 Accepted).
        _handler = new QueueHandler(
            (HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}"}"""),
            (HttpStatusCode.Accepted, ""));
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        // Production CreateClient() returns a fresh client each call — mirror that so the
        // node can set Timeout on each (a reused instance throws once a request is sent).
        A.CallTo(() => _httpClientFactory.CreateClient(string.Empty))
            .ReturnsLazily(() => new HttpClient(_handler, disposeHandler: false));

        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<SendMicrosoftGraphEmailNode.GraphConfiguration>(ServerConfig))
            .Returns(new SendMicrosoftGraphEmailNode.GraphConfiguration
            {
                AzureTenantId = TenantId,
                ClientId = ClientId,
                ClientSecret = ClientSecret
            });
    }

    private SendMicrosoftGraphEmailNode CreateNode(NodeDelegate next) =>
        new(next, _etlContext, _httpClientFactory);

    private SendMicrosoftGraphEmailNodeConfiguration CreateConfig() => new()
    {
        ServerConfiguration = ServerConfig,
        Mailbox = Mailbox,
        SubjectPath = "$.subject",
        ToPath = "$.to",
        Path = "$.body"
    };

    [Fact]
    public async Task ProcessObjectAsync_SendsGraphSendMailWithTokenAndPayload()
    {
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<SendMicrosoftGraphEmailNodeConfiguration>(config);
        SetupGetSimpleValueByPath(dataContext, "$.subject", "[ToDo #abc] Beleg fehlt");
        A.CallTo(() => dataContext.GetArray<string>("$.to")).Returns(new[] { "bob@example.com" });
        SetupGetSimpleValueByPath(dataContext, "$.body", "Hallo Bob\n\nBitte nachreichen.");

        var node = CreateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, _handler.Requests.Count);

        // 1) token request
        var tokenReq = _handler.Requests[0];
        Assert.Equal(HttpMethod.Post, tokenReq.Method);
        Assert.Equal($"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token",
            tokenReq.RequestUri!.ToString());

        // 2) sendMail request
        var sendReq = _handler.Requests[1];
        Assert.Equal(HttpMethod.Post, sendReq.Method);
        Assert.Equal($"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(Mailbox)}/sendMail",
            sendReq.RequestUri!.ToString());
        Assert.Equal("Bearer", sendReq.Headers.Authorization?.Scheme);
        Assert.Equal(AccessToken, sendReq.Headers.Authorization?.Parameter);

        var body = JsonNode.Parse(_handler.Bodies[1])!.AsObject();
        var message = body["message"]!.AsObject();
        Assert.Equal("[ToDo #abc] Beleg fehlt", message["subject"]?.GetValue<string>());
        Assert.Equal("HTML", message["body"]!["contentType"]?.GetValue<string>());
        Assert.Contains("Bitte nachreichen", message["body"]!["content"]?.GetValue<string>());
        var recipients = (JsonArray)message["toRecipients"]!;
        Assert.Single(recipients);
        Assert.Equal("bob@example.com",
            recipients[0]!["emailAddress"]!["address"]?.GetValue<string>());
        Assert.True(body["saveToSentItems"]?.GetValue<bool>());

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_MissingServerConfiguration_Throws()
    {
        A.CallTo(() => _globalConfiguration.IsDefined(ServerConfig)).Returns(false);
        var config = CreateConfig();
        var (dataContext, nodeContext, next) = PrepareTest<SendMicrosoftGraphEmailNodeConfiguration>(config);
        var node = CreateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>
    /// HttpMessageHandler that returns queued responses in order and records every
    /// request (URI, method, headers) and request body.
    /// </summary>
    private sealed class QueueHandler(params (HttpStatusCode Status, string Body)[] responses)
        : HttpMessageHandler
    {
        private int _index;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var (status, body) = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
