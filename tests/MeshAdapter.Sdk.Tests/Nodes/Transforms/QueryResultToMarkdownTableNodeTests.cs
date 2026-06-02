using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class QueryResultToMarkdownTableNodeTests : NodeTestBase
{
    private const string DataPath = "$.queryResult";
    private const string TargetPath = "$.markdown";

    private static QueryResult CreateQueryResult(string[] headers, List<object?[]> rows)
    {
        var result = new QueryResult();
        foreach (var header in headers)
        {
            result.Columns.Add(new QueryResultColumns { Header = header });
        }

        foreach (var row in rows)
        {
            var qr = new QueryResultRow
            {
                RtId = new OctoObjectId("000000000000000000000001"),
                CkTypeId = new RtCkId<CkTypeId>("TestModel/TestType"),
                Values = row.ToList()
            };
            result.Rows.Add(qr);
        }

        return result;
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_GeneratesMarkdownTable()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<QueryResultToMarkdownTableNodeConfiguration>(config);

        var queryResult = CreateQueryResult(
            ["Name", "Value"],
            [["Item1", 42], ["Item2", 99]]);

        A.CallTo(() => dataContext.Get<QueryResult>(DataPath))
            .Returns(queryResult);

        string? capturedMarkdown = null;
        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<string?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedMarkdown = value);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedMarkdown);
        Assert.Contains("| Name | Value |", capturedMarkdown);
        Assert.Contains("| --- | --- |", capturedMarkdown);
        Assert.Contains("| Item1 | 42 |", capturedMarkdown);
        Assert.Contains("| Item2 | 99 |", capturedMarkdown);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNullData_DoesNotCallNext()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<QueryResultToMarkdownTableNodeConfiguration>(config);

        A.CallTo(() => dataContext.Get<QueryResult>(DataPath))
            .Returns((QueryResult?)null);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextNotCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<QueryResultToMarkdownTableNodeConfiguration>(config);

        var queryResult = CreateQueryResult(["Col"], [["Val"]]);
        A.CallTo(() => dataContext.Get<QueryResult>(DataPath))
            .Returns(queryResult);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        VerifyNextCalled(next, dataContext, nodeContext);
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyRows_GeneratesHeaderOnly()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest<QueryResultToMarkdownTableNodeConfiguration>(config);

        var queryResult = CreateQueryResult(["Name", "Value"], []);
        A.CallTo(() => dataContext.Get<QueryResult>(DataPath))
            .Returns(queryResult);

        string? capturedMarkdown = null;
        A.CallTo(() => dataContext.Set(
                TargetPath,
                A<string?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, string? value, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedMarkdown = value);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedMarkdown);
        Assert.Contains("| Name | Value |", capturedMarkdown);
        Assert.Contains("| --- | --- |", capturedMarkdown);
        // No data rows
        var lines = capturedMarkdown!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }
}
