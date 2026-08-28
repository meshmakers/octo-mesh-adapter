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
    /// <remarks>
    /// <c>CallsWrappedMethod</c> is what makes the observation non-invasive: configuring a call on
    /// a wrapping fake REPLACES the delegation to the wrapped instance, so without it the write
    /// would be recorded here and never reach the real data context - the helper would be watching
    /// a write it had itself cancelled.
    /// </remarks>
    public static Func<string?> CaptureWrite(IDataContext dataContext,
        RenderDelimitedTextNodeConfiguration config)
    {
        string? written = null;
        A.CallTo(() => dataContext.Set(config.TargetPath, A<string>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                written = value)
            .CallsWrappedMethod();
        return () => written;
    }

    /// <summary>
    /// Asserts that the node wrote nothing at all. Matched by method name rather than by one closed
    /// generic: binding <c>Set&lt;string&gt;</c> with five arguments would let a write through any
    /// other instantiation or the two-argument overload pass unnoticed.
    /// </summary>
    public static void AssertNothingWritten(IDataContext dataContext) =>
        A.CallTo(dataContext).Where(call => call.Method.Name == nameof(IDataContext.Set))
            .MustNotHaveHappened();
}
