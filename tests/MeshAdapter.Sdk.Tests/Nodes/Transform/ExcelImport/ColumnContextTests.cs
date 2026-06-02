using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform.ExcelImport;

/// <summary>
/// Regression test for the Excel-import strict-read finding:
///
/// Pre-migration <c>ColumnContext</c> read cells with Newtonsoft's
/// <c>row[i].Value&lt;string&gt;()</c>, which coerces a JSON number/boolean to its string
/// form (<c>42</c> → <c>"42"</c>, <c>true</c> → <c>"True"</c>). The STJ port reads with
/// <c>node.GetValue&lt;string&gt;()</c>, which THROWS <see cref="InvalidOperationException"/>
/// on any non-string cell. Excel cells routinely arrive as JSON numbers/booleans (quantities,
/// codes, years, flags), so the whole import would fail at runtime. (Note: this is NOT fixed
/// by routing through SystemTextJsonOptions — its <c>AllowReadingFromString</c> only coerces
/// string→number, never number→string; the fix is Newtonsoft-style scalar coercion.)
/// </summary>
public class ColumnContextTests
{
    // Built exactly as ImportFromExcelNode receives them: parsed JSON from
    // dataContext.Get<JsonArray>("$.body.columns") / ("$.body.data").
    private static JsonArray Columns() => (JsonArray)JsonNode.Parse(
        """
        [
            { "attributePath": "name", "columnIndex": 0 },
            { "attributePath": "quantity", "columnIndex": 1 },
            { "attributePath": "active", "columnIndex": 2 }
        ]
        """)!;

    private static JsonArray Row() => (JsonArray)JsonNode.Parse("""["Pump", 42, true]""")!;

    [Fact]
    public void GetValueByIndex_NumericCell_CoercedToString()
    {
        var ctx = new ColumnContext(Columns());
        Assert.Equal("42", ctx.GetValueByIndex<string>(Row(), 1));
    }

    [Fact]
    public void GetValueByIndex_BooleanCell_CoercedToString()
    {
        var ctx = new ColumnContext(Columns());
        Assert.Equal("True", ctx.GetValueByIndex<string>(Row(), 2));
    }

    [Fact]
    public void GetValueByPath_NumericCell_CoercedToString()
    {
        var ctx = new ColumnContext(Columns());
        Assert.Equal("42", ctx.GetValueByPath<string>(Row(), "quantity"));
    }

    [Fact]
    public void GetValueByIndex_StringCell_Unchanged()
    {
        var ctx = new ColumnContext(Columns());
        Assert.Equal("Pump", ctx.GetValueByIndex<string>(Row(), 0));
    }
}
