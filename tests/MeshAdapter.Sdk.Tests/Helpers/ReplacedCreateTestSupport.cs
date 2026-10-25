using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     AB#7463 — builders for the node wiring tests of the generic-create guard: a CK cache in which one type
///     is replaced by a method (<c>replacesGenericCreate</c>) and one is not, and an ETL context that does or
///     does not carry the method-invocation marker.
/// </summary>
internal static class ReplacedCreateTestSupport
{
    public const string Tenant = "tenant1";
    public const string Model = "Test-1.0.0";
    public const string MethodName = "Create-1";

    /// <summary>A type whose generic create is replaced by <see cref="MethodName" />.</summary>
    public const string ReplacedType = "Test/Account";

    /// <summary>A type without a replacing method.</summary>
    public const string PlainType = "Test/Plain";

    public static readonly OctoObjectId PipelineRtId = OctoObjectId.GenerateNewId();

    public static string ReplacingMethodId { get; } = Method().QualifiedMethodId;

    private static CkMethodGraph Method() => new(new CkId<CkTypeId>($"{Model}/Account"),
        new CkMethodDto { MethodId = MethodName, Kind = CkMethodKindDto.Static, ReplacesGenericCreate = true });

    public static RtCkId<CkTypeId> Rt(string id) => new(id);

    private static CkTypeGraph Graph(string rtId, bool replaced) =>
        new(new CkId<CkTypeId>(rtId.Replace("Test/", $"{Model}/")),
            isAbstract: false, isFinal: false, isCollectionRoot: false, baseTypes: [],
            derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [], definedAttributes: [],
            allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(),
            indexes: [], associations: new CkGraphDirectedAssociations([]), description: "test",
            enableChangeStreamPreAndPostImages: false,
            allMethods: replaced ? new Dictionary<string, CkMethodGraph> { [MethodName] = Method() } : null);

    /// <summary>
    ///     A CK cache that knows <see cref="ReplacedType" /> (flagged) and <see cref="PlainType" />, plus every
    ///     extra type in <paramref name="additionalReplacedTypes" /> as flagged.
    /// </summary>
    public static ICkCacheService CkCache(params string[] additionalReplacedTypes)
    {
        var cache = A.Fake<ICkCacheService>();
        var types = new List<(string Type, bool Replaced)> { (ReplacedType, true), (PlainType, false) };
        types.AddRange(additionalReplacedTypes.Select(t => (t, true)));
        foreach (var (type, replaced) in types)
        {
            var graph = Graph(type, replaced);
            var graphOut = (CkTypeGraph?)graph;
            A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(type), out graphOut)).Returns(true)
                .AssignsOutAndRefParameters(graph);
            A.CallTo(() => cache.GetRtCkType(Tenant, Rt(type))).Returns(graph);
        }

        return cache;
    }

    /// <summary>Configures the fake ETL context: tenant, running pipeline and (optionally) the marker.</summary>
    public static void ConfigureRun(IMeshEtlContext context, ExecutePipelineMethodInvocation? marker)
    {
        A.CallTo(() => context.TenantId).Returns(Tenant);
        A.CallTo(() => context.PipelineRtEntityId)
            .Returns(new RtEntityId("System.Communication/Pipeline", PipelineRtId));
        A.CallTo(() => context.MethodInvocation).Returns(marker);
    }

    /// <summary>The marker the controller sends for the replacing method's own run of <see cref="PipelineRtId" />.</summary>
    public static ExecutePipelineMethodInvocation OwnRunMarker() => new()
    {
        MethodId = ReplacingMethodId,
        InvocationId = Guid.NewGuid(),
        CkTypeId = ReplacedType,
        PipelineRtId = PipelineRtId.ToString()
    };

    public static EntityUpdateInfo<RtEntity> Insert(string type, OctoObjectId? rtId = null) =>
        EntityUpdateInfo<RtEntity>.CreateInsert(Rt(type), new RtEntity(Rt(type), rtId ?? OctoObjectId.GenerateNewId()));

    public static EntityUpdateInfo<RtEntity> Update(string type)
    {
        var entity = new RtEntity(Rt(type), OctoObjectId.GenerateNewId());
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(Rt(type), entity.RtId), entity);
    }

    public static EntityUpdateInfo<RtEntity> Replace(string type)
    {
        var entity = new RtEntity(Rt(type), OctoObjectId.GenerateNewId());
        return EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(Rt(type), entity.RtId), entity);
    }
}
