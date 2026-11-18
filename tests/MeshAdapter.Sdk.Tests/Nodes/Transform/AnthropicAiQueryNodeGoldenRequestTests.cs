using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     Golden requests of <c>AnthropicAiQuery@1</c>: every HTTP request the node sends for three real
///     pipeline shapes (document analysis, chat with MCP tools on the caller's identity, knowledge
///     generation from a prompt template) is compared <b>byte for byte</b> with a recorded file, together
///     with the data the node leaves in the data context.
///     <para>
///         The files were recorded from the node before its internals were moved behind the shared LLM
///         engine. They are the proof that a deployed pipeline sends the same request afterwards: cache
///         breakpoints, <c>output_config</c>, headers, the MCP handshake and the order of every property.
///         The other test classes of this node compare parsed JSON, which a changed property order, a
///         changed number format or a lost <c>cache_control</c> on an unasserted block would pass.
///     </para>
///     <para>
///         A deliberate change of the wire format is recorded again with the environment variable
///         <c>OCTO_UPDATE_GOLDEN_REQUESTS=1</c> and reviewed as a diff of the files in
///         <c>Resources/GoldenRequests/AnthropicAiQuery</c>.
///     </para>
/// </summary>
public class AnthropicAiQueryNodeGoldenRequestTests
{
    [Fact]
    public async Task DocumentAnalysis_JsonWithoutSchema()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.DocumentAnalysis();

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("document-analysis");
    }

    [Fact]
    public async Task ChatWithMcpTools_DelegatedToTheCaller()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.ChatWithMcpTools();

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("chat-mcp-delegation");
    }

    [Fact]
    public async Task KnowledgeGeneration_FromATemplate_WithSchemaCachedSourceAndARepairCall()
    {
        var (scenario, config) = AnthropicAiQueryGoldenScenario.KnowledgeGeneration();

        await scenario.RunAsync(config);

        scenario.AssertMatchesGolden("knowledge-generation");
    }
}
