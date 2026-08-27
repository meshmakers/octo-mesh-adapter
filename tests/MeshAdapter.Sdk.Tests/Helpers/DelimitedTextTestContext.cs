using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Shared setup for the delimited-text renderer tests: a real <see cref="DataContextImpl" /> over
/// the test data, wrapped by FakeItEasy so that reads (SelectMatches/GetKind/Get) run against the
/// real implementation while Set calls can be observed. This exercises the genuine path-resolution
/// behavior end-to-end rather than a mocked approximation of it.
/// </summary>
public static class DelimitedTextTestContext
{
    public static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) Prepare(
        RenderDelimitedTextNodeConfiguration config, JsonNode? testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();

        var data = testData ?? new JsonObject();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(data.ToJsonString()));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("RenderDelimitedText", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    /// <summary>
    /// Observes what the node writes to its target path. The returned accessor is read after the
    /// node ran; it stays null when the node wrote nothing at all, which is a different outcome
    /// from writing an empty document and has to stay distinguishable.
    /// </summary>
    public static Func<string?> CaptureWrite(IDataContext dataContext,
        RenderDelimitedTextNodeConfiguration config)
    {
        string? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                written = value);
        return () => written;
    }
}
