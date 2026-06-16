namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Static catalog of dry-run-honouring Load nodes shipped by MeshAdapter.Sdk
/// (M4-B.2). Read by the executor at end-of-run to populate the MCP tool's
/// <c>LoadNodesNotHonouringDryRun</c> report: any Load node type that appears
/// in the execution's debug stream but is NOT in this set ran for real and
/// the operator should treat its side effects accordingly.
///
/// Adapter-specific Load nodes (Modbus, IEC, OPC-UA, etc.) opt in by adding
/// their NodeName+Version string to a similar registry in their own repo —
/// this set covers only the nodes shipped by the SDK itself.
/// </summary>
public static class DryRunHonouredLoadNodes
{
    /// <summary>NodeName@Version key for <see cref="ApplyChangesNode"/>.</summary>
    public const string ApplyChanges = "ApplyChanges@1";

    /// <summary>NodeName@Version key for <see cref="ApplyChangesNode2"/>.</summary>
    public const string ApplyChanges2 = "ApplyChanges@2";

    /// <summary>NodeName@Version key for <see cref="DeployPipelineNode"/>.</summary>
    public const string DeployPipeline = "DeployPipeline@1";

    /// <summary>NodeName@Version key for <see cref="EMailSenderNode"/>.</summary>
    public const string SendEMail = "SendEMail@1";

    /// <summary>NodeName@Version key for <see cref="GrafanaProvisionTenantNode"/>.</summary>
    public const string GrafanaProvisionTenant = "GrafanaProvisionTenant@1";

    /// <summary>NodeName@Version key for <see cref="GrafanaDeprovisionTenantNode"/>.</summary>
    public const string GrafanaDeprovisionTenant = "GrafanaDeprovisionTenant@1";

    /// <summary>NodeName@Version key for <see cref="SaveStreamDataInArchive"/>.</summary>
    public const string SaveStreamDataInArchive = "SaveStreamDataInArchive@1";

    /// <summary>NodeName@Version key for <see cref="SaveTimeRangeStreamDataInArchive"/>.</summary>
    public const string SaveTimeRangeStreamDataInArchive = "SaveTimeRangeStreamDataInArchive@1";

    /// <summary>NodeName@Version key for <see cref="SftpUploadNode"/>.</summary>
    public const string SftpUpload = "SftpUpload@1";

    /// <summary>NodeName@Version key for <see cref="ToDiscordNode"/>.</summary>
    public const string ToDiscord = "ToDiscord@1";

    // UpdateRtEntityIfNewer is intentionally NOT listed: it only reads from MongoDB to
    // diff candidates and writes its filtered output to the in-memory data context for
    // downstream Load nodes (typically ApplyChanges@1/@2) to actually persist. It has no
    // real-world side effect of its own; the downstream sink is what dry-run suppresses.

    /// <summary>
    /// All SDK-shipped Load node types that honour <c>IPipelineExecutionMode.IsDryRun</c>.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>
    {
        ApplyChanges,
        ApplyChanges2,
        DeployPipeline,
        SendEMail,
        GrafanaProvisionTenant,
        GrafanaDeprovisionTenant,
        SaveStreamDataInArchive,
        SaveTimeRangeStreamDataInArchive,
        SftpUpload,
        ToDiscord
    };
}
