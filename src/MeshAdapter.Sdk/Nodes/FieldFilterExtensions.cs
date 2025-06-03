using System.Globalization;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal static class FieldFilterExtensions
{
    internal static void GetFieldFilter(this ICollection<FieldFilterWithPathDto>? fieldFilters,
        IDataContext dataContext,
        DataQueryOperation dataQueryOperation)
    {
        if (fieldFilters != null)
        {
            foreach (var fieldFilter in fieldFilters)
            {
                var comparisonValue = GetComparisonValue(fieldFilter.ComparisonValue);
                if (comparisonValue == null && !string.IsNullOrWhiteSpace(fieldFilter.ComparisonValuePath))
                {
                    var t = dataContext.Current?.SelectTokens(fieldFilter.ComparisonValuePath ?? "$").ToList();
                    if (t != null)
                    {
                        if (t.Count == 1)
                        {
                            comparisonValue = t.First();
                        }
                        else if (t.Count > 1)
                        {
                            comparisonValue = t;
                        }
                    }
                }

                dataQueryOperation.AddFieldFilter(fieldFilter.AttributePath, GetOperator(fieldFilter.Operator),
                    comparisonValue);
            }
        }
    }

    private static object? GetComparisonValue(object? comparisonValue)
    {
        if (comparisonValue is JValue jValue)
        {
            switch (jValue.Type)
            {
                case JTokenType.Float:
                    return jValue.Value<double>();
                case JTokenType.Boolean:
                    return jValue.Value<bool>();
                case JTokenType.Date:
                    return jValue.Value<DateTime>();
                case JTokenType.String:
                    return jValue.Value<string>();
                case JTokenType.Null:
                    return null;
                default:
                    return jValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return comparisonValue;
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