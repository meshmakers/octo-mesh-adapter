using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration of <c>RevealSecret@1</c> (AB#5538): reads one <c>Secret</c> attribute of one runtime
/// entity, decrypts it in process and writes the plaintext to <see cref="TargetPathNodeConfiguration.TargetPath" />.
/// </summary>
/// <remarks>
/// <para>
/// The only way for a pipeline to obtain the plaintext of a Secret attribute (concept AB#5528,
/// decision 6): every other node — <c>GetRtEntitiesById@1</c>, <c>GetRtEntitiesByType@1</c>, queries,
/// change streams — sees the marker <c>{"isSet": true|false}</c>. The decrypt is counted
/// (<c>octo.secrets.decrypt{tenant,ckType,attribute,service}</c>), the value is never logged, and the
/// written plaintext is registered as secret for the execution, so debug snapshots, the execution log
/// and <c>SetPipelineExecutionResult@1</c> show <c>***</c> instead.
/// </para>
/// <para>
/// A Secret that is not set (absent, <c>null</c> or a placeholder) writes <c>null</c> to the target.
/// An unknown entity, an attribute that is not a Secret, missing key material or an unknown key id
/// fail the node; no message carries the value.
/// </para>
/// </remarks>
[NodeName("RevealSecret", 1)]
public record RevealSecretNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Identity the entity is read as: <c>Caller</c> (default) or <c>ServiceAccount</c> (AB#5127). The
    /// identity must be allowed to read the entity — data permissions apply to the read, decryption
    /// itself needs no extra permission. <c>System</c> is refused: it bypasses data permissions, so it
    /// would let anyone who may edit a pipeline reveal every credential of the tenant.
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    /// <summary>
    /// Runtime CK type of the entity (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// JSONPath to the runtime CK type id of the entity (alternative to CkTypeId)
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// Runtime id of the entity (use either RtId or RtIdPath)
    /// </summary>
    [PropertyGroup("Entity", 2)]
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// JSONPath to the runtime id of the entity (alternative to RtId; RtId wins when both are set)
    /// </summary>
    [PropertyGroup("Entity", 3, "jsonpath")]
    public string? RtIdPath { get; set; }

    /// <summary>
    /// Name of the Secret attribute, e.g. <c>Password</c> or <c>password</c> (case-insensitive). A
    /// dotted path reaches a Secret inside single <c>Record</c> attributes (<c>Settings.ApiKey</c>);
    /// record arrays are not supported.
    /// </summary>
    [PropertyGroup("Secret", 0)]
    public required string AttributeName { get; set; }
}
