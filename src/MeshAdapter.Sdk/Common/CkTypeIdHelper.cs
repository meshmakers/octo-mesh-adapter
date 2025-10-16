using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// Helper class for resolving CkTypeId from configuration or data context
/// </summary>
public static class CkTypeIdHelper
{
    /// <summary>
    /// Resolves CkTypeId from either the direct value or a path in the data context
    /// </summary>
    /// <param name="rtCkTypeId">Direct CkTypeId value (priority)</param>
    /// <param name="ckTypeIdPath">Path to CkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve the path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved CkTypeId or throws exception if not found</returns>
    public static RtCkId<CkTypeId> ResolveRtCkTypeId(RtCkId<CkTypeId>? rtCkTypeId, string? ckTypeIdPath,
        IDataContext dataContext, INodeContext nodeContext)
    {
        if (rtCkTypeId == null && ckTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.CkTypeIdNotSet(nodeContext);
        }
        
        if (rtCkTypeId != null)
        {
            return rtCkTypeId;
        }
        
        if (ckTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(ckTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw MeshAdapterPipelineExecutionException.CkTypeIdValueNull(nodeContext, ckTypeIdPath);
            }
            return new RtCkId<CkTypeId>(ckTypeIdValue);
        }
        
        throw MeshAdapterPipelineExecutionException.CkTypeIdNotSet(nodeContext);
    }

    /// <summary>
    /// Tries to resolve CkTypeId from either the direct value or a path in the data context.
    /// If both are null, returns false and sets resolvedCkTypeId to null.
    /// </summary>
    /// <param name="ckTypeId">Direct CkTypeId value (priority)</param>
    /// <param name="ckTypeIdPath">Path to CkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve the path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <param name="resolvedCkTypeId">Resolved CkTypeId if found, null otherwise</param>
    /// <returns>true if CkTypeId was resolved successfully, false otherwise</returns>
    public static bool TryResolveCkTypeId(CkId<CkTypeId>? ckTypeId, string? ckTypeIdPath,
        IDataContext dataContext, INodeContext nodeContext, out CkId<CkTypeId>? resolvedCkTypeId)
    {
        if (ckTypeId == null && ckTypeIdPath == null)
        {
            resolvedCkTypeId = null;
            return false;
        }

        if (ckTypeId != null)
        {
            resolvedCkTypeId = ckTypeId;
            return true;
        }

        if (ckTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(ckTypeIdPath);
            if (ckTypeIdValue == null)
            {
                resolvedCkTypeId = null;
                return false;
            }
            resolvedCkTypeId = new CkId<CkTypeId>(ckTypeIdValue);
            return true;
        }

        resolvedCkTypeId = null;
        return false;
    }
    
    /// <summary>
    /// Resolves OriginCkTypeId from either the direct value or a path in the data context
    /// </summary>
    /// <param name="originCkTypeId">Direct OriginCkTypeId value (priority)</param>
    /// <param name="originCkTypeIdPath">Path to OriginCkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved OriginCkTypeId or throws exception if not found</returns>
    public static RtCkId<CkTypeId> ResolveOriginCkTypeId(RtCkId<CkTypeId>? originCkTypeId, string? originCkTypeIdPath,
        IDataContext dataContext, INodeContext nodeContext)
    {
        if (originCkTypeId == null && originCkTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.OriginCkTypeIdNotSet(nodeContext);
        }
        
        if (originCkTypeId != null)
        {
            return originCkTypeId;
        }
        
        if (originCkTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(originCkTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw MeshAdapterPipelineExecutionException.OriginCkTypeIdValueNull(nodeContext, originCkTypeIdPath);
            }
            return new RtCkId<CkTypeId>(ckTypeIdValue);
        }
        
        throw MeshAdapterPipelineExecutionException.OriginCkTypeIdNotSet(nodeContext);
    }
    
    /// <summary>
    /// Resolves TargetCkTypeId from either the direct value or a path in the data context
    /// </summary>
    /// <param name="targetCkTypeId">Direct TargetCkTypeId value (priority)</param>
    /// <param name="targetCkTypeIdPath">Path to TargetCkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved TargetCkTypeId or throws exception if not found</returns>
    public static RtCkId<CkTypeId> ResolveTargetCkTypeId(RtCkId<CkTypeId>? targetCkTypeId, string? targetCkTypeIdPath, IDataContext dataContext, INodeContext nodeContext)
    {
        if (targetCkTypeId == null && targetCkTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetCkTypeIdNotSet(nodeContext);
        }
        
        if (targetCkTypeId != null)
        {
            return targetCkTypeId;
        }
        
        if (targetCkTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(targetCkTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw MeshAdapterPipelineExecutionException.TargetCkTypeIdValueNull(nodeContext, targetCkTypeIdPath);
            }
            return new RtCkId<CkTypeId>(ckTypeIdValue);
        }
        
        throw MeshAdapterPipelineExecutionException.TargetCkTypeIdNotSet(nodeContext);
    }
}
