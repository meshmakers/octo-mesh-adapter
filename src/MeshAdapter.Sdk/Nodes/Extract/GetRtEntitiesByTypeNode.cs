using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Gets rt entities by type, or - with <c>ckInterfaceId</c> (AB#6933) - the entities of all types that implement a
/// CK interface through the engine's one shared interface query (<c>GetRtEntitiesByInterfaceAsync</c>); the node
/// itself contains no expansion, fan-out or merge.
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByTypeNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();

        // Everything that can be decided from the configuration is decided before a session is opened.
        Func<IOctoSession, Task<IResultSet<RtEntity>>> query;
        if (c.CkInterfaceId == null && c.CkTypeId == null && c.CkTypeIdPath == null)
        {
            var node = nodeContext.NodePath.ToString();
            throw Invalid(RtEntitySelectionReasons.CkTypeOrCkInterfaceRequired, node,
                $"[{node}]: CkTypeId is not set. Please set ckTypeId or ckTypeIdPath, or ckInterfaceId to select by CK interface.");
        }

        var queryOptions = RtEntityQueryOptions.Create();
        if (c.CkInterfaceId != null)
        {
            // AB#6933: the interface variant is ONE call of the engine's shared method; the node expands, fans out
            // and merges nothing itself. Filters and sort orders are translated exactly as for the type variant, the
            // security filter lives in the engine method and acts on the session of the node's identity.
            var ckInterfaceId = ValidateInterfaceSelection(c, nodeContext, out var skip, out var take);
            c.FieldFilters.GetFieldFilter(dataContext, queryOptions);
            c.SortOrders.GetSortOrders(queryOptions);
            query = async session =>
            {
                try
                {
                    // The count is always requested so that the output has the shape of the type variant (TotalCount).
                    var result = await etlContext.TenantRepository.GetRtEntitiesByInterfaceAsync(session, ckInterfaceId,
                        queryOptions, skip, take, includeTotalCount: true);
                    return new ResultSet<RtEntity>(result.Items, result.TotalCount ?? result.Items.Count, null, null);
                }
                catch (InterfaceQueryException e)
                {
                    throw ToSelectionException(nodeContext, e);
                }
            };
        }
        else
        {
            var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);
            c.FieldFilters.GetFieldFilter(dataContext, queryOptions);
            c.SortOrders.GetSortOrders(queryOptions);
            query = session => etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, queryOptions,
                c.Skip, c.Take);
        }

        // AB#5028 — scoped: an ordinary read of tenant business data.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        IResultSet<RtEntity> r;
        try
        {
            r = await query(session);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }

        dataContext.Set(c.TargetPath, r, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static RtCkId<CkInterfaceId> ValidateInterfaceSelection(GetRtEntitiesByTypeNodeConfiguration c,
        INodeContext nodeContext, out int skip, out int take)
    {
        var node = nodeContext.NodePath.ToString();

        if (c.CkTypeId != null || c.CkTypeIdPath != null)
        {
            throw Invalid(RtEntitySelectionReasons.CkTypeAndCkInterfaceBothSet, node,
                $"[{node}]: ckInterfaceId cannot be combined with ckTypeId or ckTypeIdPath; set exactly one.");
        }

        RtCkId<CkInterfaceId> ckInterfaceId;
        try
        {
            if (string.IsNullOrWhiteSpace(c.CkInterfaceId))
            {
                throw new ArgumentException("empty");
            }

            ckInterfaceId = new RtCkId<CkInterfaceId>(c.CkInterfaceId);
        }
        catch (ArgumentException)
        {
            throw Invalid(RtEntitySelectionReasons.CkInterfaceIdInvalid, node,
                $"[{node}]: ckInterfaceId '{c.CkInterfaceId}' is not a valid CK interface id (expected <model>/<interface>-<version>, e.g. System/Named-1).",
                ("ckInterfaceId", c.CkInterfaceId!));
        }

        skip = c.Skip ?? 0;
        if (c.Take == null)
        {
            throw Invalid(RtEntitySelectionReasons.TakeRequiredForInterface, node,
                $"[{node}]: take is required when ckInterfaceId is set (skip defaults to 0, skip + take at most {RtInterfaceQueryLimits.MaxSkipPlusTake}).");
        }

        take = c.Take.Value;
        if (skip < 0 || take < 0)
        {
            throw Invalid(RtEntitySelectionReasons.PagingNegative, node,
                $"[{node}]: skip and take must not be negative.",
                ("skip", skip.ToString(CultureInfo.InvariantCulture)),
                ("take", take.ToString(CultureInfo.InvariantCulture)));
        }

        return ckInterfaceId;
    }

    private static RtEntitySelectionException Invalid(string reason, string node, string message,
        params (string Name, string Value)[] parameters)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal) { ["node"] = node };
        foreach (var (name, value) in parameters)
        {
            dictionary[name] = value;
        }

        return new RtEntitySelectionException(RtEntitySelectionCodes.NodeConfigurationInvalid, reason, null,
            dictionary, message);
    }

    /// <summary>The engine's refusal reaches the pipeline run with its number, reason token and parameters unchanged.</summary>
    private static RtEntitySelectionException ToSelectionException(INodeContext nodeContext,
        InterfaceQueryException e)
    {
        return new RtEntitySelectionException(RtEntitySelectionCodes.InterfaceQuery, e.Reason, e.MessageNumber,
            e.Parameters, $"[{nodeContext.NodePath}]: {e.Message}", e);
    }
}
