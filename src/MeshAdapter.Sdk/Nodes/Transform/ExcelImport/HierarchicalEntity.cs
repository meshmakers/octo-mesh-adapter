using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal record HierarchicalEntity
{
    public HierarchicalEntity(CkId<CkTypeId> ckTypeId, string name, string? parentName, CkId<CkTypeId>? parentCkTypeId)
    {
        CkTypeId = ckTypeId;
        Name = name;
        ParentName = parentName;
        ParentCkTypeId = parentCkTypeId;
    }

    public HierarchicalEntity(OctoObjectId rtId, CkId<CkTypeId> ckTypeId, string parentName, CkId<CkTypeId> parentCkTypeId)
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
    public CkId<CkTypeId> CkTypeId { get; init; }
    public string? Name { get; init; }
    public string? ParentName { get; init; }
    public CkId<CkTypeId>? ParentCkTypeId { get; init; }

    public CkId<CkAssociationRoleId> AssociationRoleId { get; init; } = new("System/ParentChild");
}