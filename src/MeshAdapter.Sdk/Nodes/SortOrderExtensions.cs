using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal static class SortOrderExtensions
{
    internal static void GetSortOrders(this ICollection<SortOrderDto>? sortOrderDtos,
        IDataContext dataContext,
        DataQueryOperation dataQueryOperation)
    {
        if (sortOrderDtos != null && sortOrderDtos.Any())
        {
            foreach (var s in sortOrderDtos)
            {
                dataQueryOperation.SortOrder(s.AttributeName, GetSortOrder(s.SortOrder));
            }
        }

    }

    private static SortOrders GetSortOrder(SortOrdersDto sortOrder)
    {
        return sortOrder switch
        {
            SortOrdersDto.Ascending => SortOrders.Ascending,
            SortOrdersDto.Descending => SortOrders.Descending,
            SortOrdersDto.Default => SortOrders.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, null)
        };
    }
}