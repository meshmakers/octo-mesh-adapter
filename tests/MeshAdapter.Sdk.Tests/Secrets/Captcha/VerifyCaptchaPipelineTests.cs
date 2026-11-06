using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Services.HttpRequests;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public class VerifyCaptchaPipelineTests : VerifyCaptchaTestBase
{
    private const string Sentinel = "sentinel-pipeline-secret-93be41";
    private const string RequestBody = $$"""{"captchaToken":"{{FakeTurnstileHandler.DummyToken}}"}""";

    private readonly RecordingLoggerProvider _logs = new();
    private readonly RecordingAdapterEventService _events = new();
    private readonly IPipelineExecutionReporter _reporter = A.Fake<IPipelineExecutionReporter>();
    private readonly HttpRequestService _httpRequestService;
    private readonly MeshAdapterTriggerContext _triggerContext;
    private readonly DefaultPipelineDebugger _debugger;

    public VerifyCaptchaPipelineTests()
    {
        A.CallTo(() => EtlContext.Properties).Returns(new Dictionary<string, object?>());
        Handler = new FakeTurnstileHandler((form, _) => Task.FromResult(form.GetValueOrDefault("secret") == Sentinel
            ? FakeTurnstileHandler.Json(FakeTurnstileHandler.PassBody)
            : FakeTurnstileHandler.FailWith("invalid-input-secret")));
        StubDefaults(DefaultEntity(Sentinel));

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        services.AddDataPipeline().RegisterNode<VerifyCaptchaNode>().RegisterEtlContext<IMeshEtlContext>();
        var registry = A.Fake<IPipelineRegistryService>();
        var contextCreator = A.Fake<IContextCreatorService>();
        services.AddSingleton(registry);
        services.AddSingleton(contextCreator);
        services.AddSingleton(_reporter);
        services.AddSingleton<ICkCacheService>(_ => CkCacheService);
        services.AddSingleton<ISecretAttributeProtector>(_ => Protector);
        services.AddSingleton(_ => HttpClientFactory());
        services.AddSingleton<IPipelineDebugger>(serviceProvider => new DefaultPipelineDebugger(serviceProvider.GetRequiredService<ILoggerFactory>()));
        var provider = services.BuildServiceProvider();
        _debugger = (DefaultPipelineDebugger)provider.GetRequiredService<IPipelineDebugger>();

        var pipelineRtEntityId = new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId());
        var dataFlowRtId = OctoObjectId.GenerateNewId();
        var registration = new PipelineRegistration(SecretTestSupport.TenantId, dataFlowRtId, pipelineRtEntityId, true,
            new NodeDefinitionRoot
            {
                Transformations =
                [
                    Config(),
                    new SetPipelineExecutionResultNodeConfiguration { Path = "$" }
                ]
            }, A.Fake<IGlobalConfiguration>(), new Dictionary<string, object?>());
        PipelineRegistration? found = registration;
        A.CallTo(() => registry.TryGetPipelineRegistration(SecretTestSupport.TenantId, pipelineRtEntityId, out found!))
            .Returns(true);
        A.CallTo(() => contextCreator.CreateEtlContext<IMeshEtlContext>(A<PipelineRegistration>._,
            A<ExecutePipelineOptions>._, A<Guid>._)).Returns(Task.FromResult(EtlContext));

        var triggerNodeContext = A.Fake<INodeContext>();
        A.CallTo(() => triggerNodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration2>())
            .Returns(new FromHttpRequestNodeConfiguration2
                { Path = "/registration", Method = HttpMethod.Post, AllowAnonymous = true });
        _triggerContext = new MeshAdapterTriggerContext(provider, SecretTestSupport.TenantId, dataFlowRtId,
            pipelineRtEntityId, triggerNodeContext, A.Fake<IGlobalConfiguration>());
        _httpRequestService = new HttpRequestService(
            Microsoft.Extensions.Options.Options.Create(new AdapterOptions { DedicatedTenantId = SecretTestSupport.TenantId }),
            Microsoft.Extensions.Options.Options.Create(new MeshAdapterConfiguration { AuditAnonymousInvocations = true }),
            _events, NullLogger<HttpRequestService>.Instance);
    }

    [Fact]
    public async Task HttpRoute_AnswersWithTheVerdict_AndNoSentinelAnywhere()
    {
        var response = await Post(RequestBody);

        var verdict = JsonNode.Parse(response)!["captcha"]!.AsObject();
        AssertResult(verdict, true, "default", null);
        Assert.Equal(Sentinel, Assert.Single(Handler.Requests).Form["secret"]);
        AssertSentinelAbsent(response);
    }

    [Fact]
    public async Task HttpRoute_WhenTheProviderFails_StillLeaksNothing()
    {
        Handler = new FakeTurnstileHandler((_, _) => throw new System.Net.Http.HttpRequestException("refused"));

        var response = await Post(RequestBody);

        AssertResult(JsonNode.Parse(response)!["captcha"]!.AsObject(), false, "default", "verification-unavailable");
        AssertSentinelAbsent(response);
    }

    [Fact]
    public async Task DryRun_LeaksNothing()
    {
        var result = await _triggerContext.ExecuteAsync(
            new ExecutePipelineOptions(DateTime.UtcNow) { IsDryRun = true }, JsonNode.Parse(Input()));

        AssertSentinelAbsent(JsonSerializer.Serialize(result));
    }

    private async Task<string> Post(string body)
    {
        var node = new FromHttpRequestNode2(NullLogger<FromHttpRequestNode2>.Instance, _httpRequestService, _events);
        await node.StartAsync(_triggerContext);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = $"/{SecretTestSupport.TenantId}/registration";
        context.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        Assert.True(await _httpRequestService.SendRequestAsync(context));
        await node.StopAsync(_triggerContext);
        return Encoding.UTF8.GetString(responseBody.ToArray());
    }

    private void AssertSentinelAbsent(string response)
    {
        var reported = string.Join("|", Fake.GetCalls(_reporter).SelectMany(call => call.Arguments)
            .Select(argument => argument is string text ? text : JsonSerializer.Serialize(argument)));
        var all = new StringBuilder()
            .Append(response)
            .Append(reported)
            .Append(JsonSerializer.Serialize(_debugger.GetDebugInformation()))
            .Append(string.Join("|", _logs.Messages))
            .Append(string.Join("|", _events.Events))
            .ToString();
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Sentinel));

        Assert.NotEmpty(_debugger.GetDebugInformation().DebugPoints);
        Assert.Contains(Fake.GetCalls(_reporter), call => call.Method.Name == nameof(IPipelineExecutionReporter.ReportExecutionEndAsync));
        Assert.DoesNotContain(Sentinel, all);
        Assert.DoesNotContain(base64, all);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Messages)
                {
                    owner.Messages.Add(formatter(state, exception) + exception);
                }
            }
        }
    }
}
