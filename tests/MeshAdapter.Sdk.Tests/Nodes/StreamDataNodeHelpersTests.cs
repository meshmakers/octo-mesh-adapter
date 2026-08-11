using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Direct tests for the helpers shared by <c>GetQueryById@1</c> and <c>GetStreamData@1</c>. The node
/// suites cover the common paths end-to-end; these focus on the branches a node test cannot reach
/// cheaply — the aggregation-key fallback, the boxing variants a JSON round-trip produces, and the
/// list-resolution shapes.
/// </summary>
public class StreamDataNodeHelpersTests
{
    private const string Hint = "the value stays unset.";

    private static INodeContext FakeNodeContext() => A.Fake<INodeContext>();

    // ------------------------------------------------------------------ ToUtc

    [Fact]
    public void ToUtc_UnspecifiedKind_IsReadAsUtcNotShifted()
    {
        var value = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var result = StreamDataNodeHelpers.ToUtc(value);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        // The clock reading must be preserved — only the kind is stamped.
        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ToUtc_UtcKind_IsUnchanged()
    {
        var value = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(value, StreamDataNodeHelpers.ToUtc(value));
    }

    [Fact]
    public void ToUtc_LocalKind_IsConverted()
    {
        var value = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);

        var result = StreamDataNodeHelpers.ToUtc(value);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(value.ToUniversalTime(), result);
    }

    [Fact]
    public void ToUtcOrNull_Null_StaysNull()
    {
        Assert.Null(StreamDataNodeHelpers.ToUtcOrNull(null));
    }

    // ------------------------------------------------- ResolveDateTimeFromPath

    [Fact]
    public void ResolveDateTimeFromPath_BlankPath_ReturnsNullWithoutTouchingContext()
    {
        var dataContext = A.Fake<IDataContext>();

        var result = StreamDataNodeHelpers.ResolveDateTimeFromPath(
            dataContext, FakeNodeContext(), "  ", "FromPath", Hint);

        Assert.Null(result);
        A.CallTo(() => dataContext.GetValue(A<string>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public void ResolveDateTimeFromPath_DateTimeOffset_IsConvertedToUtc()
    {
        var dataContext = A.Fake<IDataContext>();
        // 14:00 at +02:00 is 12:00 UTC.
        A.CallTo(() => dataContext.GetValue("$.from", A<bool>._))
            .Returns(new DateTimeOffset(2026, 7, 1, 14, 0, 0, TimeSpan.FromHours(2)));

        var result = StreamDataNodeHelpers.ResolveDateTimeFromPath(
            dataContext, FakeNodeContext(), "$.from", "FromPath", Hint);

        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ResolveDateTimeFromPath_StringWithoutOffset_IsReadAsUtc()
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetValue("$.from", A<bool>._)).Returns("2026-07-01T12:00:00");

        var result = StreamDataNodeHelpers.ResolveDateTimeFromPath(
            dataContext, FakeNodeContext(), "$.from", "FromPath", Hint);

        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ResolveDateTimeFromPath_UnresolvedPath_WarnsWithTheCallersHint()
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = FakeNodeContext();
        A.CallTo(() => dataContext.GetValue("$.from", A<bool>._)).Returns(null);

        var result = StreamDataNodeHelpers.ResolveDateTimeFromPath(
            dataContext, nodeContext, "$.from", "FromPath", Hint);

        Assert.Null(result);
        // The hint is supplied by the caller because the fallback differs per node (persisted query
        // value vs. open boundary).
        A.CallTo(() => nodeContext.Warning(A<string>.That.Contains(Hint), A<object[]>._))
            .MustHaveHappened();
    }

    [Fact]
    public void ResolveDateTimeFromPath_PresentButNotADate_Throws()
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetValue("$.from", A<bool>._)).Returns(new object());

        Assert.Throws<MeshAdapterPipelineExecutionException>(() =>
            StreamDataNodeHelpers.ResolveDateTimeFromPath(
                dataContext, FakeNodeContext(), "$.from", "FromPath", Hint));
    }

    // ----------------------------------------------------- ResolveIntFromPath

    public static TheoryData<object, int> IntBoxingVariants => new()
    {
        // JsonScalar.ToClr prefers Int32, falls back to Int64 for larger values; reals box to double.
        { 42, 42 },
        { 42L, 42 },
        { 42.0d, 42 },
        { "42", 42 }
    };

