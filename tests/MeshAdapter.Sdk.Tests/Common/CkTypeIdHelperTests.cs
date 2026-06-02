using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace MeshAdapter.Sdk.Tests.Common;

public class CkTypeIdHelperTests
{
    private static readonly RtCkId<CkTypeId> TestCkTypeId = new("TestModel/TestType");

    private readonly IDataContext _dataContext;
    private readonly INodeContext _nodeContext;

    public CkTypeIdHelperTests()
    {
        _dataContext = A.Fake<IDataContext>();
        _nodeContext = A.Fake<INodeContext>();
    }

    #region ResolveRtCkTypeId

    [Fact]
    public void ResolveRtCkTypeId_DirectValue_ReturnsDirectValue()
    {
        var result = CkTypeIdHelper.ResolveRtCkTypeId(TestCkTypeId, null, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveRtCkTypeId_PathResolution_ReturnsResolvedValue()
    {
        const string path = "$.ckTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns("TestModel/TestType");

        var result = CkTypeIdHelper.ResolveRtCkTypeId(null, path, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveRtCkTypeId_BothNull_ThrowsException()
    {
        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveRtCkTypeId(null, null, _dataContext, _nodeContext));
    }

    [Fact]
    public void ResolveRtCkTypeId_PathResolvesToNull_ThrowsException()
    {
        const string path = "$.ckTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns(null);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveRtCkTypeId(null, path, _dataContext, _nodeContext));
    }

    [Fact]
    public void ResolveRtCkTypeId_DirectValueTakesPriorityOverPath()
    {
        const string path = "$.ckTypeId";
        var otherCkTypeId = new RtCkId<CkTypeId>("Other/Type");
        A.CallTo(() => _dataContext.Get<string>(path)).Returns("Other/Type");

        var result = CkTypeIdHelper.ResolveRtCkTypeId(TestCkTypeId, path, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    #endregion

    #region TryResolveRtCkTypeId

    [Fact]
    public void TryResolveRtCkTypeId_DirectValue_ReturnsTrueWithValue()
    {
        var success = CkTypeIdHelper.TryResolveRtCkTypeId(TestCkTypeId, null, _dataContext, out var resolved);

        Assert.True(success);
        Assert.Equal(TestCkTypeId, resolved);
    }

    [Fact]
    public void TryResolveRtCkTypeId_PathResolution_ReturnsTrueWithValue()
    {
        const string path = "$.ckTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns("TestModel/TestType");

        var success = CkTypeIdHelper.TryResolveRtCkTypeId(null, path, _dataContext, out var resolved);

        Assert.True(success);
        Assert.Equal(TestCkTypeId, resolved);
    }

    [Fact]
    public void TryResolveRtCkTypeId_BothNull_ReturnsFalse()
    {
        var success = CkTypeIdHelper.TryResolveRtCkTypeId(null, null, _dataContext, out var resolved);

        Assert.False(success);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveRtCkTypeId_PathResolvesToNull_ReturnsFalse()
    {
        const string path = "$.ckTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns(null);

        var success = CkTypeIdHelper.TryResolveRtCkTypeId(null, path, _dataContext, out var resolved);

        Assert.False(success);
        Assert.Null(resolved);
    }

    #endregion

    #region ResolveOriginCkTypeId

    [Fact]
    public void ResolveOriginCkTypeId_DirectValue_ReturnsDirectValue()
    {
        var result = CkTypeIdHelper.ResolveOriginCkTypeId(TestCkTypeId, null, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveOriginCkTypeId_PathResolution_ReturnsResolvedValue()
    {
        const string path = "$.originCkTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns("TestModel/TestType");

        var result = CkTypeIdHelper.ResolveOriginCkTypeId(null, path, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveOriginCkTypeId_BothNull_ThrowsException()
    {
        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveOriginCkTypeId(null, null, _dataContext, _nodeContext));
    }

    [Fact]
    public void ResolveOriginCkTypeId_PathResolvesToNull_ThrowsException()
    {
        const string path = "$.originCkTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns(null);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveOriginCkTypeId(null, path, _dataContext, _nodeContext));
    }

    #endregion

    #region ResolveTargetCkTypeId

    [Fact]
    public void ResolveTargetCkTypeId_DirectValue_ReturnsDirectValue()
    {
        var result = CkTypeIdHelper.ResolveTargetCkTypeId(TestCkTypeId, null, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveTargetCkTypeId_PathResolution_ReturnsResolvedValue()
    {
        const string path = "$.targetCkTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns("TestModel/TestType");

        var result = CkTypeIdHelper.ResolveTargetCkTypeId(null, path, _dataContext, _nodeContext);

        Assert.Equal(TestCkTypeId, result);
    }

    [Fact]
    public void ResolveTargetCkTypeId_BothNull_ThrowsException()
    {
        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveTargetCkTypeId(null, null, _dataContext, _nodeContext));
    }

    [Fact]
    public void ResolveTargetCkTypeId_PathResolvesToNull_ThrowsException()
    {
        const string path = "$.targetCkTypeId";
        A.CallTo(() => _dataContext.Get<string>(path)).Returns(null);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => CkTypeIdHelper.ResolveTargetCkTypeId(null, path, _dataContext, _nodeContext));
    }

    #endregion
}
