using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Node object for converting an array to a Markdown table
/// </summary>
/// <param name="next"></param>
[NodeConfiguration(typeof(QueryResultToMarkdownTableNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class QueryResultToMarkdownTableNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<QueryResultToMarkdownTableNodeConfiguration>();

        var queryResult = dataContext.GetComplexObjectByPath<QueryResult>(c.Path);
        if (queryResult == null)
        {
            nodeContext.Error("No value found");
            return;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("| " + string.Join(" | ", queryResult.Columns.Select(column => column.Header)) + " |");
        stringBuilder.AppendLine("| " + string.Join(" | ", queryResult.Columns.Select(_ => "---")) + " |");
        foreach (var row in queryResult.Rows)
        {
            stringBuilder.AppendLine("| " + string.Join(" | ", row.Values) + " |");
        }

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, stringBuilder.ToString());

        await next(dataContext, nodeContext);
    }
}