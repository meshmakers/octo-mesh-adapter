using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromHttpRequest
/// </summary>
[NodeName("FromHttpRequest", 2)]
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
    /// Defines the roles that grant access, any one of them is sufficient. Without a role the caller only needs a valid access token
    /// </summary>
    [PropertyGroup("Security", 1, "roleSelector")]
    public string[] RequiredRoles { get; set; } = [];
}
