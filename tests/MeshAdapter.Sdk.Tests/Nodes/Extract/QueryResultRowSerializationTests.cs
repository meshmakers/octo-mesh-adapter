using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Newtonsoft.Json;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// Tests that QueryResultRow serialization correctly handles RtCkId via Newtonsoft.Json.
/// Regression test for bug: "In the result of the node GetQueryById CkTypeId is null."
/// Root cause was using NewtonCkTypeIdConverter (for CkId) instead of
/// NewtonRtCkTypeIdConverter (for RtCkId) on the CkTypeId property.
/// </summary>
public class QueryResultRowSerializationTests
{
    [Fact]
    public void Serialize_QueryResultRow_CkTypeIdIsNotNull()
    {
        // Arrange
        var ckTypeId = new RtCkId<CkTypeId>("System", new CkTypeId("Person-1"));
        var row = new QueryResultRow
        {
            RtId = new OctoObjectId("507f1f77bcf86cd799439011"),
            CkTypeId = ckTypeId,
            Values = ["TestValue"]
        };

        // Act
        var json = JsonConvert.SerializeObject(row);

        // Assert
        Assert.Contains("\"CkTypeId\":", json);
        Assert.DoesNotContain("\"CkTypeId\":null", json);
    }

    [Fact]
    public void Deserialize_QueryResultRow_CkTypeIdIsPreserved()
    {
        // Arrange
        var ckTypeId = new RtCkId<CkTypeId>("System", new CkTypeId("Person-1"));
        var row = new QueryResultRow
        {
            RtId = new OctoObjectId("507f1f77bcf86cd799439011"),
            CkTypeId = ckTypeId,
            Values = ["TestValue"]
        };

        // Act
        var json = JsonConvert.SerializeObject(row);
        var deserialized = JsonConvert.DeserializeObject<QueryResultRow>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.CkTypeId);
        Assert.Equal(ckTypeId, deserialized.CkTypeId);
    }

    [Fact]
    public void Serialize_QueryResult_AllRowsCkTypeIdsAreNotNull()
    {
        // Arrange
        var queryResult = new QueryResult();
        queryResult.Columns.Add(new QueryResultColumns { Header = "Name" });
        queryResult.Rows.Add(new QueryResultRow
        {
            RtId = new OctoObjectId("507f1f77bcf86cd799439011"),
            CkTypeId = new RtCkId<CkTypeId>("System", new CkTypeId("Person-1")),
            Values = ["Alice"]
        });
        queryResult.Rows.Add(new QueryResultRow
        {
            RtId = new OctoObjectId("507f1f77bcf86cd799439012"),
            CkTypeId = new RtCkId<CkTypeId>("Energy", new CkTypeId("Device-2")),
            Values = ["Sensor"]
        });

        // Act
        var json = JsonConvert.SerializeObject(queryResult);
        var deserialized = JsonConvert.DeserializeObject<QueryResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Rows.Count);

        foreach (var row in deserialized.Rows)
        {
            Assert.NotNull(row.CkTypeId);
        }

        // CkTypeId.SemanticVersionedFullName omits version suffix for version 1
        Assert.Equal("System/Person", deserialized.Rows[0].CkTypeId.SemanticVersionedFullName);
        Assert.Equal("Energy/Device-2", deserialized.Rows[1].CkTypeId.SemanticVersionedFullName);
    }
}
