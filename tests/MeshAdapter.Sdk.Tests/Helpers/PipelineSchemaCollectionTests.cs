using Xunit.v3;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     Runs inside <see cref="PipelineSchemaCollection" />. It reads the collection definition xunit applies at run
///     time, because a second definition with the same name can take its place and the attribute on
///     <see cref="PipelineSchemaCollection" /> alone does not show that the collection runs without parallelism. It
///     also checks that <see cref="PipelineSchemaFixture.NodeSchema" /> and
///     <see cref="PipelineSchemaFixture.NodeSchemaOrNull" /> each hand out a copy.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class PipelineSchemaCollectionTests(PipelineSchemaFixture schema)
{
    [Fact]
    public void TheCollectionInEffect_RunsWithoutParallelism()
    {
        var collection = Assert.IsAssignableFrom<IXunitTestCollection>(TestContext.Current.TestCollection);

        Assert.True(collection.DisableParallelization,
            "The collection definition in effect for PipelineSchemaCollection.Name runs in parallel; " +
            "look for a second [CollectionDefinition] with the same name.");
    }

    [Fact]
    public void NodeSchema_ReturnsACopy_SoAChangeIsNotSeenByTheNextRead()
    {
        var first = schema.NodeSchema("SftpDelete@1");
        first["properties"]!.AsObject().Add("mutatedByATest", true);

        Assert.False(schema.NodeSchema("SftpDelete@1")["properties"]!.AsObject().ContainsKey("mutatedByATest"));
    }

    [Fact]
    public void NodeSchemaOrNull_ReturnsACopy_SoAChangeIsNotSeenByTheNextRead()
    {
        var first = schema.NodeSchemaOrNull("SftpDelete@1")!;
        first["properties"]!.AsObject().Add("mutatedByATest", true);

        Assert.False(schema.NodeSchemaOrNull("SftpDelete@1")!["properties"]!.AsObject().ContainsKey("mutatedByATest"));
    }
}
