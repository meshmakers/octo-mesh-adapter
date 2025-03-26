using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal record HierarchicalEntity(CkId<CkTypeId> CkTypeId, string Name, string? ParentName, CkId<CkTypeId>? ParentCkTypeId)
{
    public OctoObjectId? RtId { get; set; }
    public List<Tuple<string, string>> Attributes { get; set; } = [];
}