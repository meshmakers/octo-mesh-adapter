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

    public static Exception InvalidValue(INodeContext nodeContext, JToken jToken)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Invalid value: {jToken}");
    }

    public static Exception TargetCkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: targetCkTypeId and targetCkTypeIdPath is not set.");
    }

    public static Exception TargetCkTypeIdValueNull(INodeContext nodeContext, string? path = null)
    {
        var pathInfo = path != null ? $" at path '{path}'" : "";
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of target CkTypeId is null{pathInfo}.");
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

    public static Exception AssociationRoleIdPathNotSet(INodeContext nodeContext)
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

    public static Exception GraphDirectionNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Graph direction is not set. Please set graphDirection or graphDirectionPath.");
    }

    public static Exception OriginCkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Origin CkTypeId is not set. Please set originCkTypeId or originCkTypeIdPath.");
    }

    public static Exception OriginCkTypeIdValueNull(INodeContext nodeContext, string? path = null)
    {
        var pathInfo = path != null ? $" at path '{path}'" : "";
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of origin CkTypeId is null{pathInfo}.");
    }

    public static Exception OriginRtIdsNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Origin RtIds are not set. Please set originRtId or originRtIdPath.");
    }
    
    public static Exception OriginRtIdNotFound(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: originRtId and originRtIdPath is not set.");
    }
    
    public static Exception OriginRtIdValueNull(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: Value of origin RtId is null.");
    }

    public static Exception ValueNotSet(INodeContext nodeContext, string? cValuePath)
    {
        return new MeshAdapterPipelineExecutionException(
            $"[{nodeContext.NodePath}]: Value is not set. Please set value or valuePath. ValuePath: {cValuePath}");
    }
    
    public static Exception CkTypeIdNotSet(INodeContext nodeContext)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: CkTypeId is not set. Please set ckTypeId or ckTypeIdPath.");
    }
    
    public static Exception CkTypeIdValueNull(INodeContext nodeContext, string path)
    {
        return new MeshAdapterPipelineExecutionException($"[{nodeContext.NodePath}]: No CkTypeId found at path '{path}'.");
    }
}
