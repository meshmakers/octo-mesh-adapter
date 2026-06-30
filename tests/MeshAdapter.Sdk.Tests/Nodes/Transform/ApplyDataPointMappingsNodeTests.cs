using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the CK-value-type → <see cref="FormulaResultType"/> mapping the node uses to cast a mapping
/// expression's numeric result back to the target attribute's CLR type. This is what lets a
/// DataPointMapping drive a Boolean target (e.g. <c>IsCharging ← value &gt; 0</c>) — the
/// AttributeValueConverter does not coerce a raw double into a bool, so the node must produce a
/// typed value via <c>IFormulaEngine.Evaluate(..., FormulaResultType)</c>.
/// </summary>
public class ApplyDataPointMappingsNodeTests
{
    [Theory]
    [InlineData(AttributeValueTypesDto.Boolean, FormulaResultType.Boolean)]
    [InlineData(AttributeValueTypesDto.Int, FormulaResultType.Int)] // == Integer
    [InlineData(AttributeValueTypesDto.Enum, FormulaResultType.Int)]
    [InlineData(AttributeValueTypesDto.Int64, FormulaResultType.Int64)] // == Integer64
    [InlineData(AttributeValueTypesDto.Double, FormulaResultType.Double)]
    [InlineData(AttributeValueTypesDto.DateTime, FormulaResultType.DateTime)]
    public void MapValueTypeToFormulaResultType_ScalarTypes_AreTyped(
        AttributeValueTypesDto valueType, FormulaResultType expected)
    {
        Assert.Equal(expected, ApplyDataPointMappingsNode.MapValueTypeToFormulaResultType(valueType));
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.String)]
    [InlineData(AttributeValueTypesDto.Binary)]
    [InlineData(AttributeValueTypesDto.BinaryLinked)]
    [InlineData(AttributeValueTypesDto.StringArray)]
    [InlineData(AttributeValueTypesDto.IntArray)]
    [InlineData(AttributeValueTypesDto.Record)]
    [InlineData(AttributeValueTypesDto.RecordArray)]
    [InlineData(AttributeValueTypesDto.TimeSpan)]
    [InlineData(AttributeValueTypesDto.DateTimeOffset)]
    [InlineData(AttributeValueTypesDto.GeospatialPoint)]
    public void MapValueTypeToFormulaResultType_NonScalarTypes_FallBackToRawDouble(
        AttributeValueTypesDto valueType)
    {
        // null signals the caller to keep the raw EvaluateRaw(double) path.
        Assert.Null(ApplyDataPointMappingsNode.MapValueTypeToFormulaResultType(valueType));
    }
}
