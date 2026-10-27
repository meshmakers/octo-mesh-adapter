using System.Globalization;
using System.Reflection;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     AB#6933: <c>GetRtEntitiesByType@1</c> with <c>ckInterfaceId</c> is a thin call of the ONE engine method
///     <see cref="ITenantRepository.GetRtEntitiesByInterfaceAsync" /> (AB#7019). The node holds no expansion, fan-out or
///     merge; these tests pin the hand-over to the engine, the node-side validation and the pass-through of the engine's
///     refusals (number 7019, reason token, parameters).
/// </summary>
public class GetRtEntitiesByTypeNodeInterfaceTests : SessionNodeTestBase
{
    private static readonly RtCkId<CkInterfaceId> NamedInterface = new("System/Named-1");

    private GetRtEntitiesByTypeNode CreateNode(NodeDelegate next) => new(next, EtlContext);

    private static GetRtEntitiesByTypeNodeConfiguration InterfaceConfig(Action<GetRtEntitiesByTypeNodeConfiguration>? tune = null)
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkInterfaceId = "System/Named-1",
            Take = 50,
            TargetPath = "$.result"
        };
        tune?.Invoke(config);
        return config;
    }

    private void GivenEngineReturns(long? totalCount, params RtEntity[] entities)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
                A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._))
            .Returns(new RtInterfaceQueryResult(entities, totalCount));
    }

    private void GivenEngineThrows(InterfaceQueryException exception)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
                A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._))
            .Throws(exception);
    }

    private void AssertEngineNotCalled()
    {
        A.CallTo(TenantRepository).Where(call => call.Method.Name.StartsWith("GetRtEntitiesBy", StringComparison.Ordinal))
            .MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    // ---- the node calls the shared method and has no expansion of its own -----------------------------------------

    [Fact]
    public async Task WithCkInterfaceId_CallsTheSharedEngineMethodOnce_AndNeverTheTypeQuery()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => { c.Skip = 20; c.Take = 30; }));
        GivenEngineReturns(77);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, NamedInterface,
                A<RtEntityQueryOptions>._, 20, 30, true))
            .MustHaveHappenedOnceExactly();
        // No own expansion: the type query is not used for any implementer, and the repository is touched by that one call only.
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .MustNotHaveHappened();
        A.CallTo(TenantRepository).Where(call => call.Method.Name.StartsWith("GetRtEntitiesBy", StringComparison.Ordinal))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SkipDefaultsToZero()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.Take = 5));
        GivenEngineReturns(0);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
                A<RtEntityQueryOptions>._, 0, 5, A<bool>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FieldFiltersAndSortOrders_AreTranslatedLikeTheTypeVariant()
    {
        var config = InterfaceConfig(c =>
        {
            c.FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "Name",
                    Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = "alpha"
                }
            ];
            c.SortOrders = [new SortOrderDto { AttributeName = "Name", SortOrder = SortOrdersDto.Descending }];
        });
        var (dataContext, nodeContext, next) = PrepareTest(config);
        GivenEngineReturns(1);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        var call = Fake.GetCalls(TenantRepository)
            .Single(c => c.Method.Name == nameof(ITenantRepository.GetRtEntitiesByInterfaceAsync));
        var options = (RtEntityQueryOptions)call.Arguments[2]!;
        var filter = Assert.Single(options.FieldFilters!);
        Assert.Equal("Name", filter.AttributePath);
        Assert.Equal("alpha", filter.ComparisonValue);
        var sort = Assert.Single(options.SortOrders!);
        Assert.Equal("Name", sort.AttributePath);
        Assert.Equal(SortOrders.Descending, sort.SortOrder);
    }

    [Fact]
    public async Task Result_HasTheShapeOfTheTypeVariant_WithTheTotalCountOfTheEngine()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig());
        var entity = new RtEntity(new RtCkId<CkTypeId>("TestModel/TypeA"), new OctoObjectId("000000000000000000000001"));
        GivenEngineReturns(42, entity);
        IResultSet<RtEntity>? captured = null;
        A.CallTo(() => dataContext.Set("$.result", A<IResultSet<RtEntity>?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes(call => captured = (IResultSet<RtEntity>?)call.Arguments[1]);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        Assert.Equal(42, captured.TotalCount);
        Assert.Equal(entity.CkTypeId, Assert.Single(captured.Items).CkTypeId);
        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task Transaction_IsStartedAndCommitted()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig());
        GivenEngineReturns(0);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => Session.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.AbortTransactionAsync()).MustNotHaveHappened();
    }

    // ---- permissions: the engine call runs on the session of the node's identity ----------------------------------

    [Fact]
    public async Task Caller_IsTheDefaultIdentity_AndItsSessionIsTheOneHandedToTheEngine()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig());
        GivenEngineReturns(0);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.GetSessionForAsync(NodeExecutionIdentity.Caller)).MustHaveHappenedOnceExactly();
        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(Session, A<RtCkId<CkInterfaceId>>._,
                A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._))
            .MustHaveHappenedOnceExactly();
        AssertScopedSessionOpened();
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    [InlineData(NodeExecutionIdentity.System)]
    public async Task ConfiguredIdentity_IsHonouredExactlyLikeTheTypeVariant(NodeExecutionIdentity identity)
    {
        GivenSystemSessionIsExpected();
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.Identity = identity));
        GivenEngineReturns(0);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.GetSessionForAsync(identity)).MustHaveHappenedOnceExactly();
    }

    // ---- node validation: stable code + parameters, nothing reaches the engine ------------------------------------

    [Fact]
    public async Task BothTypeAndInterface_FailsValidation_WithoutTouchingTheEngine()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.CkTypeId = new RtCkId<CkTypeId>("TestModel/TypeA")));

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.CkTypeAndCkInterfaceBothSet);
        AssertEngineNotCalled();
    }

    [Fact]
    public async Task BothTypePathAndInterface_FailsValidation()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.CkTypeIdPath = "$.typeId"));

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.CkTypeAndCkInterfaceBothSet);
        AssertEngineNotCalled();
    }

    [Fact]
    public async Task NeitherTypeNorInterface_FailsValidation_WithTheSameStableCode()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration { TargetPath = "$.result" };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.CkTypeOrCkInterfaceRequired);
        AssertEngineNotCalled();
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoModelSeparator")]
    [InlineData("System/")]
    public async Task MalformedInterfaceId_FailsValidation_WithTheValueAsParameter(string value)
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.CkInterfaceId = value));

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.CkInterfaceIdInvalid);
        Assert.Equal(value, ex.Parameters["ckInterfaceId"]);
        AssertEngineNotCalled();
    }

    [Fact]
    public async Task MissingTake_FailsValidation_TakeIsRequiredForAnInterface()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => c.Take = null));

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.TakeRequiredForInterface);
        AssertEngineNotCalled();
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    public async Task NegativePaging_FailsValidation(int skip, int take)
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig(c => { c.Skip = skip; c.Take = take; }));

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        AssertConfigurationFailure(ex, RtEntitySelectionReasons.PagingNegative);
        Assert.Equal(skip.ToString(CultureInfo.InvariantCulture), ex.Parameters["skip"]);
        Assert.Equal(take.ToString(CultureInfo.InvariantCulture), ex.Parameters["take"]);
        AssertEngineNotCalled();
    }

    [Fact]
    public async Task TypeVariantWithoutInterface_IsUnchanged()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = new RtCkId<CkTypeId>("TestModel/TypeA"),
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).Returns(resultSet);

        await CreateNode(next).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
            A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._)).MustNotHaveHappened();
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustHaveHappenedOnceExactly();
    }

    // ---- the engine's refusals reach the pipeline run with number, reason token and parameters --------------------

    public static TheoryData<string, string[]> EngineRefusals => new()
    {
        { InterfaceQueryReasons.UnknownInterface, ["ckInterfaceId", "System/Named-1"] },
        { InterfaceQueryReasons.TextSearchNotSupported, ["ckInterfaceId", "System/Named-1"] },
        { InterfaceQueryReasons.AggregationNotSupported, ["ckInterfaceId", "System/Named-1"] },
        { InterfaceQueryReasons.NavigationNotSupported, ["ckInterfaceId", "System/Named-1", "attributePath", "Owner->Name"] },
        { InterfaceQueryReasons.TooManyCollectionRoots, ["ckInterfaceId", "System/Named-1", "rootCount", "65", "maxRoots", "64"] },
        { InterfaceQueryReasons.PageTooLarge, ["skip", "900", "take", "200", "maxSkipPlusTake", "1000"] },
        { InterfaceQueryReasons.UnknownMember, ["ckInterfaceId", "System/Named-1", "attributePath", "Nope", "usage", "sort"] }
    };

    [Theory]
    [MemberData(nameof(EngineRefusals))]
    public async Task EngineRefusal_ReachesThePipelineRunWithTheSameNumberReasonAndParameters(string reason, string[] nameValuePairs)
    {
        var parameters = new List<(string, string)>();
        for (var i = 0; i < nameValuePairs.Length; i += 2)
        {
            parameters.Add((nameValuePairs[i], nameValuePairs[i + 1]));
        }

        var engineException = CreateEngineException(reason, [.. parameters]);
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig());
        GivenEngineThrows(engineException);

        var ex = await Assert.ThrowsAsync<RtEntitySelectionException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(RtEntitySelectionCodes.InterfaceQuery, ex.Code);
        Assert.Equal(7019, ex.MessageNumber);
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(engineException.Parameters.OrderBy(p => p.Key), ex.Parameters.OrderBy(p => p.Key));
        Assert.Equal(reason, ex.Parameters["reason"]);
        Assert.Same(engineException, ex.InnerException);
        // The refused call does not leave a transaction open and does not continue the pipeline.
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task OtherEngineFailures_ArePassedOnUnchanged_AndTheTransactionIsAborted()
    {
        var (dataContext, nodeContext, next) = PrepareTest(InterfaceConfig());
        var failure = new InvalidOperationException("boom");
        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
            A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._)).Throws(failure);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateNode(next).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Same(failure, ex);
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static void AssertConfigurationFailure(RtEntitySelectionException ex, string reason)
    {
        Assert.Equal(RtEntitySelectionCodes.NodeConfigurationInvalid, ex.Code);
        Assert.Equal(reason, ex.Reason);
        Assert.Null(ex.MessageNumber);
        Assert.True(ex.Parameters.ContainsKey("node"));
    }

    /// <summary>
    ///     The engine's exception can only be built by the engine (private constructor, internal factories); the test
    ///     builds the same shape through the engine's own <c>Create</c> so number, reason and parameters are the real ones.
    /// </summary>
    private static InterfaceQueryException CreateEngineException(string reason, (string Name, string Value)[] parameters)
    {
        var create = typeof(InterfaceQueryException).GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("InterfaceQueryException.Create not found; the engine changed.");
        return (InterfaceQueryException)create.Invoke(null, [reason, $"engine text for {reason}", parameters])!;
    }
}
