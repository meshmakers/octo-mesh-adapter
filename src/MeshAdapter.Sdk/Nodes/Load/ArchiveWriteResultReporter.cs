using System.Text.Json.Nodes;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Surfaces what an archive write stored (AB#7598): a warning naming the rows not stored per reason, the result
/// at the node's <c>WriteResultPath</c>, and a failure when the node asks for it. No pipeline reports success for
/// a value that was not stored without saying so.
/// </summary>
internal static class ArchiveWriteResultReporter
{
    /// <summary>Reports <paramref name="result" /> for the node <paramref name="nodeName" />.</summary>
    public static void Report(string nodeName, StreamDataWriteResult result, string? writeResultPath, bool failOnSkippedRows,
        IDataContext dataContext, INodeContext nodeContext)
    {
        var byReason = result.SkippedByReason;
        if (!string.IsNullOrWhiteSpace(writeResultPath))
        {
            var reasons = new JsonObject();
            foreach (var (reason, count) in byReason)
            {
                reasons[reason.ToString()] = count;
            }

            dataContext.Set(writeResultPath, new JsonObject
            {
                ["stored"] = result.Stored,
                ["skipped"] = result.SkippedCount,
                ["skippedByReason"] = reasons
            });
        }

        if (result.SkippedCount == 0)
        {
            return;
        }

        var summary = $"{nodeName}: stored {result.Stored} row(s), {result.SkippedCount} not stored " +
                      $"({string.Join(", ", byReason.Select(kv => $"{kv.Key} {kv.Value}"))}).";
        // A manual row that could not be stored always fails the node, whatever failOnSkippedRows says. The repository
        // already throws for it; this keeps the node contract even if a store only reports it.
        var manualSkipped = result.Skipped.Count(r => r.Origin == ArchiveRowOrigin.Manual);
        if (manualSkipped > 0)
        {
            throw new InvalidOperationException(summary + $" {manualSkipped} of them are manual rows, which always fail the node.");
        }

        if (failOnSkippedRows)
        {
            throw new InvalidOperationException(summary + " The node is configured with failOnSkippedRows.");
        }

        nodeContext.Warning(summary);
    }
}
