using System.Net;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// What a real pipeline definition produces, which a C# object initializer cannot reach: an
/// initializer applies only when the key is <em>absent</em>, so a definition carrying an explicit
/// null hands the node a value nobody wrote. The assertions are about the behaviour that follows
/// rather than about the property values - once the node resolves them, asserting the properties
/// would only restate the resolution it is meant to prove.
/// </summary>
public class MakeHttpRequestConfigurationDeserializationTests : NodeTestBase
{
    private const string Entry = "TestApi";

    private static IMeshEtlContext EtlContext(HttpApiSettings? settings = null)
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(globalConfiguration);
        A.CallTo(() => globalConfiguration.IsDefined(Entry)).Returns(settings is not null);
        if (settings is not null)
        {
            A.CallTo(() => globalConfiguration.GetValue<HttpApiSettings>(Entry)).Returns(settings);
        }

        return etlContext;
    }

    private static async Task<MakeHttpRequestNodeConfiguration> DeserializeAsync(string transformationYaml)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataPipelineSerializer();
        builder.RegisterNode(typeof(MakeHttpRequestNode));
        var serializer = services.BuildServiceProvider()
            .GetRequiredService<IPipelineConfigurationSerializer>();

        var root = await serializer.DeserializeAsync("transformations:\n" + transformationYaml);
        return root.Transformations!.OfType<MakeHttpRequestNodeConfiguration>().Single();
    }

    [Fact]
    public async Task Deserialize_SectionsOmitted_UsesDocumentedDefaults()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
            """);

        Assert.Null(config.Paging);
        Assert.Null(config.Retry);
        Assert.Null(config.TimeoutSeconds);
        Assert.Equal(HttpErrorHandling.LogAndStop, config.OnHttpError);
        Assert.Equal("Authorization", config.AuthHeaderName);
        Assert.Equal("", config.AuthHeaderValuePrefix);
    }

    [Fact]
    public async Task Deserialize_ExplicitNullSections_DoNotReachTheNodeAsNull()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
                retry: null
                paging: null
                timeoutSeconds: null
                onHttpError: null
            """);

        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"result\":\"ok\"}"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EtlContext());
        var exception = await Record.ExceptionAsync(() => node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Null(exception);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpErrorHandling.LogAndStop, config.OnHttpError);
    }

    [Fact]
    public async Task Deserialize_ExplicitNullAuthStrings_SendTheDocumentedHeader()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: /article
                targetPath: $.response
                apiConfiguration: TestApi
                authHeaderName: null
                authHeaderValuePrefix: null
            """);

        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        var etlContext = EtlContext(new HttpApiSettings
        {
            BaseUrl = "https://host/api/v1", ApiKey = "token-1"
        });

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // A null header name never reaches the target: it fails while the request is being built.
        Assert.Equal("token-1", handler.Requests.Single().Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Deserialize_ExplicitNullPagingValues_RequestTheDocumentedFirstPage()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
                paging:
                  itemsPath: $.result
                  pageParameterName: null
                  pageSizeParameterName: null
                  pageSize: null
                  firstPageNumber: null
                  stopOnShortPage: null
                  maxPages: null
            """);

        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Json("{\"result\":[]}"));

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EtlContext());
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Parameter names, first page number and page size all as documented - and the walk ran at
        // all, which a maxPages resolved to zero would have prevented.
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://host/api?page=1&pageSize=100", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Deserialize_ExplicitNullBackoffBase_StillWaitsTheDocumentedDelay()
    {
        var config = await DeserializeAsync("""
              - type: MakeHttpRequest@1
                method: GET
                url: https://host/api
                targetPath: $.response
                onHttpError: Throw
                retry:
                  maxAttempts: 3
                  backoffBaseSeconds: null
            """);

        var (dataContext, nodeContext, next) = PrepareTest<MakeHttpRequestNodeConfiguration>(config);
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable, "busy"));
        var time = new RecordingTimeProvider();

        var node = new MakeHttpRequestNode(next, new HttpClient(handler), EtlContext(), time);
        var pending = node.ProcessObjectAsync(dataContext, nodeContext);
        while (!pending.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(() => pending);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], time.RequestedDelays);
    }

    /// <summary>
    /// Records the delays the node asks the clock for, so a resolved backoff base is pinned by what
    /// was waited for rather than by restating the resolution.
    /// </summary>
    private sealed class RecordingTimeProvider : FakeTimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime,
            TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
