using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Materializes the execution's verified caller into the data context (AB#5136). See
/// <see cref="Meshmakers.Octo.MeshAdapter.Nodes.Extract.WriteVerifiedCallerNodeConfiguration" />.
/// </summary>
[NodeConfiguration(typeof(WriteVerifiedCallerNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class WriteVerifiedCallerNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<WriteVerifiedCallerNodeConfiguration>();

        if (etlContext.VerifiedPrincipal is { } caller)
        {
            var obj = new JsonObject
            {
                ["subjectId"] = caller.SubjectId,
                ["tenantId"] = caller.TenantId,
                ["name"] = caller.Name,
                ["email"] = caller.Email,
                ["roles"] = new JsonArray(caller.Roles.Select(r => (JsonNode)r!).ToArray())
            };

            dataContext.Set(c.TargetPath, obj, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
            nodeContext.Info($"WriteVerifiedCaller: wrote verified caller '{caller.SubjectId}' to {c.TargetPath}");
        }
        else
        {
            nodeContext.Info("WriteVerifiedCaller: no verified caller on this execution — nothing written");
        }

        await next(dataContext, nodeContext);
    }
}
