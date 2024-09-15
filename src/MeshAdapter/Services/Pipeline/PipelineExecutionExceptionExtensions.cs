using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

internal static class PipelineExecutionExceptionExtensions
{
    public static PipelineExecutionException SessionNotFound(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId)
    {
        return new PipelineExecutionException($"[{tenantId}] Session for '{pipelineRtEntityId}' and '{pipelineExecutionId}' not found");
    }
}