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
/// <remarks>
/// Automated rows skipped only for <see cref="StreamDataSkipReason.CkTypeMismatch" /> are logged at Debug, not as a
/// warning (AB#7988): pipelines deliberately feed one update list to one archive node per archive, and each archive
/// keeps its own CK type, so the rows of the other types are an expected outcome on every run. Their counts stay in
/// the result at <c>WriteResultPath</c>; <c>failOnSkippedRows</c> still fails for them.
/// </remarks>
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

        // Rows of other CK types are the expected result of fanning one update list out to one node per archive
        // (AB#7988); only they, and only for automated rows, stay at Debug. Any other reason keeps the warning.
        var expectedSkips = result.Skipped.Count(r =>
            r.Origin == ArchiveRowOrigin.Automated && r.Reason == StreamDataSkipReason.CkTypeMismatch);
        if (expectedSkips == result.SkippedCount)
        {
            nodeContext.Debug(summary + " Rows of other CK types are expected when one update list feeds one node per archive.");
            return;
        }

        nodeContext.Warning(summary);
    }
}
