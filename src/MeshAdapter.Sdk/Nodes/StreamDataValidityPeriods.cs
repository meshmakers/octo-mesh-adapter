using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Window = Meshmakers.Octo.Sdk.MeshAdapter.Nodes.StreamDataGapAnalyzer.Window;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Validity periods per series for <c>DetectStreamDataGaps@1</c> (AB#9589): reading them from the
/// configuration or the pipeline data, and turning them into the effective periods a series is
/// checked in — its periods merged and cut to the checked period. Every period is half-open
/// [from, to), like the checked period itself.
/// </summary>
internal static class StreamDataValidityPeriods
{
    /// <summary>One validity period as configured, in UTC; <see cref="To" /> null is an open end.</summary>
    internal readonly record struct Period(DateTime From, DateTime? To);

    /// <summary>
    /// The validity periods per series, or null when neither <paramref name="configured" /> nor
    /// <paramref name="path" /> is set (the node then checks the whole period for every series).
    /// The inline list takes precedence. A path resolving to no entry gives an empty map and a
    /// warning naming the path, so every series counts as without a period. An entry without a
    /// valid runtime id or start, with an unreadable timestamp, or with from &gt;= to fails the node
    /// naming the entry.
    /// </summary>
    internal static Dictionary<OctoObjectId, List<Period>>? Resolve(ICollection<ValidityPeriodDto>? configured,
        string? path, IDataContext dataContext, INodeContext nodeContext, string propertyName, string pathPropertyName)
    {
        if (configured is { Count: > 0 })
        {
            var result = new Dictionary<OctoObjectId, List<Period>>();
            var index = 0;
            foreach (var entry in configured)
            {
                var name = $"{propertyName}[{index++}]";
                Add(result, name, entry.RtId, StreamDataNodeHelpers.ToUtc(entry.From),
                    StreamDataNodeHelpers.ToUtcOrNull(entry.To), nodeContext);
            }

            return result;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fromPath = new Dictionary<OctoObjectId, List<Period>>();
        var entries = 0;
        foreach (var match in dataContext.SelectMatches(path))
        {
            if (match.GetKind("$") == DataKind.Array)
            {
                var length = match.Length("$");
                for (var i = 0; i < length; i++)
                {
                    AddFromData(fromPath, $"{pathPropertyName} '{path}' entry {entries++}", match, $"$[{i}]",
                        nodeContext);
                }
            }
            else if (match.GetKind("$") == DataKind.Object)
            {
                AddFromData(fromPath, $"{pathPropertyName} '{path}' entry {entries++}", match, "$", nodeContext);
            }
            else if (match.GetKind("$") is not (DataKind.Null or DataKind.Undefined))
            {
                throw MeshAdapterPipelineExecutionException.DetectGapsValidityEntryInvalid(nodeContext,
                    $"{pathPropertyName} '{path}' entry {entries}", "the value is not an object or an array of objects.");
            }
        }

        if (entries == 0)
        {
            nodeContext.Warning(
                $"{pathPropertyName} '{path}' resolved to no validity period; no series is checked or reported.");
        }

        return fromPath;
    }

    /// <summary>
    /// The periods a series is checked in: its periods cut to [<paramref name="from" />,
    /// <paramref name="to" />), sorted, with overlapping and touching ones merged. Empty when no period
    /// reaches into the checked period.
    /// </summary>
    internal static List<Window> Effective(IEnumerable<Period> periods, DateTime from, DateTime to)
        => Merge(periods.Select(p => new Window(p.From < from ? from : p.From,
            p.To is { } end && end < to ? end : to)));

    /// <summary>Sorts the windows, drops empty ones and merges what overlaps or touches.</summary>
    internal static List<Window> Merge(IEnumerable<Window> windows)
    {
        var merged = new List<Window>();
        foreach (var window in windows.Where(w => w.End > w.Start).OrderBy(w => w.Start).ThenBy(w => w.End))
        {
            if (merged.Count != 0 && window.Start <= merged[^1].End)
            {
                if (window.End > merged[^1].End)
                {
                    merged[^1] = merged[^1] with { End = window.End };
                }

                continue;
            }

            merged.Add(window);
        }

        return merged;
    }

    /// <summary>True when the window [start, end) overlaps one of the sorted, merged periods.</summary>
    internal static bool Overlaps(IReadOnlyList<Window> periods, DateTime start, DateTime end)
        => periods.Any(p => p.Start < end && p.End > start);

    private static void AddFromData(Dictionary<OctoObjectId, List<Period>> target, string name,
        IDataContext match, string prefix, INodeContext nodeContext)
    {
        if (match.GetKind(prefix) != DataKind.Object)
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsValidityEntryInvalid(nodeContext, name,
                "the entry is not an object.");
        }

        var rtId = match.GetValue($"{prefix}.rtId")?.ToString();
        var from = ToInstant(match.GetValue($"{prefix}.from"), name, "from", nodeContext)
                   ?? throw MeshAdapterPipelineExecutionException.DetectGapsValidityEntryInvalid(nodeContext, name,
                       "'from' is missing.");
        var to = ToInstant(match.GetValue($"{prefix}.to"), name, "to", nodeContext);

        Add(target, name, rtId, from, to, nodeContext);
    }

    private static void Add(Dictionary<OctoObjectId, List<Period>> target, string name, string? rtIdText,
        DateTime from, DateTime? to, INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(rtIdText) || !OctoObjectId.TryParse(rtIdText, out var rtId))
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsValidityEntryInvalid(nodeContext, name,
                $"'{rtIdText}' is not a valid runtime id.");
        }

        if (to is { } end && from >= end)
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsValidityPeriodInvalid(nodeContext,
                $"{name} (rtId '{rtId}')", from, end);
        }

        if (!target.TryGetValue(rtId, out var periods))
        {
            periods = [];
            target[rtId] = periods;
        }

        periods.Add(new Period(from, to));
    }

    private static DateTime? ToInstant(object? value, string name, string field, INodeContext nodeContext)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => StreamDataNodeHelpers.ToUtc(dateTime),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => throw MeshAdapterPipelineExecutionException.DetectGapsValidityEntryInvalid(nodeContext, name,
                $"'{field}' value '{value}' cannot be read as a date/time.")
        };
    }
}
