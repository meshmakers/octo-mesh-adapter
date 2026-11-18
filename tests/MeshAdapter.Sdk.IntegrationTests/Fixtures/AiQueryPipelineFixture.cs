using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     The adapter's own service registration (<c>AddOctoMeshAdapter</c>) on a MongoDB, with the two
///     things an LLM query reaches outside the process replaced: the HTTP clients of the Anthropic API
///     and of the MCP server answer from <see cref="Http" />, and tokens come from
///     <see cref="TokenService" />. Everything else - pipeline deserialization, orchestrator, node
///     construction through the container, the named HTTP clients - is the adapter's.
/// </summary>
public class AiQueryPipelineFixture : ImportCkModelFixture
{
    /// <summary>Where the adapter's Anthropic and MCP clients send their requests; set per test.</summary>
    public SwitchableHandler Http { get; } = new();

    public IServiceAccountTokenService TokenService { get; } = A.Fake<IServiceAccountTokenService>();

    protected override async Task InitializeServicesAsync()
    {
        Services.AddSingleton(A.Fake<IPipelineLogger>());
        Services.AddOctoMeshAdapter();
        // AddOctoMeshAdapter wires the distributed tenant notifications; there is no broker in this test.
        Services.AddSingleton(A.Fake<IDistributionEventHubService>());

        // After AddOctoMeshAdapter: the last registration of a service is the one that is resolved.
        Services.AddSingleton(TokenService);
        Services.AddHttpClient(AnthropicMessagesChatClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Http);
        Services.AddHttpClient("OctoMcp").ConfigurePrimaryHttpMessageHandler(() => Http);

        await base.InitializeServicesAsync();
    }

    /// <summary>A handler that forwards to the handler of the running test.</summary>
    public sealed class SwitchableHandler : HttpMessageHandler
    {
        private HttpMessageInvoker? _current;

        public void SwitchTo(HttpMessageHandler handler) => _current = new HttpMessageInvoker(handler, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return (_current ?? throw new InvalidOperationException("No handler is set for this test."))
                .SendAsync(request, cancellationToken);
        }

        // The HTTP client factory disposes its handlers when they expire; this one outlives them.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
