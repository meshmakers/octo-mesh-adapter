using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that checks if an entity with a matching attribute value already exists.
/// Writes true/false to targetPath and optionally the existing entity to existingEntityPath.
/// </summary>
[NodeConfiguration(typeof(CheckDuplicateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CheckDuplicateNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<CheckDuplicateNodeConfiguration>();

        var value = dataContext.Current?.SelectToken(config.ValuePath)?.ToString();
        if (string.IsNullOrEmpty(value))
        {
            nodeContext.Debug("No value found for duplicate check, skipping");
            dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, false);
            await next(dataContext, nodeContext);
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(config.AttributeName, FieldFilterOperator.Equals, value);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, config.CkTypeId, queryOptions, 0, 1);
        await session.CommitTransactionAsync();

        var isDuplicate = result.TotalCount > 0;

        dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode, isDuplicate);

        if (isDuplicate && !string.IsNullOrEmpty(config.ExistingEntityPath))
        {
            var existingEntity = result.Items.FirstOrDefault();
            if (existingEntity != null)
            {
                dataContext.SetValueByPath(config.ExistingEntityPath, DocumentModes.Extend,
                    ValueKinds.Simple, TargetValueWriteModes.Overwrite, existingEntity);
            }
        }

        nodeContext.Info(isDuplicate
            ? $"Duplicate found: entity with {config.AttributeName}='{value}' already exists"
            : $"No duplicate found for {config.AttributeName}='{value}'");

        await next(dataContext, nodeContext);
    }
}
