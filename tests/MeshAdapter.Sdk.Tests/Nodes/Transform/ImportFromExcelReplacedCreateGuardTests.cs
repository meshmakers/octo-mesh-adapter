using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using static MeshAdapter.Sdk.Tests.Helpers.ReplacedCreateTestSupport;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     AB#7463 — ImportFromExcel refuses to insert nodes of a type whose CK method replaces the generic create,
///     unless it runs as that method's own pipeline run. The node has no dry-run branch, so the refusal in front of
///     the write holds for dry runs as well.
/// </summary>
public class ImportFromExcelReplacedCreateGuardTests : SessionNodeTestBase
{
    private const string TreeNodeType = "Basic/TreeNode";
    private static readonly string RootId = OctoObjectId.GenerateNewId().ToString();

    private static readonly IPipelineExecutionMode DryRun = new DefaultPipelineExecutionMode { IsDryRun = true };

    public ImportFromExcelReplacedCreateGuardTests()
    {
        GivenSystemSessionIsExpected();
        A.CallTo(() => TenantRepository.CreateTransientRtEntityByRtCkIdAsync(A<RtCkId<CkTypeId>>._))
            .ReturnsLazily((RtCkId<CkTypeId> id) => Task.FromResult(new RtEntity(id, OctoObjectId.GenerateNewId())));
    }

    private async Task Import(ICkCacheService cache, IPipelineExecutionMode? mode = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(new ImportFromExcelNodeConfiguration(),
            executionMode: mode);
        A.CallTo(() => dataContext.Get<string>("$.body.importType")).Returns("TreePath");
        A.CallTo(() => dataContext.Get<string>("$.body.treeModelRootRtId")).Returns(RootId);
        A.CallTo(() => dataContext.Get<JsonArray>("$.body.columns")).Returns(
            (JsonArray)JsonNode.Parse("""[{ "attributePath": "name", "columnIndex": 0 }]""")!);
        A.CallTo(() => dataContext.Get<JsonArray>("$.body.data")).Returns(
            (JsonArray)JsonNode.Parse("""[["Plant/Hall"]]""")!);

        var node = new ImportFromExcelNode(next, cache, A.Fake<IWellKnownNameLoader>(), EtlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    private void AssertNothingWritten()
    {
        AssertNoSessionOpened();
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustNotHaveHappened();
    }

    [Fact]
    public async Task InsertOfReplacedType_WithoutMarker_IsRefused_AndNothingIsWritten()
    {
        ConfigureRun(EtlContext, null);

        var ex = await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            Import(CkCache(TreeNodeType)));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task InsertOfReplacedType_InDryRun_IsRefused()
    {
        ConfigureRun(EtlContext, null);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            Import(CkCache(TreeNodeType), DryRun));

        AssertNothingWritten();
    }

    [Fact]
    public async Task InsertOfReplacedType_WithMarkerOfAnotherMethod_IsRefused()
    {
        ConfigureRun(EtlContext, OwnRunMarker() with { MethodId = "Test-1.0.0/Account/Other-1" });

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() => Import(CkCache(TreeNodeType)));

        AssertNothingWritten();
    }

    [Fact]
    public async Task InsertOfReplacedType_WithMatchingMarker_IsWritten()
    {
        // The marker's method must be the one replacing Basic/TreeNode in this cache.
        var cache = CkCache(TreeNodeType);
        ConfigureRun(EtlContext, OwnRunMarker());

        await Import(cache);

        AssertSystemSessionOpenedSynchronously();
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task InsertOfPlainType_IsNotAffected()
    {
        ConfigureRun(EtlContext, null);

        // Only Test/Account is flagged; Basic/TreeNode is not even in the cache.
        await Import(CkCache());

        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustHaveHappenedOnceExactly();
    }
}
