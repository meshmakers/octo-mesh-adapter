using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
///     The engine's released-document lock refuses every caller, the system session included; <c>ApplyChanges@1</c>
///     (always system) and <c>ApplyChanges@2</c> in each identity mode only surface that refusal: the node fails with
///     <see cref="ReleasedDocumentWriteRefusedException" /> carrying the engine's stable code, the transaction is
///     aborted and never committed, and the pipeline does not go on. The real lock behind real pipelines is covered by
///     the integration tests (<c>ReleasedDocumentLockPipelineTests</c>).
/// </summary>
public class ApplyChangesReleasedDocumentRefusalTests : SessionNodeTestBase
{
    private const string DataPath = "$.updates";
    private static readonly RtCkId<CkTypeId> InvoiceType = new("TestModel/Invoice");
    private static readonly OctoObjectId InvoiceId = new("000000000000000000000042");

    public ApplyChangesReleasedDocumentRefusalTests()
    {
        // ApplyChanges@1 and the System identity of @2 run under the system context by design.
        GivenSystemSessionIsExpected();
    }

    private static ReleasedDocumentLockedException Locked(string operation)
    {
        var result = new OperationResult();
        result.AddMessage(new OperationMessage(MessageLevel.Error, $"{InvoiceType}@{InvoiceId}",
            ReleasedDocumentLockedException.MessageNumber, "The released document cannot be changed."));
        return new ReleasedDocumentLockedException(result,
        [
            new ReleasedDocumentRefusal(new RtEntityId(InvoiceType, InvoiceId), operation, ["Title"], "Released",
                "InvoiceNumber")
        ]);
    }

    private static StornoRefusedException StornoRefused()
    {
        var result = new OperationResult();
        result.AddMessage(new OperationMessage(MessageLevel.Error, $"{InvoiceType}@{InvoiceId}", 169,
            "The original is not released."));
        return new StornoRefusedException(result);
    }

    private static EntityUpdateInfo<RtEntity> Update() =>
        EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(InvoiceType, InvoiceId),
            new RtEntity(InvoiceType, InvoiceId, new Dictionary<string, object?> { ["Title"] = "changed" }));

    private static EntityUpdateInfo<RtEntity> Delete() =>
        EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(InvoiceType, InvoiceId));

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) Prepare<TConfig>(TConfig config,
        EntityUpdateInfo<RtEntity> update) where TConfig : class, INodeConfiguration
    {
        var prepared = PrepareTest(config);
        A.CallTo(() => prepared.DataContext.Get<List<EntityUpdateInfo<RtEntity>>>(DataPath))
            .Returns([update]);
        return prepared;
    }

    private void GivenTheEngineRefusesV1(Exception refusal) =>
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<OperationResult>._))
            .ThrowsAsync(refusal);

    private void GivenTheEngineRefusesV2(Exception refusal) =>
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .ThrowsAsync(refusal);

    private async Task<ReleasedDocumentWriteRefusedException> AssertNodeFailsAsync(Func<Task> run,
        Func<bool> nextCalled, string expectedCode)
    {
        var refused = await Assert.ThrowsAsync<ReleasedDocumentWriteRefusedException>(run);
        Assert.Equal(expectedCode, refused.ErrorCode);
        Assert.Contains($": {expectedCode}: ", refused.Message, StringComparison.Ordinal);
        Assert.False(nextCalled(), "the pipeline must not go on after the refused write");
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => Session.CommitTransactionAsync()).MustNotHaveHappened();
        return refused;
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ApplyChangesV1_SystemSession_FailsWithTheEngineCode(string operation)
    {
        var config = new ApplyChangesNodeConfiguration { Path = DataPath };
        var (dataContext, nodeContext, next) = Prepare(config, operation == "delete" ? Delete() : Update());
        var nextCalled = false;
        GivenTheEngineRefusesV1(Locked(operation));
        var node = new ApplyChangesNode((_, _) =>
        {
            nextCalled = true;
            return next(dataContext, nodeContext);
        }, EtlContext, ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

        var refused = await AssertNodeFailsAsync(() => node.ProcessObjectAsync(dataContext, nodeContext),
            () => nextCalled, ReleasedDocumentLockedException.ErrorCode);

        AssertSystemSessionOpened();
        Assert.IsType<ReleasedDocumentLockedException>(refused.InnerException);
        Assert.Contains(InvoiceId.ToString(), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller, "update")]
    [InlineData(NodeExecutionIdentity.Caller, "delete")]
    [InlineData(NodeExecutionIdentity.ServiceAccount, "update")]
    [InlineData(NodeExecutionIdentity.ServiceAccount, "delete")]
    [InlineData(NodeExecutionIdentity.System, "update")]
    [InlineData(NodeExecutionIdentity.System, "delete")]
    public async Task ApplyChangesV2_EveryIdentity_FailsWithTheEngineCode(NodeExecutionIdentity identity,
        string operation)
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = DataPath, Identity = identity };
        var (dataContext, nodeContext, next) = Prepare(config, operation == "delete" ? Delete() : Update());
        var nextCalled = false;
        GivenTheEngineRefusesV2(Locked(operation));
        var node = new ApplyChangesNode2((_, _) =>
        {
            nextCalled = true;
            return next(dataContext, nodeContext);
        }, EtlContext, ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

        await AssertNodeFailsAsync(() => node.ProcessObjectAsync(dataContext, nodeContext), () => nextCalled,
            ReleasedDocumentLockedException.ErrorCode);

        switch (identity)
        {
            case NodeExecutionIdentity.Caller:
                AssertScopedSessionOpened();
                break;
            case NodeExecutionIdentity.ServiceAccount:
                AssertServiceAccountSessionOpened();
                break;
            default:
                AssertSystemSessionOpened();
                break;
        }
    }

    [Fact]
    public async Task ApplyChangesV2_StornoRefusal_FailsWithItsCode()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = DataPath };
        var (dataContext, nodeContext, next) = Prepare(config, Update());
        GivenTheEngineRefusesV2(StornoRefused());
        var node = new ApplyChangesNode2(next, EtlContext, ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

        await AssertNodeFailsAsync(() => node.ProcessObjectAsync(dataContext, nodeContext), nextCalled: () => false,
            StornoRefusedException.ErrorCode);
    }

    [Fact]
    public async Task AnotherError_IsNotTurnedIntoAReleasedDocumentRefusal()
    {
        var config = new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = DataPath };
        var (dataContext, nodeContext, next) = Prepare(config, Update());
        GivenTheEngineRefusesV2(new InvalidOperationException("disk full"));
        var node = new ApplyChangesNode2(next, EtlContext, ReplacedCreateTestSupport.LoadedCacheWithoutReplacedTypes());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("disk full", failure.Message);
    }
}
