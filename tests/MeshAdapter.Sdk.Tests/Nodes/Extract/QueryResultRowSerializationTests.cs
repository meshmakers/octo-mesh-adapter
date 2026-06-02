using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// Tests that QueryResultRow serialization correctly handles RtCkId via STJ converters.
/// Regression test for bug: "In the result of the node GetQueryById CkTypeId is null."
/// Root cause was using a CkTypeId converter (for CkId) instead of the RtCkId
/// converter on the CkTypeId property.
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
        var json = JsonSerializer.Serialize(row);

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
        var json = JsonSerializer.Serialize(row);
        var deserialized = JsonSerializer.Deserialize<QueryResultRow>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.CkTypeId);
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
        var json = JsonSerializer.Serialize(queryResult);

        // Assert — verify the serialized JSON contains both rows with non-null CkTypeId.
        // Round-tripping through STJ Deserialize doesn't repopulate the get-only Rows
        // collection, so we just validate the wire shape directly.
        Assert.Contains("System/Person", json);
        Assert.Contains("Energy/Device-2", json);
        Assert.DoesNotContain("\"CkTypeId\":null", json);
    }
}
