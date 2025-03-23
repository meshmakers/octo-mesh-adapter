using System.Reactive.Linq;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Pipeline node that triggers when a real-time entity is updated
/// </summary>
/// <param name="systemContext"></param>
[NodeConfiguration(typeof(FromWatchRtEntityNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class FromWatchRtEntityNode(ISystemContext systemContext) : ITriggerPipelineNode
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
            BeforeFieldFilters = c.BeforeFieldFilters?.Select(f =>
                new FieldFilter(f.AttributePath.ToPascalCase(), (FieldFilterOperator)f.Operator, f.ComparisonValue)).ToList(),
            FieldFilters = c.FieldFilters?.Select(f =>
                new FieldFilter(f.AttributePath.ToPascalCase(), (FieldFilterOperator)f.Operator, f.ComparisonValue)).ToList(),
        };

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(context.TenantId);

        _updateStream = await tenantRepository.WatchRtEntitiesAsync(c.CkTypeId, filter);
        _updateStream.GetUpdates()
            .Select(u =>
                Observable.FromAsync(() => context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), u)))
            .Concat()
            .Subscribe();
    }

    /// <inheritdoc />
    public Task StopAsync(ITriggerContext context)
    {
        _updateStream?.Dispose();

        return Task.CompletedTask;
    }
}