    [Theory]
    [MemberData(nameof(IntBoxingVariants))]
    public void ResolveIntFromPath_AcceptsEveryBoxingVariant(object value, int expected)
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetValue("$.limit", A<bool>._)).Returns(value);

        var result = StreamDataNodeHelpers.ResolveIntFromPath(
            dataContext, FakeNodeContext(), "$.limit", "LimitPath", Hint);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveIntFromPath_LongOutsideInt32Range_Throws()
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetValue("$.limit", A<bool>._)).Returns(long.MaxValue);

        Assert.Throws<MeshAdapterPipelineExecutionException>(() =>
            StreamDataNodeHelpers.ResolveIntFromPath(
                dataContext, FakeNodeContext(), "$.limit", "LimitPath", Hint));
    }

    [Fact]
    public void ResolveIntFromPath_NonIntegralDouble_Throws()
    {
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.GetValue("$.limit", A<bool>._)).Returns(42.5d);

        Assert.Throws<MeshAdapterPipelineExecutionException>(() =>
            StreamDataNodeHelpers.ResolveIntFromPath(
                dataContext, FakeNodeContext(), "$.limit", "LimitPath", Hint));
    }

    [Fact]
    public void ResolveIntFromPath_UnresolvedPath_WarnsAndReturnsNull()
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = FakeNodeContext();
        A.CallTo(() => dataContext.GetValue("$.limit", A<bool>._)).Returns(null);

        Assert.Null(StreamDataNodeHelpers.ResolveIntFromPath(
            dataContext, nodeContext, "$.limit", "LimitPath", Hint));
        A.CallTo(() => nodeContext.Warning(A<string>._, A<object[]>._)).MustHaveHappened();
    }

    // ---------------------------------------------- ResolveStringListFromPath

    [Fact]
    public void ResolveStringListFromPath_SingleScalarMatch_ReturnsOneValue()
    {
        var dataContext = A.Fake<IDataContext>();
        SetupMatches(dataContext, "$.name", Scalar("METER-1"));

        var result = StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, FakeNodeContext(), "$.name", "WellKnownNamesPath", Hint);

        Assert.Equal(["METER-1"], result);
    }

    [Fact]
    public void ResolveStringListFromPath_ArrayMatch_IsUnwrapped()
    {
        var dataContext = A.Fake<IDataContext>();
        SetupMatches(dataContext, "$.names", Array("METER-1", "METER-2", "METER-3"));

        var result = StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, FakeNodeContext(), "$.names", "WellKnownNamesPath", Hint);

        Assert.Equal(["METER-1", "METER-2", "METER-3"], result);
    }

    [Fact]
    public void ResolveStringListFromPath_MultiMatch_CollectsEveryMatch()
    {
        // The shape a wildcard path such as "$.items[*].name" produces.
        var dataContext = A.Fake<IDataContext>();
        SetupMatches(dataContext, "$.items[*].name", Scalar("METER-1"), Scalar("METER-2"));

        var result = StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, FakeNodeContext(), "$.items[*].name", "WellKnownNamesPath", Hint);

        Assert.Equal(["METER-1", "METER-2"], result);
    }

    [Fact]
    public void ResolveStringListFromPath_DropsNullAndBlankEntries()
    {
        var dataContext = A.Fake<IDataContext>();
        SetupMatches(dataContext, "$.names", Array("METER-1", null, "   ", "METER-2"));

        var result = StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, FakeNodeContext(), "$.names", "WellKnownNamesPath", Hint);

        Assert.Equal(["METER-1", "METER-2"], result);
    }

    [Fact]
    public void ResolveStringListFromPath_NoMatches_WarnsAndReturnsNull()
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = FakeNodeContext();
        A.CallTo(() => dataContext.SelectMatches("$.names")).Returns([]);

        var result = StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, nodeContext, "$.names", "WellKnownNamesPath", Hint);

        // Null rather than an empty list, so the caller can tell "not configured" from "configured
        // but empty" and leave the filter off entirely.
        Assert.Null(result);
        A.CallTo(() => nodeContext.Warning(A<string>._, A<object[]>._)).MustHaveHappened();
    }

    [Fact]
    public void ResolveStringListFromPath_OnlyBlankEntries_WarnsAndReturnsNull()
    {
        var dataContext = A.Fake<IDataContext>();
        var nodeContext = FakeNodeContext();
        SetupMatches(dataContext, "$.names", Array("  ", null));

        Assert.Null(StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, nodeContext, "$.names", "WellKnownNamesPath", Hint));
        A.CallTo(() => nodeContext.Warning(A<string>._, A<object[]>._)).MustHaveHappened();
    }

    [Fact]
    public void ResolveStringListFromPath_BlankPath_ReturnsNullWithoutTouchingContext()
    {
        var dataContext = A.Fake<IDataContext>();

        Assert.Null(StreamDataNodeHelpers.ResolveStringListFromPath(
            dataContext, FakeNodeContext(), null, "WellKnownNamesPath", Hint));
        A.CallTo(() => dataContext.SelectMatches(A<string>._)).MustNotHaveHappened();
    }

    // ---------------------------------------------- ResolveStreamColumnValue

    [Fact]
    public void ResolveStreamColumnValue_ReadsByStorageKey()
    {
        // No derivation happens here any more — the caller supplies the key the query layer used,
        // which is what makes a versioned computed column readable at all (AB#4764).
        var values = new Dictionary<string, object?>
        {
            ["amountvalue"] = 42.0,
            ["mycomputed__v2"] = 7.5
        };

        Assert.Equal(42.0, StreamDataNodeHelpers.ResolveStreamColumnValue(values, "amountvalue"));
        Assert.Equal(7.5, StreamDataNodeHelpers.ResolveStreamColumnValue(values, "mycomputed__v2"));
    }

    [Fact]
    public void ResolveStreamColumnValue_MissingKey_ReturnsNull()
    {
        var values = new Dictionary<string, object?> { ["temperature"] = 20.0 };

        Assert.Null(StreamDataNodeHelpers.ResolveStreamColumnValue(values, "humidity"));
    }

    [Fact]
    public void ResolveStreamColumnValue_DoesNotNormaliseTheKey()
    {
        // Passing an attribute path where a storage key is expected must miss rather than silently
        // work — otherwise the derivation would be back, just implicitly.
        var values = new Dictionary<string, object?> { ["amountvalue"] = 42.0 };

        Assert.Null(StreamDataNodeHelpers.ResolveStreamColumnValue(values, "Amount.Value"));
    }

    // ----------------------------------------- ResolveStreamAggregationValue

    [Fact]
    public void ResolveStreamAggregationValue_PrimaryOutputKey_IsPreferred()
    {
        var values = new Dictionary<string, object?> { ["amountvalue_avg"] = 21.5 };

        var result = StreamDataNodeHelpers.ResolveStreamAggregationValue(
            values, "amountvalue", AggregationTypesDto.Average);

        Assert.Equal(21.5, result);
    }

    [Fact]
    public void ResolveStreamAggregationValue_FallsBackToSqlAliasForm()
    {
        // The store also surfaces the raw SQL alias "{Func}_{physicalColumn}".
        var values = new Dictionary<string, object?> { ["Avg_amountvalue"] = 21.5 };

        var result = StreamDataNodeHelpers.ResolveStreamAggregationValue(
            values, "amountvalue", AggregationTypesDto.Average);

        Assert.Equal(21.5, result);
    }

    [Fact]
    public void ResolveStreamAggregationValue_PrimaryWinsOverSqlAlias()
    {
        var values = new Dictionary<string, object?>
        {
            ["amountvalue_avg"] = 1.0,
            ["Avg_amountvalue"] = 2.0
        };

        Assert.Equal(1.0, StreamDataNodeHelpers.ResolveStreamAggregationValue(
            values, "amountvalue", AggregationTypesDto.Average));
    }

    [Fact]
    public void ResolveStreamAggregationValue_NeitherKeyPresent_ReturnsNull()
    {
        var values = new Dictionary<string, object?> { ["amountvalue_sum"] = 1.0 };

        Assert.Null(StreamDataNodeHelpers.ResolveStreamAggregationValue(
            values, "amountvalue", AggregationTypesDto.Average));
    }

    // ------------------------------------------------- MapStreamAggregation

    [Theory]
    [InlineData(AggregationTypesDto.Count, AggregationFunction.Count, "count")]
    [InlineData(AggregationTypesDto.Sum, AggregationFunction.Sum, "sum")]
    [InlineData(AggregationTypesDto.Average, AggregationFunction.Average, "avg")]
    [InlineData(AggregationTypesDto.Minimum, AggregationFunction.Minimum, "min")]
    [InlineData(AggregationTypesDto.Maximum, AggregationFunction.Maximum, "max")]
    public void MapStreamAggregation_MapsFunctionAndResultKeyToken(
        AggregationTypesDto dto, AggregationFunction expectedFunction, string expectedToken)
    {
        var (function, keyToken) = StreamDataNodeHelpers.MapStreamAggregation(dto);

        Assert.Equal(expectedFunction, function);
        Assert.Equal(expectedToken, keyToken);
    }

    [Theory]
    [InlineData(AggregationTypesDto.None)]
    [InlineData(AggregationTypesDto.TimeWeightedAverage)]
    [InlineData(AggregationTypesDto.StateDuration)]
    public void MapStreamAggregation_UnsupportedType_Throws(AggregationTypesDto dto)
    {
        // None is not an aggregation at all; the other two need per-column metadata (carry lookback,
        // comparison value) this mapping cannot supply.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamDataNodeHelpers.MapStreamAggregation(dto));
    }

    // ------------------------------------------------------------- test setup

    private static JsonNode? Scalar(string value) => JsonValue.Create(value);

    private static JsonArray Array(params string?[] values)
        => new(values.Select(v => v is null ? null : JsonValue.Create(v)).ToArray<JsonNode?>());

    /// <summary>
    /// Fakes <see cref="IDataContext.SelectMatches"/>: one sub-context per match, each reporting its
    /// own kind so the array branch and the scalar branch are both reachable.
    /// </summary>
    private static void SetupMatches(IDataContext dataContext, string path, params JsonNode?[] matches)
    {
        var contexts = matches.Select(node =>
        {
            var match = A.Fake<IDataContext>();
            if (node is JsonArray array)
            {
                A.CallTo(() => match.GetKind("$")).Returns(DataKind.Array);
                A.CallTo(() => match.Length("$")).Returns(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    A.CallTo(() => match.GetValue($"$[{i}]", A<bool>._))
                        .Returns(element?.GetValue<string>());
                }
            }
            else
            {
                A.CallTo(() => match.GetKind("$")).Returns(DataKind.String);
                A.CallTo(() => match.GetValue("$", A<bool>._)).Returns(node?.GetValue<string>());
            }

            return match;
        }).ToList();

        A.CallTo(() => dataContext.SelectMatches(path)).Returns(contexts);
    }
}
