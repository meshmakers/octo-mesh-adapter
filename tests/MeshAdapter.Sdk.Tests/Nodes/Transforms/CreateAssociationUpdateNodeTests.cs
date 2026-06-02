using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class CreateAssociationUpdateNodeTests : NodeTestBase
{
    [Fact]
    public async Task ProcessObjectAsync_WithCreateKind_SetsUpdateInfoOnDataContext()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginRtId = new OctoObjectId("000000000000000000000001"),
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetRtId = new OctoObjectId("000000000000000000000002"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            UpdateKind = AssociationUpdateKind.Create,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.result",
                A<AssociationUpdateInfo?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithDeleteKind_SetsUpdateInfoOnDataContext()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginRtId = new OctoObjectId("000000000000000000000001"),
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetRtId = new OctoObjectId("000000000000000000000002"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            UpdateKind = AssociationUpdateKind.Delete,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => dataContext.Set(
                "$.result",
                A<AssociationUpdateInfo?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoOriginRtIdAndNoPath_Throws()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetRtId = new OctoObjectId("000000000000000000000002"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            UpdateKind = AssociationUpdateKind.Create,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoTargetRtIdAndNoPath_Throws()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginRtId = new OctoObjectId("000000000000000000000001"),
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            UpdateKind = AssociationUpdateKind.Create,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNoUpdateKindAndNoPath_Throws()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginRtId = new OctoObjectId("000000000000000000000001"),
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetRtId = new OctoObjectId("000000000000000000000002"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);

        await Assert.ThrowsAsync<MeshAdapterPipelineExecutionException>(
            () => node.ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task ProcessObjectAsync_CallsNext()
    {
        var config = new CreateAssociationUpdateNodeConfiguration
        {
            OriginRtId = new OctoObjectId("000000000000000000000001"),
            OriginCkTypeId = new RtCkId<CkTypeId>("TestModel/OriginType"),
            TargetRtId = new OctoObjectId("000000000000000000000002"),
            TargetCkTypeId = new RtCkId<CkTypeId>("TestModel/TargetType"),
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("TestModel/TestRole"),
            UpdateKind = AssociationUpdateKind.Create,
            TargetPath = "$.result"
        };
        var (dataContext, nodeContext, next) = PrepareTest<CreateAssociationUpdateNodeConfiguration>(config);

        var node = new CreateAssociationUpdateNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }
}
