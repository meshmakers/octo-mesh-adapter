using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     The one collection of every test class that reads the pipeline JSON schema. Its classes run one after the other
///     and never in parallel with any other collection, and they share one <see cref="PipelineSchemaFixture" />.
/// </summary>
/// <remarks>
///     Building the schema on two threads at once can spin forever in the process-wide XML documentation cache of
///     Namotion.Reflection, which replaces <c>inheritdoc</c> elements in place without a lock. A class that needs the
///     schema takes <see cref="PipelineSchemaFixture" /> in its constructor instead of building it. xunit supplies this
///     collection fixture only to classes in this collection: elsewhere the parameter fails the build (xUnit1041,
///     warnings are errors) or, with that warning suppressed, every test of the class. A test file that uses
///     <c>IPipelineSchemaGenerator</c>, <c>INodeSchemaRegistry</c> or an SDK type that reaches the schema build on its
///     own (<c>AdapterPoolMemberService</c>, <c>AdapterExecutionService</c>, <c>AdapterNodeDescriptorProjection</c>,
///     <c>AdapterBuilder</c>, <c>WebAdapterBuilder</c>) fails <see cref="PipelineSchemaFixtureArchitectureTests" />.
///     Other test files may name <c>PipelineSchemaFixture</c> only in a declaration without an initializer, such as a
///     constructor parameter or a field; every other mention (new, a class or collection fixture, an alias) fails that
///     test as well. The comment at the top of that test lists the forms it refuses on the safe side and the ones it
///     does not cover.
///     <see cref="PipelineSchemaCollectionTests" /> checks that the collection definition in effect runs without
///     parallelism.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PipelineSchemaCollection : ICollectionFixture<PipelineSchemaFixture>
{
    public const string Name = "Pipeline schema";
}

/// <summary>
///     The pipeline JSON schema, generated once per test run from the registrations of <c>AddDataPipelineSerializer</c>
///     and <c>AddMeshDataPipelineNodes</c>.
/// </summary>
public sealed class PipelineSchemaFixture
{
    private readonly JsonArray _nodeSchemas;

    public PipelineSchemaFixture()
    {
        var services = new ServiceCollection();
        services.AddDataPipelineSerializer().AddMeshDataPipelineNodes();
        var json = services.BuildServiceProvider().GetRequiredService<IPipelineSchemaGenerator>().GenerateSchema();

        _nodeSchemas = JsonNode.Parse(json)!["$defs"]!["TransformationNode"]!["oneOf"]!.AsArray();
    }

    /// <summary>
    ///     A copy of the schema of the node <paramref name="type" /> (for example <c>SftpDelete@1</c>); exactly one
    ///     must exist. A copy, so that no test can change what the next one reads.
    /// </summary>
    public JsonObject NodeSchema(string type) => NodeSchemasOf(type).Single().DeepClone().AsObject();

    /// <summary>
    ///     A copy of the schema of the node <paramref name="type" />, or <c>null</c> when the schema lists none.
    /// </summary>
    public JsonObject? NodeSchemaOrNull(string type) =>
        NodeSchemasOf(type).SingleOrDefault()?.DeepClone().AsObject();

    private IEnumerable<JsonObject> NodeSchemasOf(string type) =>
        _nodeSchemas.Select(node => node!.AsObject())
            .Where(node => node["properties"]?["type"]?["const"]?.GetValue<string>() == type);
}
