using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// Regression test for review finding #5:
///
/// <see cref="FieldFilterExtensions.GetFieldFilter"/> resolved
/// <see cref="FieldFilterWithPathDto.ComparisonValuePath"/> via
/// <c>dataContext.GetKind(path)</c> + <c>dataContext.Get&lt;JsonArray&gt;(path)</c> /
/// <c>dataContext.Get&lt;JsonNode&gt;(path)</c>. For wildcard JSONPath expressions
/// (e.g. <c>$.items[*].id</c>), <c>GetKind</c> returns the kind of the first match
/// and <c>Get&lt;JsonNode&gt;</c> returns only that first match — so an N-element
/// wildcard collapsed to a single scalar comparison value. This silently broke
/// In / NotIn / AnyEq filters that production pipelines use to build dynamic
/// query sets. Same fix shape as commit edfba77 in CreateUpdateInfoNode.
/// </summary>
public class FieldFilterExtensionsTests
{
    [Fact]
    public void GetFieldFilter_WildcardComparisonValuePath_ProducesMultiElementList()
    {
        const string json = """
            {
                "items": [
                    { "id": "a" },
                    { "id": "b" },
                    { "id": "c" }
                ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        var filters = new List<FieldFilterWithPathDto>
        {
            new()
            {
                AttributePath = "name",
                Operator = FieldFilterOperatorDto.In,
                ComparisonValuePath = "$.items[*].id"
            }
        };

        var queryOptions = RtEntityQueryOptions.Create();
        filters.GetFieldFilter(dataContext, queryOptions);

        Assert.NotNull(queryOptions.FieldFilters);
        var filter = Assert.Single(queryOptions.FieldFilters);
        Assert.Equal("name", filter.AttributePath);
        Assert.Equal(FieldFilterOperator.In, filter.Operator);

        // The bug: wildcard collapsed to first match ("a"). After fix: all three.
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(filter.ComparisonValue);
        var values = ((System.Collections.IEnumerable)filter.ComparisonValue!).Cast<object?>().ToList();
        Assert.Equal(3, values.Count);
        Assert.Equal(new object?[] { "a", "b", "c" }, values);
    }

    [Fact]
    public void GetFieldFilter_ScalarComparisonValuePath_ProducesScalar()
    {
        // Sanity check the non-wildcard path is unaffected by the fix.
        const string json = """{ "filter": { "value": "x" } }""";
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        var filters = new List<FieldFilterWithPathDto>
        {
            new()
            {
                AttributePath = "name",
                Operator = FieldFilterOperatorDto.Equals,
                ComparisonValuePath = "$.filter.value"
            }
        };

        var queryOptions = RtEntityQueryOptions.Create();
        filters.GetFieldFilter(dataContext, queryOptions);

        var filter = Assert.Single(queryOptions.FieldFilters!);
        Assert.Equal("x", filter.ComparisonValue);
    }

    [Fact]
    public void GetFieldFilter_IntegerScalarComparisonValuePath_IsInt()
    {
        // The comparison value for an integer JSON number that fits in Int32 boxes to int —
        // matches Newtonsoft's in-memory round-trip (JObject.FromObject(int) → JValue with
        // Value=Int32). Larger values fall through to long. Enforced by
        // Sdk.Common.PipelineParityTests.AttributeRoundTripClrTypeParityTests.
        const string json = """{ "filter": { "value": 42 } }""";
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        var filters = new List<FieldFilterWithPathDto>
        {
            new()
            {
                AttributePath = "count",
                Operator = FieldFilterOperatorDto.Equals,
                ComparisonValuePath = "$.filter.value"
            }
        };

        var queryOptions = RtEntityQueryOptions.Create();
        filters.GetFieldFilter(dataContext, queryOptions);

        var filter = Assert.Single(queryOptions.FieldFilters!);
        Assert.IsType<int>(filter.ComparisonValue);
        Assert.Equal(42, filter.ComparisonValue);
    }

    [Fact]
    public void GetFieldFilter_IntegerWildcardComparisonValuePath_ProducesIntList()
    {
        // Same Int32 preference applied per-element across a wildcard expansion.
        const string json = """{ "items": [ { "id": 1 }, { "id": 2 } ] }""";
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        var filters = new List<FieldFilterWithPathDto>
        {
            new()
            {
                AttributePath = "id",
                Operator = FieldFilterOperatorDto.In,
                ComparisonValuePath = "$.items[*].id"
            }
        };

        var queryOptions = RtEntityQueryOptions.Create();
        filters.GetFieldFilter(dataContext, queryOptions);

        var filter = Assert.Single(queryOptions.FieldFilters!);
        var values = ((System.Collections.IEnumerable)filter.ComparisonValue!).Cast<object?>().ToList();
        Assert.Equal(new object?[] { 1, 2 }, values);
    }

    [Fact]
    public void GetFieldFilter_LiteralArrayPath_ProducesList()
    {
        // The pre-existing DataKind.Array branch still works after the wildcard fix.
        const string json = """{ "ids": ["x", "y"] }""";
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        var filters = new List<FieldFilterWithPathDto>
        {
            new()
            {
                AttributePath = "name",
                Operator = FieldFilterOperatorDto.In,
                ComparisonValuePath = "$.ids"
            }
        };

        var queryOptions = RtEntityQueryOptions.Create();
        filters.GetFieldFilter(dataContext, queryOptions);

        var filter = Assert.Single(queryOptions.FieldFilters!);
        Assert.IsAssignableFrom<System.Collections.IEnumerable>(filter.ComparisonValue);
        var values = ((System.Collections.IEnumerable)filter.ComparisonValue!).Cast<object?>().ToList();
        Assert.Equal(new object?[] { "x", "y" }, values);
    }
}
