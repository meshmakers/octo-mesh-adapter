using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromHttpRequest
/// </summary>
// AB#4924: Interactive. An HTTP caller is blocked on the response — this is the clearest
// case of work somebody is waiting for, and the scenario §5 of the leasing concept names:
// a "generate billing" click must not queue behind 40 nightly batch entries.
[NodeName("FromHttpRequest", 2)]
[NodeExecutionClass(PipelineExecutionClass.Interactive)]
public record FromHttpRequestNodeConfiguration2 : TriggerNodeConfiguration
{
    /// <summary>
    /// Defines the HTTP action to be performed
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public HttpMethod Method { get; set; }

    /// <summary>
    /// Defines the path to be used
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string Path { get; set; } = null!;

    /// <summary>
    /// Defines whether requests are accepted without a valid access token
    /// </summary>
    [PropertyGroup("Security", 0)]
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Defines the roles that grant access, any one of them is sufficient. An empty list accepts any caller with a valid access token
    /// </summary>
    [PropertyGroup("Security", 1, "roleSelector")]
    public string[] RequiredRoles { get; set; } = [];
}
