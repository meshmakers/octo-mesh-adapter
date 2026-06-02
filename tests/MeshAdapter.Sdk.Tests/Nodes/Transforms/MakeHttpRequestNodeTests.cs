using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MakeHttpRequestNodeTests : NodeTestBase
{
    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string responseContent)
    {
        var handler = new MockHttpMessageHandler(statusCode, responseContent);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSuccessfulGetRequest_SetsResponseOnDataContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"result\":\"success\"}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.response",
                A<JsonNode?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    // Bodies that aren't JSON OBJECTS must be stored as text, not parsed as JSON.
    // Legacy JObject.Parse threw for these and fell through to the text branch.
    // The STJ JsonNode.Parse accepts all of them and (pre-fix) silently produced
    // JsonValue / JsonArray, which a downstream Get<string>(targetPath) reader
    // cannot recover the original text from.
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"foo\"")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("[{\"a\":1}]")]
    public async Task ProcessObjectAsync_NonObjectResponse_StoredAsText(string responseBody)
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, responseBody);

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Set<string> with the raw body must be the call that happens.
        A.CallTo(() => dataContext.Set(
                "$.response",
                responseBody,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();

        // Set<JsonNode> must NOT happen — that's the regression where scalar/array
        // bodies were silently parsed as JSON.
        A.CallTo(() => dataContext.Set(
                "$.response",
                A<JsonNode?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_ObjectResponse_StoredAsJson()
    {
        // Sanity: JSON-object responses still go through the JsonNode path.
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"result\":\"ok\"}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.response",
                A<JsonNode?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSuccessfulRequest_CallsNext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"result\":\"success\"}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithFailedRequest_DoesNotCallNext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithUrlPath_ResolvesUrlFromDataContext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            UrlPath = "$.url",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"ok\":true}");

        SetupGetSimpleValueByPath(dataContext, "$.url", "http://example.com/api/data");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMissingUrlConfig_DoesNotCallNext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithPathParameters_ReplacesInUrl()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/{userId}/data",
            TargetPath = "$.response",
            PathParameters =
            [
                new HttpPathParameter { Name = "userId", Value = "123" }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNonJsonResponse_SetsStringResponse()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "plain text response");

        string? capturedString = null;
        A.CallTo(() => dataContext.Set(
                "$.response",
                A<string?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) =>
                capturedString = value);

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("plain text response", capturedString);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithInvalidMethod_DoesNotCallNext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "INVALID",
            Url = "http://example.com/api/data",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithPostAndBody_SendsBodyContent()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "POST",
            Url = "http://example.com/api/data",
            Body = "{\"name\":\"test\"}",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"id\":1}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithHeaderParameters_AddsHeaders()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            Url = "http://example.com/api/data",
            TargetPath = "$.response",
            HeaderParameters =
            [
                new HttpHeaderParameter { Name = "X-Custom-Header", Value = "test-value" }
            ]
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    /// <summary>
    /// Characterization: a BodyPath-sourced request body must be serialized to the exact compact
    /// bytes the former Get&lt;JsonNode&gt;().ToJsonString(SystemTextJsonOptions.Default) produced —
    /// relaxed encoder (non-ASCII literal), no indentation. The migrated GetBody reads the value
    /// via Get&lt;object?&gt; instead.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_WithBodyPath_SendsCompactRelaxedEncodedBody()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "POST",
            Url = "http://example.com/api/data",
            BodyPath = "$.payload",
            TargetPath = "$.response"
        };

        var testData = new JsonObject
        {
            ["payload"] = new JsonObject
            {
                ["name"] = "Grüße",
                ["count"] = 42,
                ["nested"] = new JsonObject { ["flag"] = true }
            }
        };

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                .BuildServiceProvider(services),
            logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("MakeHttpRequest", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();

        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var node = new MakeHttpRequestNode(next, new HttpClient(handler));
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Expected: the exact bytes the legacy Get<JsonNode> + ToJsonString(options) produced.
        var expected = ((JsonObject)testData["payload"]!.DeepClone())
            .ToJsonString(SystemTextJsonOptions.Default);
        Assert.Equal(expected, handler.LastBody);
        // Relaxed encoder: umlaut emitted literally, not \u-escaped; compact (no newlines).
        Assert.Contains("Grüße", handler.LastBody);
        Assert.DoesNotContain("\n", handler.LastBody);
    }

    private class MockHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            };
            return Task.FromResult(response);
        }
    }

    private class CapturingHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(content) };
        }
    }
}
