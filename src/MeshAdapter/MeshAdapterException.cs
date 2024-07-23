using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.MeshAdapter;

public class MeshAdapterException : Exception
{
    private MeshAdapterException()
    {
    }

    private MeshAdapterException(string message) : base(message)
    {
    }

    private MeshAdapterException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception PipelineConfigurationNotFound(string tenantId, OctoObjectId pipelineRtId)
    {
        return new MeshAdapterException($"Pipeline configuration not found for tenant '{tenantId}' and pipeline '{pipelineRtId}'");
    }
    
    public static Exception StreamDataConfigurationNotFound()
    {
        return new MeshAdapterException("StreamData configuration not found");
    }
}
