using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Serialization;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

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
    [Newtonsoft.Json.JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public required OctoObjectId RtId { get; set; }
    [Newtonsoft.Json.JsonConverter(typeof(NewtonCkTypeIdConverter))]
    public required CkId<CkTypeId> CkTypeId { get; set; }
    public List<object?> Values { get; set; } = new();
}