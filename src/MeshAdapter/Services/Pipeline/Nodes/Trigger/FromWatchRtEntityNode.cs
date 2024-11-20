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

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;

[NodeConfiguration(typeof(FromWatchRtEntityNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class FromWatchRtEntityNode(ISystemContext systemContext) : ITriggerPipelineNode
{
    private IUpdateStream<RtEntity>? _updateStream;

    public async Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromWatchRtEntityNodeConfiguration>();

        WatchStreamFilter filter = new WatchStreamFilter
        {
            UpdateTypes = (UpdateTypes)c.UpdateTypes,
            RtId = c.RtId,
            BeforeFieldFilters = c.BeforeFieldFilters?.Select(f =>
                new FieldFilter(f.AttributeName.ToPascalCase(), (FieldFilterOperator)f.Operator, f.ComparisonValue)).ToList(),
            FieldFilters = c.FieldFilters?.Select(f =>
                new FieldFilter(f.AttributeName.ToPascalCase(), (FieldFilterOperator)f.Operator, f.ComparisonValue)).ToList(),
        };

        var tenantRepository = await systemContext.FindTenantRepositoryAsync(context.TenantId);

        _updateStream = await tenantRepository.WatchRtEntitiesAsync(c.CkTypeId, filter);
        _updateStream.GetUpdates()
            .Select(u =>
                Observable.FromAsync(() => context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), u)))
            .Concat()
            .Subscribe();
    }

    public Task StopAsync(ITriggerContext context)
    {
        _updateStream?.Dispose();

        return Task.CompletedTask;
    }
}