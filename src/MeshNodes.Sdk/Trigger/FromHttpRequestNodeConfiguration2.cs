using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromHttpRequest
/// </summary>
/// <remarks>
/// The response of the route is the final data root of the pipeline minus the request envelope
/// (AB#5674): the keys <c>path</c>, <c>method</c>, <c>principal</c>, <c>headers</c>, <c>body</c>,
/// <c>bodyEncoding</c>, <c>query</c>, <c>files</c>, <c>formData</c>, <c>contentType</c> and
/// <c>contentLength</c> are removed from the top level, and a whole copy of the request headers or of
/// the principal anywhere in the response is removed as well. A single field mapped on purpose, such as
/// <c>$.principal.email</c> into <c>$.createdBy</c>, stays. Responses of other shapes and an empty
/// response are returned unchanged. The behaviour cannot be switched off; use the pipeline debugger to
/// inspect the data of a run. Shaping the result with <c>Project@1</c> and <c>clear: true</c> stays
/// advisable, because intermediate results are part of the data root too.
/// </remarks>
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
