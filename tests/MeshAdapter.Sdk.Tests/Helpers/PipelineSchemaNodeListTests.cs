namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     A pipeline schema without the list of node schemas fails every test of <see cref="PipelineSchemaCollection" />,
///     so the message must name the path, whichever part of it is missing or of another type, instead of surfacing
///     as a null reference or as the generic message of System.Text.Json.
/// </summary>
public class PipelineSchemaNodeListTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"$defs\": []}")]
    [InlineData("{\"$defs\": {}}")]
    [InlineData("{\"$defs\": {\"TransformationNode\": []}}")]
    [InlineData("{\"$defs\": {\"TransformationNode\": {}}}")]
    [InlineData("{\"$defs\": {\"TransformationNode\": {\"oneOf\": {}}}}")]
    public void From_WithoutTheNodeSchemaArray_NamesTheMissingPath(string schemaJson)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PipelineSchemaNodeList.From(schemaJson));

        Assert.Equal("The pipeline schema has no $defs/TransformationNode/oneOf array.", exception.Message);
    }

    [Fact]
    public void From_ReturnsTheArrayAtThePath()
    {
        var nodeSchemas = PipelineSchemaNodeList.From(
            "{\"$defs\": {\"TransformationNode\": {\"oneOf\": [{\"const\": \"first\"}, {\"const\": \"second\"}]}}}");

        Assert.Equal(new[] { "first", "second" }, nodeSchemas.Select(node => node!["const"]!.GetValue<string>()));
    }
}
