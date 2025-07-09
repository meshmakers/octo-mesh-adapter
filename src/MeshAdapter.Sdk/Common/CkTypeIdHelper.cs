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
    /// <param name="ckTypeId">Direct CkTypeId value (priority)</param>
    /// <param name="ckTypeIdPath">Path to CkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved CkTypeId or throws exception if not found</returns>
    public static CkId<CkTypeId> ResolveCkTypeId(CkId<CkTypeId>? ckTypeId, string? ckTypeIdPath, IDataContext dataContext, INodeContext nodeContext)
    {
        if (ckTypeId == null && ckTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.CkTypeIdNotSet(nodeContext);
        }
        
        if (ckTypeId != null)
        {
            return ckTypeId;
        }
        
        if (ckTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(ckTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw MeshAdapterPipelineExecutionException.CkTypeIdValueNull(nodeContext, ckTypeIdPath);
            }
            return new CkId<CkTypeId>(ckTypeIdValue);
        }
        
        throw MeshAdapterPipelineExecutionException.CkTypeIdNotSet(nodeContext);
    }
    
    /// <summary>
    /// Resolves OriginCkTypeId from either the direct value or a path in the data context
    /// </summary>
    /// <param name="originCkTypeId">Direct OriginCkTypeId value (priority)</param>
    /// <param name="originCkTypeIdPath">Path to OriginCkTypeId in data context</param>
    /// <param name="dataContext">Data context to resolve path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved OriginCkTypeId or throws exception if not found</returns>
    public static CkId<CkTypeId> ResolveOriginCkTypeId(CkId<CkTypeId>? originCkTypeId, string? originCkTypeIdPath, IDataContext dataContext, INodeContext nodeContext)
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
            return new CkId<CkTypeId>(ckTypeIdValue);
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
    public static CkId<CkTypeId> ResolveTargetCkTypeId(CkId<CkTypeId>? targetCkTypeId, string? targetCkTypeIdPath, IDataContext dataContext, INodeContext nodeContext)
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
            return new CkId<CkTypeId>(ckTypeIdValue);
        }
        
        throw MeshAdapterPipelineExecutionException.TargetCkTypeIdNotSet(nodeContext);
    }
}
