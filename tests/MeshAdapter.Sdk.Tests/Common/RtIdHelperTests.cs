using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Common;

public class RtIdHelperTests
{
    private static readonly OctoObjectId TestRtId = new("000000000000000000000001");

    private readonly IDataContext _dataContext;
    private readonly INodeContext _nodeContext;

    public RtIdHelperTests()
    {
        _dataContext = A.Fake<IDataContext>();
        _nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => _dataContext.Current).Returns(new JObject());
    }

    [Fact]
    public void TryResolveRtId_DirectValue_ReturnsTrueWithValue()
    {
        var success = RtIdHelper.TryResolveRtId(TestRtId, null, _dataContext, _nodeContext, out var resolved);

        Assert.True(success);
        Assert.Equal(TestRtId, resolved);
    }

    [Fact]
    public void TryResolveRtId_PathResolution_ReturnsTrueWithValue()
    {
        const string path = "$.rtId";
        A.CallTo(() => _dataContext.GetComplexObjectByPath<OctoObjectId?>(path, A<JsonSerializer>._))
            .Returns(TestRtId);

        var success = RtIdHelper.TryResolveRtId(null, path, _dataContext, _nodeContext, out var resolved);

        Assert.True(success);
        Assert.Equal(TestRtId, resolved);
    }

    [Fact]
    public void TryResolveRtId_BothNull_ReturnsFalse()
    {
        var success = RtIdHelper.TryResolveRtId(null, null, _dataContext, _nodeContext, out var resolved);

        Assert.False(success);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveRtId_PathResolvesToNull_ReturnsFalse()
    {
        const string path = "$.rtId";
        A.CallTo(() => _dataContext.GetComplexObjectByPath<OctoObjectId?>(path, A<JsonSerializer>._))
            .Returns(null);

        var success = RtIdHelper.TryResolveRtId(null, path, _dataContext, _nodeContext, out var resolved);

        Assert.False(success);
        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveRtId_DataContextCurrentNull_ThrowsException()
    {
        const string path = "$.rtId";
        A.CallTo(() => _dataContext.Current).Returns(null);

        Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => RtIdHelper.TryResolveRtId(null, path, _dataContext, _nodeContext, out _));
    }
}
