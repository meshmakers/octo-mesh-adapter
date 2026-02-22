using System.Net;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MakeHttpRequestNodeTests
{
    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        MakeHttpRequestNodeConfiguration config, JToken? testData = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            "MakeHttpRequest",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"result\":\"success\"}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.SetValueByPath(
                "$.response",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JToken>._))
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"result\":\"success\"}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Server Error");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"ok\":true}");

        A.CallTo(() => dataContext.GetSimpleValueByPath<string>("$.url"))
            .Returns("http://example.com/api/data");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithMissingUrlConfig_DoesNotCallNext()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET",
            TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "plain text response");

        JToken? capturedValue = null;
        A.CallTo(() => dataContext.SetValueByPath(
                "$.response",
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<JToken>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, JToken value) =>
                capturedValue = value);

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedValue);
        Assert.Equal("plain text response", capturedValue!.ToString());
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{\"id\":1}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, "{}");

        var node = new MakeHttpRequestNode(next, httpClient);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
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
}
