using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

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
    /// <returns>Resolved CkTypeId or null if not found</returns>
    public static CkId<CkTypeId>? ResolveCkTypeId(CkId<CkTypeId>? ckTypeId, string? ckTypeIdPath, IDataContext dataContext)
    {
        if (ckTypeId != null)
        {
            return ckTypeId;
        }
        
        if (ckTypeIdPath != null)
        {
            var ckTypeIdValue = dataContext.GetSimpleValueByPath<string>(ckTypeIdPath);
            if (ckTypeIdValue == null)
            {
                throw new InvalidOperationException($"No CkTypeId found at path '{ckTypeIdPath}'");
            }
            return new CkId<CkTypeId>(ckTypeIdValue);
        }
        
        return null;
    }
}
