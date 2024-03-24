using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.MeshAdapter.Services;

internal interface ISenderManager
{
    Task LoadAsync(string tenantId);
    Task UpdateAsync(string tenantId, OctoObjectId pipelineRtId);
}