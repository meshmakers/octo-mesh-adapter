using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal record HierarchicalEntity
{
    public HierarchicalEntity(RtCkId<CkTypeId> ckTypeId, string name, string? parentName, RtCkId<CkTypeId>? parentCkTypeId)
    {
        CkTypeId = ckTypeId;
        Name = name;
        ParentName = parentName;
        ParentCkTypeId = parentCkTypeId;
    }

    public HierarchicalEntity(OctoObjectId rtId, RtCkId<CkTypeId> ckTypeId, string parentName, RtCkId<CkTypeId> parentCkTypeId)
    {
        IsObjectInRepository = true;
        RtId = rtId;
        CkTypeId = ckTypeId;
        ParentName = parentName;
        ParentCkTypeId = parentCkTypeId;
        AssociationRoleId = new("Basic/RelatedClassification");
    }

    public bool IsObjectInRepository { get; init; }
    public OctoObjectId? RtId { get; set; }
    public List<Tuple<string, string>> Attributes { get; set; } = [];
    public RtCkId<CkTypeId> CkTypeId { get; init; }
    public string? Name { get; init; }
    public string? ParentName { get; init; }
    public RtCkId<CkTypeId>? ParentCkTypeId { get; init; }

    public RtCkId<CkAssociationRoleId> AssociationRoleId { get; init; } = new("System/ParentChild");
}