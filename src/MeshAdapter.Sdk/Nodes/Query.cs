using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal class QueryResult
{
    public List<QueryResultColumns> Columns { get; } = new();
    public List<QueryResultRow> Rows { get; } = new();
}

internal class QueryResultColumns
{
    public required string Header { get; set; }
}

internal class QueryResultRow
{
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId? RtId { get; set; }
    [JsonConverter(typeof(RtCkIdTypeIdConverter))]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }
    public List<object?> Values { get; set; } = new();
}