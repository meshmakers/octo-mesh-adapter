using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// Pins how an archive write result is reported (AB#7598, AB#7988): automated rows of other CK types are expected
/// when one update list feeds one archive node per archive, so they stay at Debug; every other skip reason warns,
/// manual rows and <c>failOnSkippedRows</c> fail the node, and the counts stay at <c>WriteResultPath</c>.
/// </summary>
public class ArchiveWriteResultReporterTests
{
    private const string NodeName = "SaveStreamDataInArchive";
    private static readonly DateTime From = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly RecordingLogger _logger = new();
    private readonly IDataContext _dataContext = A.Fake<IDataContext>();
    private readonly INodeContext _nodeContext;

    public ArchiveWriteResultReporterTests()
    {
        _nodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), _logger,
            "SaveStreamDataInArchive", new SaveStreamDataInArchiveNodeConfiguration { ArchiveRtId = "65d5c447b420da3fb12381bc" }, null, null);
    }

    private static StreamDataSkippedRow Row(ArchiveRowOrigin origin, StreamDataSkipReason reason) =>
        new(OctoObjectId.GenerateNewId(), From, null, origin, reason);

    private void Report(StreamDataWriteResult result, bool failOnSkippedRows = false, string? writeResultPath = null) =>
        ArchiveWriteResultReporter.Report(NodeName, result, writeResultPath, failOnSkippedRows, _dataContext, _nodeContext);

    [Fact]
    public void OnlyAutomatedCkTypeMismatch_LogsDebug_AndNoWarning()
    {
        Report(new StreamDataWriteResult(3,
        [
            Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch),
            Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch)
        ]));

        Assert.Empty(_logger.Warnings);
        var debug = Assert.Single(_logger.Debugs);
        Assert.Contains("stored 3 row(s), 2 not stored", debug);
        Assert.Contains("CkTypeMismatch 2", debug);
    }

    [Theory]
    [InlineData(StreamDataSkipReason.LowerPrecedence)]
    [InlineData(StreamDataSkipReason.ManualValueProtected)]
    public void CkTypeMismatchMixedWithAnotherReason_Warns_AndListsAllReasons(StreamDataSkipReason other)
    {
        Report(new StreamDataWriteResult(0,
        [
            Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch),
            Row(ArchiveRowOrigin.Automated, other)
        ]));

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains("CkTypeMismatch 1", warning);
        Assert.Contains($"{other} 1", warning);
        Assert.Empty(_logger.Debugs);
    }

    [Fact]
    public void OnlyAnotherReason_Warns()
    {
        Report(new StreamDataWriteResult(0, [Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.LowerPrecedence)]));

        Assert.Contains("LowerPrecedence 1", Assert.Single(_logger.Warnings));
    }

    [Fact]
    public void ManualCkTypeMismatch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Report(new StreamDataWriteResult(0, [Row(ArchiveRowOrigin.Manual, StreamDataSkipReason.CkTypeMismatch)])));

        Assert.Contains("manual rows", ex.Message);
        Assert.Empty(_logger.Warnings);
        Assert.Empty(_logger.Debugs);
    }

    [Fact]
    public void FailOnSkippedRows_ThrowsForAutomatedCkTypeMismatch()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Report(new StreamDataWriteResult(1, [Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch)]),
                failOnSkippedRows: true));

        Assert.Contains("failOnSkippedRows", ex.Message);
        Assert.Contains("CkTypeMismatch 1", ex.Message);
    }

    [Fact]
    public void NothingSkipped_LogsNothing()
    {
        Report(new StreamDataWriteResult(5, []));

        Assert.Empty(_logger.Warnings);
        Assert.Empty(_logger.Debugs);
    }

    [Fact]
    public void WriteResultPath_KeepsTheCountsOfExpectedSkips()
    {
        JsonObject? written = null;
        A.CallTo(() => _dataContext.Set("$.writeResult", A<JsonObject?>._))
            .Invokes((string _, JsonObject? value) => written = value);

        Report(new StreamDataWriteResult(2,
        [
            Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch),
            Row(ArchiveRowOrigin.Automated, StreamDataSkipReason.CkTypeMismatch)
        ]), writeResultPath: "$.writeResult");

        Assert.NotNull(written);
        Assert.Equal(2, written!["stored"]!.GetValue<int>());
        Assert.Equal(2, written["skipped"]!.GetValue<int>());
        Assert.Equal(2, written["skippedByReason"]!["CkTypeMismatch"]!.GetValue<int>());
    }

    /// <summary>Records the messages per level so a test can assert which level a report used.</summary>
    private sealed class RecordingLogger : IPipelineLogger
    {
        public List<string> Debugs { get; } = new();
        public List<string> Warnings { get; } = new();

        public void Debug(string nodeId, string nodePath, string message, params object[] args) => Debugs.Add(message);
        public void Info(string nodeId, string nodePath, string message, params object[] args) { }
        public void Warning(string nodeId, string nodePath, string message, params object[] args) => Warnings.Add(message);
        public void Error(string nodeId, string nodePath, string message, params object[] args) { }
        public void Error(string nodeId, string nodePath, Exception exception, string message, params object[] args) { }
    }
}
