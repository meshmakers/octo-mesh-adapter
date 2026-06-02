using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal static class FieldFilterExtensions
{
    internal static void GetFieldFilter(this ICollection<FieldFilterWithPathDto>? fieldFilters,
        IDataContext dataContext,
        RtEntityQueryOptions queryOptions)
    {
        if (fieldFilters != null)
        {
            foreach (var fieldFilter in fieldFilters)
            {
                var comparisonValue = fieldFilter.ComparisonValue;
                if (comparisonValue == null && !string.IsNullOrWhiteSpace(fieldFilter.ComparisonValuePath))
                {
                    var path = fieldFilter.ComparisonValuePath ?? "$";
                    // SelectMatches captures the full JSONPath dialect, including wildcards
                    // (e.g. "$.items[*].id"). The previous Get<JsonNode>(path) returned only
                    // the first match, silently collapsing N-element wildcard expansions to
                    // a single scalar comparison value. Same fix shape as commit edfba77 in
                    // CreateUpdateInfoNode.
                    var matches = dataContext.SelectMatches(path).ToList();
                    if (matches.Count == 1)
                    {
                        // Single match: either a literal JSON array (e.g. "$.ids" → unwrap)
                        // or a single scalar (e.g. "$.filter.value" → take as-is).
                        if (matches[0].GetKind("$") == DataKind.Array)
                        {
                            comparisonValue = ReadArrayScalars(matches[0]);
                        }
                        else
                        {
                            comparisonValue = matches[0].GetValue("$");
                        }
                    }
                    else if (matches.Count > 1)
                    {
                        // Multi-match (wildcard / recursive descent) — list of scalars.
                        comparisonValue = matches.Select(m => m.GetValue("$")).ToList();
                    }
                    // matches.Count == 0 → path resolved to nothing; leave comparisonValue null.
                }

                queryOptions.AddFieldFilter(fieldFilter.AttributePath, GetOperator(fieldFilter.Operator),
                    comparisonValue);
            }
        }
    }

    // Reads each element of the array sub-context as its natural CLR scalar via GetValue,
    // which routes through JsonScalar.ToClr — Int32 for integers that fit, Int64 for larger
    // values, double for reals. Matches the Newtonsoft pre-migration boxing as enforced by
    // Sdk.Common.PipelineParityTests.AttributeRoundTripClrTypeParityTests.
    private static List<object?> ReadArrayScalars(IDataContext arrayContext)
    {
        var length = arrayContext.Length("$");
        var result = new List<object?>(length);
        for (var i = 0; i < length; i++)
        {
            result.Add(arrayContext.GetValue($"$[{i}]"));
        }

        return result;
    }

    private static FieldFilterOperator GetOperator(FieldFilterOperatorDto f)
    {
        return f switch
        {
            FieldFilterOperatorDto.Equals => FieldFilterOperator.Equals,
            FieldFilterOperatorDto.NotEquals => FieldFilterOperator.NotEquals,
            FieldFilterOperatorDto.LessThan => FieldFilterOperator.LessThan,
            FieldFilterOperatorDto.LessEqualThan => FieldFilterOperator.LessEqualThan,
            FieldFilterOperatorDto.GreaterThan => FieldFilterOperator.GreaterThan,
            FieldFilterOperatorDto.GreaterEqualThan => FieldFilterOperator.GreaterEqualThan,
            FieldFilterOperatorDto.In => FieldFilterOperator.In,
            FieldFilterOperatorDto.NotIn => FieldFilterOperator.NotIn,
            FieldFilterOperatorDto.Like => FieldFilterOperator.Like,
            FieldFilterOperatorDto.MatchRegEx => FieldFilterOperator.MatchRegEx,
            FieldFilterOperatorDto.AnyEq => FieldFilterOperator.AnyEq,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, null)
        };
    }
}