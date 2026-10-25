using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using static MeshAdapter.Sdk.Tests.Helpers.ReplacedCreateTestSupport;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
///     AB#7463 — ApplyChanges@1 and @2 refuse inserts of a type whose CK method replaces the generic create,
///     except in the replacing method's own pipeline run; dry runs included (the refusal comes before the
///     dry-run branch). The guard itself is covered by <c>GenericCreateGuardTests</c> (AB#7462).
/// </summary>
public class ApplyChangesReplacedCreateGuardTests : SessionNodeTestBase
{
    private const string V1Path = "$.updateInfos";
    private const string V2Path = "$.entityUpdates";

    private static readonly IPipelineExecutionMode DryRun = new DefaultPipelineExecutionMode { IsDryRun = true };

    public ApplyChangesReplacedCreateGuardTests()
    {
        GivenSystemSessionIsExpected();
    }

    private void Run(ExecutePipelineMethodInvocation? marker) => ConfigureRun(EtlContext, marker);

    private async Task RunV1(List<EntityUpdateInfo<RtEntity>> data, IPipelineExecutionMode? mode = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(new ApplyChangesNodeConfiguration { Path = V1Path },
            executionMode: mode);
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(V1Path)).Returns(data);
        await new ApplyChangesNode(next, EtlContext, CkCache()).ProcessObjectAsync(dataContext, nodeContext);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    private async Task RunV2(List<EntityUpdateInfo<RtEntity>> data, IPipelineExecutionMode? mode = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(
            new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = V2Path }, executionMode: mode);
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(V2Path)).Returns(data);
        await new ApplyChangesNode2(next, EtlContext, CkCache()).ProcessObjectAsync(dataContext, nodeContext);
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    private void AssertNothingWritten()
    {
        AssertNoSessionOpened();
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustNotHaveHappened();
    }

    private void AssertWritten(int times = 1)
    {
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync")
            .MustHaveHappened(times, Times.Exactly);
    }

    // ---------------------------------------------------------------- ApplyChanges@1

    [Fact]
    public async Task V1_InsertOfReplacedType_WithoutMarker_IsRefused_AndNothingIsWritten()
    {
        Run(null);

        var ex = await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV1([Insert(ReplacedType)]));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task V1_InsertOfReplacedType_InDryRun_IsRefusedBeforeTheDryRunBranch()
    {
        Run(null);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV1([Insert(ReplacedType)], DryRun));

        AssertNothingWritten();
    }

    [Fact]
    public async Task V1_InsertOfReplacedType_WithMatchingMarker_IsWritten()
    {
        Run(OwnRunMarker());

        await RunV1([Insert(ReplacedType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V1_InsertOfReplacedType_WithMarkerOfAnotherPipeline_IsRefused()
    {
        Run(OwnRunMarker() with { PipelineRtId = OctoObjectId.GenerateNewId().ToString() });

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() => RunV1([Insert(ReplacedType)]));

        AssertNothingWritten();
    }

    [Fact]
    public async Task V1_InsertOfPlainType_IsNotAffected()
    {
        Run(null);

        await RunV1([Insert(PlainType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V1_UpdateAndReplaceOfReplacedType_AreNotAffected()
    {
        Run(null);

        await RunV1([Update(ReplacedType), Replace(ReplacedType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V1_MixedBatch_IsRefusedAsAWhole()
    {
        Run(null);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV1([Insert(PlainType), Update(PlainType), Insert(ReplacedType)]));

        AssertNothingWritten();
    }

    // ---------------------------------------------------------------- ApplyChanges@2

    [Fact]
    public async Task V2_InsertOfReplacedType_WithoutMarker_IsRefused_AndNothingIsWritten()
    {
        Run(null);

        var ex = await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV2([Insert(ReplacedType)]));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        AssertNothingWritten();
    }

    [Fact]
    public async Task V2_InsertOfReplacedType_InDryRun_IsRefusedBeforeTheDryRunBranch()
    {
        Run(null);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV2([Insert(ReplacedType)], DryRun));

        AssertNothingWritten();
    }

    [Fact]
    public async Task V2_InsertOfReplacedType_WithMatchingMarker_IsWritten()
    {
        Run(OwnRunMarker());

        await RunV2([Insert(ReplacedType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V2_InsertOfReplacedType_WithMatchingMarker_InDryRun_IsNotRefused_AndNotWritten()
    {
        Run(OwnRunMarker());

        await RunV2([Insert(ReplacedType)], DryRun);

        AssertNothingWritten();
    }

    [Fact]
    public async Task V2_InsertOfReplacedType_WithMarkerOfAnotherMethod_IsRefused()
    {
        Run(OwnRunMarker() with { MethodId = "Test-1.0.0/Account/Other-1" });

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() => RunV2([Insert(ReplacedType)]));

        AssertNothingWritten();
    }

    [Fact]
    public async Task V2_InsertOfPlainType_IsNotAffected()
    {
        Run(null);

        await RunV2([Insert(PlainType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V2_UpdateAndReplaceOfReplacedType_AreNotAffected()
    {
        Run(null);

        await RunV2([Update(ReplacedType), Replace(ReplacedType)]);

        AssertWritten();
    }

    [Fact]
    public async Task V2_MixedBatch_IsRefusedAsAWhole()
    {
        Run(null);

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunV2([Update(ReplacedType), Insert(ReplacedType)]));

        AssertNothingWritten();
    }
}
