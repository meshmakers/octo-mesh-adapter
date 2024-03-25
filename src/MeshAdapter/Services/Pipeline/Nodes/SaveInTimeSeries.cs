using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

internal class SaveInTimeSeriesNodeConfiguration : NodeConfiguration;

[Node("SaveInTimeSeries", 1, typeof(SaveInTimeSeriesNodeConfiguration))]
internal class SaveInTimeSeriesNode(NodeDelegate next)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        await next(dataContext);
    }
}