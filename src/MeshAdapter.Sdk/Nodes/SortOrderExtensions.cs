using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal static class SortOrderExtensions
{
    internal static void GetSortOrders(this ICollection<SortOrderDto>? sortOrderDtos,
        RtEntityQueryOptions queryOptions)
    {
        if (sortOrderDtos != null && sortOrderDtos.Any())
        {
            foreach (var s in sortOrderDtos)
            {
                queryOptions.SortOrder(s.AttributeName, GetSortOrder(s.SortOrder));
            }
        }

    }

    /// <summary>
    /// Converts the configured sort orders into the engine's sort-order items. Used by the nodes that
    /// query storage layers taking <see cref="SortOrderItem"/> directly (stream data) rather than an
    /// <see cref="RtEntityQueryOptions"/>. Returns null when nothing is configured, so callers can
    /// leave the option unset.
    /// </summary>
    internal static IReadOnlyList<SortOrderItem>? GetSortOrderItems(
        this ICollection<SortOrderDto>? sortOrderDtos)
    {
        if (sortOrderDtos == null || sortOrderDtos.Count == 0)
        {
            return null;
        }

        return sortOrderDtos
            .Select(s => new SortOrderItem(s.AttributeName, GetSortOrder(s.SortOrder)))
            .ToList();
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