using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter;

internal class MeshAdapterPipelineExecutionException : PipelineExecutionException
{
    private MeshAdapterPipelineExecutionException()
    {
    }

    private MeshAdapterPipelineExecutionException(string message) : base(message)
    {
    }

    private MeshAdapterPipelineExecutionException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception InputValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Input value is null.");
    }

    public static Exception InvalidValue(JToken jToken)
    {
        return new MeshAdapterPipelineExecutionException($"Invalid value: {jToken}");
    }

    public static Exception SourceCkTypeIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: sourceCkTypeId and sourceCkTypeIdPath is not set.");
    }

    public static Exception SourceCkTypeIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of source CkTypeId is null.");
    }

    public static Exception SourceRtIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: sourceRtId and sourceRtIdPath is not set.");
    }

    public static Exception SourceRtIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of source RtId is null.");
    }

    public static Exception TargetCkTypeIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: targetCkTypeId and targetCkTypeIdPath is not set.");
    }

    public static Exception TargetCkTypeIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of target CkTypeId is null.");
    }

    public static Exception TargetRtIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: targetRtId and targetRtIdPath is not set.");
    }

    public static Exception TargetRtIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of target RtId is null.");
    }

    public static Exception UpdateKindPathNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: updateKind or updateKindPath is not set.");
    }

    public static Exception AssociationRoleIdPathNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: associationRoleId or associationRoleIdPath is not set.");
    }

    public static Exception UpdateKindNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of update kind is null.");
    }

    public static Exception AssociationRoleIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of association role id is null.");
    }
}
