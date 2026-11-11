using MeshAdapter.Sdk.Tests.Helpers;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     AB#8703: <c>executionIdArgument</c> of <c>McpToolCall@1</c> is in the pipeline JSON schema (generated from the
///     real node registrations, like the build target) as one new optional string property.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class McpToolCallPipelineSchemaTests(PipelineSchemaFixture schema)
{
    [Fact]
    public void ExecutionIdArgument_IsAnOptionalStringProperty()
    {
        var node = schema.NodeSchema("McpToolCall@1");
        var properties = node["properties"]!.AsObject().Select(p => p.Key).ToHashSet();

        Assert.Contains("executionIdArgument", properties);
        Assert.Contains("string", node["properties"]!["executionIdArgument"]!["type"]!.ToJsonString());
        foreach (var existing in new[] { "allowedTools", "toolName", "argumentsPath", "mcpServiceAccountConfigName" })
        {
            Assert.Contains(existing, properties);
        }

        var required = node["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToArray() ?? [];
        Assert.DoesNotContain("executionIdArgument", required);
    }
}
