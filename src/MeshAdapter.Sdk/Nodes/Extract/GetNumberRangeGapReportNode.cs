using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Reads the gap report of a number range for one fiscal year (<see cref="ITenantRepository.GetNumberRangeGapReportAsync" />)
/// and writes it to the target path: the missing numbers as intervals, the duplicate numbers with every document, the
/// irregular numbers and the counts. The engine computes the report with the session of the node's identity, so it
/// counts only the documents that identity may read; the node only maps the result.
/// </summary>
[NodeConfiguration(typeof(GetNumberRangeGapReportNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetNumberRangeGapReportNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetNumberRangeGapReportNodeConfiguration>();

        var numberRange = c.NumberRange ?? (c.NumberRangePath == null ? null : dataContext.Get<string>(c.NumberRangePath));
        if (string.IsNullOrWhiteSpace(numberRange))
        {
            throw MeshAdapterPipelineExecutionException.NumberRangeNotSet(nodeContext);
        }

        var fiscalYear = c.FiscalYear ?? (c.FiscalYearPath == null ? null : dataContext.Get<int?>(c.FiscalYearPath));

        // Session identity: config-selected, default Caller - a read of tenant business data, filtered like every
        // generic read.
        // The report counts only the documents this identity may read; as System it counts all of them. No
        // transaction: the report is read-only and may scan a whole fiscal year.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        NumberRangeGapReport? report;
        try
        {
            report = await etlContext.TenantRepository.GetNumberRangeGapReportAsync(session, numberRange, fiscalYear);
        }
        catch (ArgumentException e)
        {
            throw MeshAdapterPipelineExecutionException.NumberRangeGapReportRefused(nodeContext, e);
        }

        if (report == null)
        {
            throw MeshAdapterPipelineExecutionException.NumberRangeNotFound(nodeContext, numberRange);
        }

        dataContext.Set(c.TargetPath, ToJson(report), c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>The report as JSON, with the field names of the GraphQL query <c>runtime.numberRangeGapReport</c>.</summary>
    internal static JsonObject ToJson(NumberRangeGapReport report) => new()
    {
        ["numberRange"] = report.NumberRange,
        ["fiscalYear"] = report.FiscalYear,
        ["startValue"] = report.StartValue,
        ["currentValue"] = report.CurrentValue,
        ["documentCount"] = report.DocumentCount,
        ["missing"] = new JsonArray(report.Missing.Select(g => (JsonNode)new JsonObject
        {
            ["from"] = g.From, ["to"] = g.To, ["fromNumber"] = g.FromNumber, ["toNumber"] = g.ToNumber
        }).ToArray()),
        ["duplicates"] = new JsonArray(report.Duplicates.Select(d => (JsonNode)new JsonObject
        {
            ["sequenceValue"] = d.SequenceValue,
            ["number"] = d.Number,
            ["documents"] = new JsonArray(d.Documents.Select(Document).ToArray())
        }).ToArray()),
        ["irregular"] = new JsonArray(report.Irregular.Select(i => (JsonNode)new JsonObject
        {
            ["document"] = Document(i.Document), ["number"] = i.Number, ["sequenceValue"] = i.SequenceValue
        }).ToArray()),
        ["irregularCount"] = report.IrregularCount
    };

    private static JsonNode Document(RtEntityId id) => new JsonObject
    {
        ["rtId"] = id.RtId.ToString(), ["ckTypeId"] = id.CkTypeId.ToString()
    };
}
