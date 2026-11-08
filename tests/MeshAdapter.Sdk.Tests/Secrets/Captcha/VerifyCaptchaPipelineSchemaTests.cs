using System.Text.Json.Nodes;
using MeshAdapter.Sdk.Tests.Helpers;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

/// <summary>
///     The pipeline JSON schema of <c>VerifyCaptcha@1</c>, generated from the real node registrations like the build
///     target does: listed, no endpoint property, <c>provider</c> required and <c>timeoutSeconds</c> limited to 1-60.
/// </summary>
[Collection(PipelineSchemaCollection.Name)]
public class VerifyCaptchaPipelineSchemaTests(PipelineSchemaFixture schema)
{
    private JsonObject? Generated => schema.NodeSchemaOrNull("VerifyCaptcha@1");

    private JsonObject Node => Generated ?? throw new InvalidOperationException("VerifyCaptcha@1 missing");

    private JsonObject Properties => Node["properties"]!.AsObject();

    [Fact]
    public void TheNode_IsListedInTheSchema()
    {
        Assert.NotNull(Generated);
        Assert.Contains("tokenPath", Properties.Select(property => property.Key));
    }

    [Fact]
    public void TheSchema_HasNoEndpointProperty()
    {
        var forbidden = new[] { "url", "uri", "endpoint", "host", "server", "address", "baseurl" };
        var names = Properties.Select(property => property.Key.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, name => forbidden.Any(name.Contains));
    }

    [Fact]
    public void Provider_IsRequired()
    {
        var required = Node["required"]!.AsArray().Select(requiredName => requiredName!.GetValue<string>()).ToArray();

        Assert.Contains("provider", required);
    }

    [Fact]
    public void TimeoutSeconds_IsLimitedToOneToSixty()
    {
        var timeout = Properties["timeoutSeconds"]!.AsObject();

        Assert.Equal(1, timeout["minimum"]!.GetValue<double>());
        Assert.Equal(60, timeout["maximum"]!.GetValue<double>());
    }
}
