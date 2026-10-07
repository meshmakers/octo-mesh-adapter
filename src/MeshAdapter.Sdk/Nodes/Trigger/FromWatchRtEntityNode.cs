using System.Reactive.Linq;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Pipeline node that triggers when a real-time entity is updated
/// </summary>
/// <param name="systemContext"></param>
/// <param name="ckCacheService">CK cache, to recognise Secret attributes in change-stream documents</param>
[NodeConfiguration(typeof(FromWatchRtEntityNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class FromWatchRtEntityNode(ISystemContext systemContext, ICkCacheService ckCacheService) : ITriggerPipelineNode
{
    private IUpdateStream<RtEntity>? _updateStream;

    /// <inheritdoc />
    public async Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromWatchRtEntityNodeConfiguration>();

        WatchStreamFilter filter = new WatchStreamFilter
        {
            UpdateTypes = (UpdateTypes)c.UpdateTypes,
            RtId = c.RtId,
            BeforeFieldFilterCriteria = c.BeforeFieldFilters != null
                ? FieldFilterCriteria.Create().Fields(c.BeforeFieldFilters.Select(f =>
                    new FieldFilter(f.AttributePath, f.Operator.ToFieldFilterOperator(), f.ComparisonValue)).ToList())
                : null,
            FieldFilterCriteria = c.FieldFilters != null
                ? FieldFilterCriteria.Create().Fields(c.FieldFilters.Select(f =>
                    new FieldFilter(f.AttributePath, f.Operator.ToFieldFilterOperator(), f.ComparisonValue)).ToList())
                : null,
        };

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(context.TenantId);

        _updateStream = await tenantRepository.WatchRtEntitiesAsync(c.CkTypeId, filter);
        _updateStream.GetUpdates()
            .Select(u => MaskSecrets(context.TenantId, u))
            .Select(u =>
                Observable.FromAsync(() => context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), u))
                    .Catch<object?, Exception>(ex =>
                    {
                        context.NodeContext.Error(ex, "Pipeline execution failed for watched entity update");
                        return Observable.Empty<object?>();
                    }))
            .Concat()
            .Subscribe();
    }

    /// <summary>
    /// AB#5538: change streams are not normalised by the repository, so a string stored in a Secret
    /// slot (clear text or <c>enc:v1</c> from before the attribute became Secret) arrives as a plain
    /// string and would be serialised into the data context verbatim. Decided by the CK attribute
    /// type, the slot is wrapped so it serialises as the <c>{"isSet": …}</c> marker. Protected values
    /// already arrive as <c>RtSecretValue</c>.
    /// </summary>
    internal IUpdateInfo<RtEntity> MaskSecrets(string tenantId, IUpdateInfo<RtEntity> update)
    {
        SecretAttributes.MaskLegacyValues(ckCacheService, tenantId, update.Document);
        SecretAttributes.MaskLegacyValues(ckCacheService, tenantId, update.DocumentBeforeChange);
        return update;
    }

    /// <inheritdoc />
    public Task StopAsync(ITriggerContext context)
    {
        _updateStream?.Dispose();

        return Task.CompletedTask;
    }
}