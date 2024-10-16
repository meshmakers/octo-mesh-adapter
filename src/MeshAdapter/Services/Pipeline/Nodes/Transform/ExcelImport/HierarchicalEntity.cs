using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform.ExcelImport;

internal record HierarchicalEntity(string Name, string? ParentName)
{
    public OctoObjectId? RtId { get; set; }
    public List<Tuple<string, string>> Attributes { get; set; } = [];
}