using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// The origin an archive load node writes (AB#5356, AB#7593, AB#7596): validated once per execution of the
/// node, then applied to every row. A manual row records who wrote it as resolved by the platform — the
/// verified user caller, else the pipeline's service identity — never a value from the data.
/// </summary>
internal sealed class ArchiveWriteOriginStamp
{
    private ArchiveWriteOriginStamp(ArchiveRowOrigin origin, string? writer, string? reason)
    {
        Origin = origin;
        Writer = writer;
        Reason = reason;
    }

    /// <summary>The origin of every row the node writes.</summary>
    public ArchiveRowOrigin Origin { get; }

    /// <summary>The writer of a manual row; <c>null</c> for automated rows.</summary>
    public string? Writer { get; }

    /// <summary>The static reason of a manual row, if configured.</summary>
    public string? Reason { get; }

    /// <summary>Validates the node's origin settings against the execution and resolves the writer.</summary>
    /// <param name="nodeName">Node name for the error message.</param>
    /// <param name="origin">Configured origin.</param>
    /// <param name="manualReason">Configured static reason.</param>
    /// <param name="reasonAttributePath">Configured per-row reason attribute, if the node supports one.</param>
    /// <param name="etlContext">The execution, which knows its verified caller and its service identity.</param>
    /// <remarks>
    /// The writer of a manual row is, in this order: the verified user caller's subject id; the client id of
    /// the service identity the pipeline runs under (a file-based manual import has no user caller); the
    /// pipeline itself (<c>pipeline:&lt;rtId&gt;</c>) when the execution acts as the system context. All three
    /// are resolved by the platform, never read from the data, so the writer is never empty.
    /// </remarks>
    public static async Task<ArchiveWriteOriginStamp> ResolveAsync(string nodeName, ArchiveWriteOrigin origin, string? manualReason,
        string? reasonAttributePath, IMeshEtlContext etlContext)
    {
        if (origin != ArchiveWriteOrigin.Manual)
        {
            if (!string.IsNullOrEmpty(manualReason) || !string.IsNullOrEmpty(reasonAttributePath))
            {
                throw new InvalidOperationException(
                    $"{nodeName}: a manual reason is only recorded for origin Manual; set 'origin: Manual' or remove the reason.");
            }

            return new ArchiveWriteOriginStamp(ArchiveRowOrigin.Automated, null, null);
        }

        var writer = etlContext.VerifiedPrincipal?.SubjectId;
        if (string.IsNullOrWhiteSpace(writer))
        {
            writer = await etlContext.GetServiceIdentitySubjectIdAsync();
        }

        if (string.IsNullOrWhiteSpace(writer))
        {
            writer = $"pipeline:{etlContext.PipelineRtEntityId.RtId}";
        }

        return new ArchiveWriteOriginStamp(ArchiveRowOrigin.Manual, writer,
            string.IsNullOrEmpty(manualReason) ? null : manualReason);
    }

    /// <summary>
    /// The reason of one row: the per-row value popped from <paramref name="attributes" /> when the node names
    /// an attribute for it and the row carries a non-empty string there, else the static reason. The attribute
    /// is removed in every case so it never surfaces as a user column.
    /// </summary>
    public string? ReasonFor(IDictionary<string, object?> attributes, string? reasonAttributePath)
    {
        if (string.IsNullOrEmpty(reasonAttributePath) || !attributes.Remove(reasonAttributePath, out var value))
        {
            return Reason;
        }

        return value is string s && !string.IsNullOrWhiteSpace(s) ? s : Reason;
    }

    /// <summary>Applies the origin to rows built without one (the series node builds them in the shaper).</summary>
    public List<TimeRangeStreamDataPoint> Apply(IReadOnlyList<TimeRangeStreamDataPoint> points)
    {
        if (Origin == ArchiveRowOrigin.Automated)
        {
            return points as List<TimeRangeStreamDataPoint> ?? points.ToList();
        }

        return points.Select(p => new TimeRangeStreamDataPoint
        {
            RtId = p.RtId,
            CkTypeId = p.CkTypeId,
            From = p.From,
            To = p.To,
            RtWellKnownName = p.RtWellKnownName,
            Attributes = p.Attributes,
            Origin = Origin,
            ManualWrittenBy = Writer,
            ManualReason = Reason
        }).ToList();
    }
}
