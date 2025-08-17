using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// Helper class for resolving RtId from configuration or data context
/// </summary>
public static class RtIdHelper
{
    /// <summary>
    /// Resolves the RtId from either the direct value or a path in the data context.
    /// </summary>
    /// <param name="rtId">RtId value (priority)</param>
    /// <param name="rtIdPath">RtId path in data context</param>
    /// <param name="generateNewRtId">Whether to generate a new RtId if not found</param>
    /// <param name="dataContext">Data context to resolve a path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <returns>Resolved RtId</returns>
    private static OctoObjectId GetRtId(OctoObjectId? rtId, string? rtIdPath, bool generateNewRtId, IDataContext dataContext, INodeContext nodeContext)
    {
        if (rtId == null && rtIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.RtIdNotSet(nodeContext);
        }

        if (rtId != null)
        {
            return rtId.Value;
        }

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.DataContextIsNull(nodeContext);
        }

        rtId = dataContext.GetComplexObjectByPath<OctoObjectId?>(rtIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (rtId == null && generateNewRtId)
        {
            rtId = OctoObjectId.GenerateNewId();
        }

        if (rtId == null)
        {
            throw MeshAdapterPipelineExecutionException.RtIdValueNull(nodeContext, rtIdPath);
        }

        return rtId.Value;
    }

    /// <summary>
    /// Tries to resolve the RtId from either the direct value or a path in the data context.
    /// If both are null, returns false and sets resolvedRtId to null.
    /// </summary>
    /// <param name="rtId">RtId value (priority)</param>
    /// <param name="rtIdPath">RtId path in data context</param>
    /// <param name="dataContext">Data context to resolve a path from</param>
    /// <param name="nodeContext">Node context for exception handling</param>
    /// <param name="resolvedRtId">Resolved RtId, or null if not found</param>
    /// <returns>True if RtId was resolved, false if both rtId and rtIdPath are null</returns>
    /// <exception cref="MeshAdapterPipelineExecutionException">When data context is null</exception>
    public static bool TryResolveRtId(OctoObjectId? rtId, string? rtIdPath, IDataContext dataContext, INodeContext nodeContext, out OctoObjectId? resolvedRtId)
    {
        if (rtId == null && rtIdPath == null)
        {
            resolvedRtId = null;
            return false;
        }

        if (rtId != null)
        {
            resolvedRtId = rtId.Value;
            return true;
        }

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.DataContextIsNull(nodeContext);
        }

        rtId = dataContext.GetComplexObjectByPath<OctoObjectId?>(rtIdPath,
            RtNewtonsoftSerializer.DefaultSerializer);


        if (rtId == null)
        {
            resolvedRtId = null;
            return false;
        }

        resolvedRtId = rtId.Value;
        return true;
    }
}