using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Control;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class MakeHttpRequestNodeTests : NodeTestBase
{
    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string responseContent)
    {
        var handler = new MockHttpMessageHandler(statusCode, responseContent);
        return new HttpClient(handler);
    }

    private static readonly IMeshEtlContext EmptyEtlContext = CreateEtlContext(null);

    private static IMeshEtlContext CreateEtlContext(HttpApiSettings? settings, string entry = "TestApi")
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined(entry)).Returns(settings is not null);
        if (settings is not null)
        {
            A.CallTo(() => globalConfiguration.GetValue<HttpApiSettings>(entry)).Returns(settings);
        }

        return etlContext;
    }

    [Theory]
    [InlineData("https://host/api/v1", "/article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1/", "/article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1", "article", "https://host/api/v1/article")]
    [InlineData("https://host/api/v1/", "article", "https://host/api/v1/article")]
    public async Task ProcessObjectAsync_WithApiConfiguration_JoinsBaseAndPath(
        string baseUrl, string path, string expected)
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = path, TargetPath = "$.response",
            ApiConfiguration = "TestApi", AuthHeaderName = "AuthenticationToken"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"result\":[]}"));
        var etlContext = CreateEtlContext(new HttpApiSettings { BaseUrl = baseUrl, ApiKey = "token-1" });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(expected, handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData("", "token-1")]
    [InlineData("Bearer ", "Bearer token-1")]
    public async Task ProcessObjectAsync_WithApiConfiguration_SendsAuthHeader(string prefix, string expected)
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.response",
            ApiConfiguration = "TestApi", AuthHeaderName = "AuthenticationToken",
            AuthHeaderValuePrefix = prefix
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{\"result\":[]}"));
        var etlContext = CreateEtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = "token-1"
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(expected, handler.Requests.Single().Headers.GetValues("AuthenticationToken").Single());
    }

    [Fact]
    public async Task ProcessObjectAsync_AbsoluteUrlWithApiConfiguration_ThrowsAndSendsNothing()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://elsewhere.example.com/article", TargetPath = "$.response",
            ApiConfiguration = "TestApi"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var etlContext = CreateEtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = "token-1"
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProcessObjectAsync_UndefinedApiConfiguration_ThrowsWithDefaultErrorHandling()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.response", ApiConfiguration = "TestApi"
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), CreateEtlContext(null));

        // OnHttpError is untouched here: a configuration mistake is never a runtime outcome.
        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("TestApi", ex.Message);
        Assert.Empty(handler.Requests);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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

        var node = new MakeHttpRequestNode(next, httpClient, EmptyEtlContext);
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
        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Expected: the exact bytes the legacy Get<JsonNode> + ToJsonString(options) produced.
        var expected = ((JsonObject)testData["payload"]!.DeepClone())
            .ToJsonString(SystemTextJsonOptions.Default);
        Assert.Equal(expected, handler.LastBody);
        // Relaxed encoder: umlaut emitted literally, not \u-escaped; compact (no newlines).
        Assert.Contains("Grüße", handler.LastBody);
        Assert.DoesNotContain("\n", handler.LastBody);
    }

    [Fact]
    public async Task ProcessObjectAsync_FailingStatusWithDefaults_LogsStopsAndDoesNotThrow()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response"
        };
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Error(A<string>._, A<string>._, A<Exception>._, A<string>._, A<object[]>._))
            .MustHaveHappened();
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_RetriesExhaustedWithDefaults_StaysQuiet()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response",
            Retry = new HttpRetryOptions { MaxAttempts = 3, BackoffBaseSeconds = 0 },
            TimeoutSeconds = 5
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_FailingStatusWithThrow_Throws()
    {
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api", TargetPath = "$.response",
            OnHttpError = HttpErrorHandling.Throw
        };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.IsNotType<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task ProcessObjectAsync_ResponseHandlingFails_IsLoggedInBothModes()
    {
        // A response the storage step cannot handle is not an HTTP outcome, so OnHttpError does not
        // govern it: it stays reported rather than escaping, exactly as before the option existed.
        foreach (var mode in new[] { HttpErrorHandling.LogAndStop, HttpErrorHandling.Throw })
        {
            var config = new MakeHttpRequestNodeConfiguration
            {
                Method = "GET", Url = "https://host/api", TargetPath = "$.response",
                ResponseFormat = "Auto", OnHttpError = mode
            };
            var (dataContext, nodeContext, next, logger) =
                PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
            A.CallTo(() => dataContext.Set(A<string>._, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._)).Throws(new InvalidOperationException("boom"));
            var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("plain text"));

            var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
            await node.ProcessObjectAsync(dataContext, nodeContext);

            A.CallTo(() => logger.Error(A<string>._, A<string>._, A<Exception>._, A<string>._,
                A<object[]>._)).MustHaveHappened();
        }
    }

    [Fact]
    public async Task ProcessObjectAsync_ThrowMode_IsIsolatedByForEachContinueOnError()
    {
        // The isolation the per-item composition depends on, exercised through the real loop node
        // rather than asserted about the exception type: ForEach@1 absorbs every exception except
        // OperationCanceledException, so the node's failure has to be one it can absorb.
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(ForEachNode));
        builder.RegisterNode(typeof(MakeHttpRequestNode));

        // Second item fails permanently, the others answer.
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"ok\":1}"),
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom"),
            SequencedHttpMessageHandler.Json("{\"ok\":3}"));
        services.AddSingleton(new HttpClient(handler));
        services.AddSingleton(EmptyEtlContext);

        var logger = A.Fake<IPipelineLogger>();
        var dataContext = new DataContextImpl(
            JsonDocument.Parse("{\"orders\":[{\"id\":1},{\"id\":2},{\"id\":3}]}"));
        var forEachConfig = new ForEachNodeConfiguration
        {
            IterationPath = "$.orders",
            KeyPath = "$.key",
            TargetPath = "$.loopResult",
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true,
            Transformations =
            [
                new MakeHttpRequestNodeConfiguration
                {
                    Method = "GET", Url = "https://host/api/customer", TargetPath = "$.customer",
                    OnHttpError = HttpErrorHandling.Throw
                }
            ]
        };

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("ForEach", 0, forEachConfig, dataContext);
        var next = A.Fake<NodeDelegate>();

        var ex = await Assert.ThrowsAsync<PipelineExecutionException>(
            () => new ForEachNode(next).ProcessObjectAsync(dataContext, nodeContext));

        // One aggregated failure naming the failed index, and the other two iterations ran.
        Assert.Contains("[1]", ex.Message);
        Assert.Equal(3, handler.CallCount);
    }

    private static MakeHttpRequestNodeConfiguration PagingConfig(HttpPagingOptions paging)
    {
        return new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "https://host/api/article", TargetPath = "$.items",
            OnHttpError = HttpErrorHandling.Throw, Paging = paging
        };
    }

    private static string Page(params int[] ids)
    {
        return "{\"result\":[" + string.Join(",", ids.Select(i => $"{{\"id\":{i}}}")) + "]}";
    }

    [Fact]
    public async Task ProcessObjectAsync_Paging_CollectsEveryPageFlatAndInOrder()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Json(Page(3, 4)),
            SequencedHttpMessageHandler.Json(Page(5)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
        Assert.Equal("https://host/api/article?page=1&pageSize=2",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("https://host/api/article?page=3&pageSize=2",
            handler.Requests[2].RequestUri!.ToString());
        A.CallTo(() => dataContext.Set("$.items",
                A<JsonNode?>.That.Matches(n => n!.AsArray().Count == 5 &&
                                               n.AsArray()[0]!["id"]!.GetValue<int>() == 1 &&
                                               n.AsArray()[4]!["id"]!.GetValue<int>() == 5),
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_Paging_StopsOnEmptyPage()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Json(Page()));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingWithoutShortPageStop_WalksToTheEmptyPage()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, StopOnShortPage = false
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1)),
            SequencedHttpMessageHandler.Json(Page(2)),
            SequencedHttpMessageHandler.Json(Page()));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingFirstPageNumberZero_StartsAtZero()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, FirstPageNumber = 0
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Contains("page=0", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingKeepsAnExistingQuery()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Url = "https://host/api/salesOrder?status-eq=CONFIRMED";
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("https://host/api/salesOrder?status-eq=CONFIRMED&page=1&pageSize=2",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData("{\"other\":[]}")]
    [InlineData("{\"result\":{\"id\":1}}")]
    [InlineData("not json at all")]
    public async Task ProcessObjectAsync_PagingUnusableItemsPath_FailsInsteadOfStopping(string body)
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(body));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Contains("$.result", ex.Message);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingCapReached_FailsAndWritesNothing()
    {
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, MaxPages = 3
        });
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        // A target that ignores the page parameter answers with the same full page forever.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1, 2)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        var ex = await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Equal(3, handler.CallCount);
        Assert.Contains("3", ex.Message);
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingWithoutItemsPath_Throws()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "", PageSize = 2 });
        config.OnHttpError = HttpErrorHandling.LogAndStop; // a configuration mistake throws anyway
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(Page(1)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingRetriesOnePageInPlace()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Retry = new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"),
            SequencedHttpMessageHandler.Json(Page(3, 4)),
            SequencedHttpMessageHandler.Json(Page(5)));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Page 2 was retried rather than skipped, and page 1 was not fetched again.
        Assert.Equal(4, handler.CallCount);
        Assert.Equal("https://host/api/article?page=2&pageSize=2",
            handler.Requests[2].RequestUri!.ToString());
        A.CallTo(() => dataContext.Set("$.items",
                A<JsonNode?>.That.Matches(n => n!.AsArray().Count == 5),
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingExhaustedOnOnePage_FailsTheWholeRun()
    {
        var config = PagingConfig(new HttpPagingOptions { ItemsPath = "$.result", PageSize = 2 });
        config.Retry = new HttpRetryOptions { MaxAttempts = 2, BackoffBaseSeconds = 0 };
        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json(Page(1, 2)),
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData("{\"other\":[]}")]   // unusable itemsPath
    [InlineData("{\"result\":[{\"id\":1},{\"id\":2}]}")]   // full page forever, so the cap is hit
    public async Task ProcessObjectAsync_PagingFailureUnderDefault_IsQuietAndWritesNothing(string body)
    {
        // The paging failure paths follow OnHttpError like every other runtime outcome: with the
        // default they are reported and stop the branch instead of failing the execution.
        var config = PagingConfig(new HttpPagingOptions
        {
            ItemsPath = "$.result", PageSize = 2, MaxPages = 2
        });
        config.OnHttpError = HttpErrorHandling.LogAndStop;
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json(body));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EmptyEtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => logger.Error(A<string>._, A<string>._, A<Exception>._, A<string>._,
            A<object[]>._)).MustHaveHappened();
        A.CallTo(() => dataContext.Set(A<string>._, A<JsonNode?>._, A<DocumentModes>._,
            A<ValueKinds>._, A<TargetValueWriteModes>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_PagingDefaults_AreTheDocumentedOnes()
    {
        var paging = new HttpPagingOptions { ItemsPath = "$.result" };

        Assert.Equal("page", paging.PageParameterName);
        Assert.Equal("pageSize", paging.PageSizeParameterName);
        Assert.Equal(100, paging.PageSize);
        Assert.Equal(1, paging.FirstPageNumber);
        Assert.True(paging.StopOnShortPage);
        Assert.Equal(500, paging.MaxPages);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithApiConfiguration_NeverLogsTheKey()
    {
        // The configured header is attached directly rather than through the header-parameter
        // path, which reports every value it adds at debug level. Routing the key through there
        // would write it into the pipeline log of every run.
        const string key = "super-secret-token-value";
        var config = new MakeHttpRequestNodeConfiguration
        {
            Method = "GET", Url = "/article", TargetPath = "$.response",
            ApiConfiguration = "TestApi", AuthHeaderName = "AuthenticationToken"
        };
        var (dataContext, nodeContext, next, logger) =
            PrepareTestWithLogger<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var etlContext = CreateEtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = key
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The key did travel, so the assertion below is about where it did not.
        Assert.Equal(key, handler.Requests.Single().Headers.GetValues("AuthenticationToken").Single());
        var logged = Fake.GetCalls(logger).SelectMany(c => c.Arguments.SelectMany(Flatten)).ToList();
        Assert.NotEmpty(logged);
        Assert.DoesNotContain(logged, text => text.Contains(key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Renders one logged argument as the strings it can put into a message, unwrapping the
    /// params array the logger takes so a value inside it is not hidden behind "System.Object[]".
    /// </summary>
    private static IEnumerable<string> Flatten(object? argument)
    {
        if (argument is object?[] many)
        {
            return many.Select(a => a?.ToString() ?? "");
        }

        return [argument?.ToString() ?? ""];
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
