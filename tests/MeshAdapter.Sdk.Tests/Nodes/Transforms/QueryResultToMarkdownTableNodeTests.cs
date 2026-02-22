using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class QueryResultToMarkdownTableNodeTests
{
    private const string DataPath = "$.queryResult";
    private const string TargetPath = "$.markdown";

    private (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        QueryResultToMarkdownTableNodeConfiguration config, JToken? testData = null)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            "QueryResultToMarkdownTable",
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var queryResult = CreateQueryResult(
            ["Name", "Value"],
            [["Item1", 42], ["Item2", 99]]);

        A.CallTo(() => dataContext.GetComplexObjectByPath<QueryResult>(DataPath))
            .Returns(queryResult);

        string? capturedMarkdown = null;
        A.CallTo(() => dataContext.SetValueByPath(
                TargetPath,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<string>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, string value) =>
                capturedMarkdown = value);

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
        var (dataContext, nodeContext, next) = PrepareTest(config);

        A.CallTo(() => dataContext.GetComplexObjectByPath<QueryResult>(DataPath))
            .Returns(null);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithData_CallsNext()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var queryResult = CreateQueryResult(["Col"], [["Val"]]);
        A.CallTo(() => dataContext.GetComplexObjectByPath<QueryResult>(DataPath))
            .Returns(queryResult);

        var node = new QueryResultToMarkdownTableNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyRows_GeneratesHeaderOnly()
    {
        var config = new QueryResultToMarkdownTableNodeConfiguration { Path = DataPath, TargetPath = TargetPath };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var queryResult = CreateQueryResult(["Name", "Value"], []);
        A.CallTo(() => dataContext.GetComplexObjectByPath<QueryResult>(DataPath))
            .Returns(queryResult);

        string? capturedMarkdown = null;
        A.CallTo(() => dataContext.SetValueByPath(
                TargetPath,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._,
                A<string>._))
            .Invokes((string _, DocumentModes _, ValueKinds _, TargetValueWriteModes _, string value) =>
                capturedMarkdown = value);

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
