using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

/// <summary>
///     The pipeline JSON schema of <c>VerifyCaptcha@1</c>, generated from the real node registrations like the build
///     target does: listed, no endpoint property, <c>provider</c> required and <c>timeoutSeconds</c> limited to 1-60.
/// </summary>
public class VerifyCaptchaPipelineSchemaTests
{
    private static readonly Lazy<JsonObject?> Generated = new(Generate);

    private static JsonObject? Generate()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var json = services.BuildServiceProvider().GetRequiredService<IPipelineSchemaGenerator>().GenerateSchema();

        return JsonNode.Parse(json)!["$defs"]!["TransformationNode"]!["oneOf"]!.AsArray()
            .Select(node => node!.AsObject())
            .SingleOrDefault(node => node["properties"]?["type"]?["const"]?.GetValue<string>() == "VerifyCaptcha@1");
    }

    private static JsonObject Node => Generated.Value ?? throw new InvalidOperationException("VerifyCaptcha@1 missing");

    private static JsonObject Properties => Node["properties"]!.AsObject();

    [Fact]
    public void TheNode_IsListedInTheSchema()
    {
        Assert.NotNull(Generated.Value);
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
