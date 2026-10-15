using System.Globalization;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

internal enum NotificationDigestAction
{
    RecordSilently,

    NothingNew,

    Send
}

internal sealed record SentRow(
    [property: JsonPropertyName("rtId")] string RtId,
    [property: JsonPropertyName("sentAt")] DateTime SentAt,
    [property: JsonPropertyName("changedAt")] DateTime ChangedAt);

internal sealed record DeliveryMemory(
    DateTime? ProcessedUntil,
    IReadOnlyList<SentRow> SentRows,
    string? QueryRtId,
    string? SubjectId,
    bool? SkipExistingRows,
    DateTime? LastRunAt);

internal sealed record NotificationPlan(bool RecordOnly, DateTime? ReadFrom, DateTime? ProcessedUntil,
    IReadOnlyList<SentRow> SentRows);

internal sealed record NotificationDigestDecision(
    NotificationDigestAction Action,
    IReadOnlyList<string> NewRtIds,
    DateTime? ProcessedUntil,
    IReadOnlyList<SentRow> SentRows);

internal sealed record NotificationRecipient(string RtId, string? SubjectId, DateTime? CreatedAt);

internal enum NotificationBodyKind
{
    Plain,

    Markup
}

internal sealed record RenderedNotification(string Subject, string Body);

internal sealed class NotificationRenderException(IReadOnlyList<string> unknownPlaceholders)
    : Exception($"The template uses placeholders the query does not provide: {string.Join(", ", unknownPlaceholders.Select(p => "${" + p + "}"))}")
{
    public IReadOnlyList<string> UnknownPlaceholders { get; } = unknownPlaceholders;
}

internal static class NotificationDigest
{
    public const int MaxRowsPerMail = 100;

    public const int RowsPerRun = 10_000;

    public const int MaxSentRows = 10_000;

    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan SentRowRetention = TimeSpan.FromDays(30);

    public static readonly TimeSpan LeaseExpiry = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan IdleRecordInterval = TimeSpan.FromHours(1);

    private const string CountPlaceholder = "count";

    public static DateTime StartOf(DateTime? preferenceCreatedAt, DateTime? deployedAt)
    {
        var created = preferenceCreatedAt ?? DateTime.MinValue;
        var deployed = deployedAt ?? DateTime.MinValue;
        return created > deployed ? created : deployed;
    }

    public static bool IsLeaseHeld(DateTime? runningSince, DateTime now) =>
        runningSince is { } since && now - since < LeaseExpiry;

    public static bool IsBackingOff(string? lastError, DateTime? failedAt, DateTime now,
        DateTime? configurationChangedAt) =>
        !string.IsNullOrEmpty(lastError) && failedAt is { } at && now - at < RetryDelay &&
        !(configurationChangedAt > at);

    public static bool NeedsIdleWrite(NotificationDigestAction action, bool sameRows, string? lastError,
        DateTime? lastRunAt, DateTime now) =>
        action == NotificationDigestAction.RecordSilently || !sameRows || !string.IsNullOrEmpty(lastError) ||
        lastRunAt is not { } ranAt || now - ranAt >= IdleRecordInterval;

    public static NotificationPlan Plan(DeliveryMemory? memory, string? subjectId, bool skipExistingRows,
        string queryRtId, DateTime start, DateTime now)
    {
        var otherRecipient = memory?.SubjectId != null && memory.SubjectId != subjectId;
        var switchedToCatchUp = memory?.SkipExistingRows == true && !skipExistingRows;
        var sentRows = otherRecipient || switchedToCatchUp
            ? []
            : Prune(memory?.SentRows ?? [], ReadFrom(memory?.ProcessedUntil), now);
        var restart = memory?.LastRunAt is not { } lastRunAt || lastRunAt < start || otherRecipient ||
                      memory.QueryRtId != null && memory.QueryRtId != queryRtId ||
                      memory.SkipExistingRows != null && memory.SkipExistingRows != skipExistingRows ||
                      skipExistingRows && memory.ProcessedUntil == null;

        return restart
            ? new NotificationPlan(skipExistingRows, null, null, sentRows)
            : new NotificationPlan(false, ReadFrom(memory!.ProcessedUntil), memory.ProcessedUntil, sentRows);
    }

    public static DateTime? ReadFrom(DateTime? processedUntil) =>
        processedUntil is { } until && until > DateTime.MinValue + Overlap ? until - Overlap : null;

    public static NotificationDigestDecision RecordStart(NotificationPlan plan, DateTime? latestChange, DateTime now)
    {
        var processedUntil = latestChange is { } latest ? latest + Overlap : DateTime.MinValue;
        return new NotificationDigestDecision(NotificationDigestAction.RecordSilently, [], processedUntil,
            Prune(plan.SentRows, ReadFrom(processedUntil), now));
    }

    public static NotificationDigestDecision Decide(NotificationPlan plan, IReadOnlyList<NotificationCandidate> page,
        DateTime now)
    {
        var sent = new HashSet<string>(plan.SentRows.Select(row => row.RtId), StringComparer.Ordinal);
        var newRows = page.Where(row => !sent.Contains(row.RtId.ToString())).DistinctBy(row => row.RtId).ToList();
        var processedUntil = page.Count > 0 ? page.Max(row => row.ChangedAt) : plan.ProcessedUntil;
        if (plan.ProcessedUntil is { } before && processedUntil < before)
        {
            processedUntil = before;
        }

        return new NotificationDigestDecision(
            newRows.Count == 0 ? NotificationDigestAction.NothingNew : NotificationDigestAction.Send,
            newRows.Select(row => row.RtId.ToString()).ToList(), processedUntil,
            Remember(plan.SentRows, newRows, processedUntil, now));
    }

    public static IReadOnlyList<SentRow> Prune(IEnumerable<SentRow> sentRows, DateTime? readFrom, DateTime now) =>
        sentRows.Where(row => CanBeReadAgain(row, readFrom) || now - row.SentAt < SentRowRetention).ToList();

    private static bool CanBeReadAgain(SentRow row, DateTime? readFrom) => readFrom is not { } from || row.ChangedAt > from;

    public static bool MemoryUnchanged(DeliveryMemory? memory, NotificationDigestDecision decision, string queryRtId,
        string? subjectId, bool skipExistingRows) =>
        memory != null && memory.ProcessedUntil == decision.ProcessedUntil && memory.QueryRtId == queryRtId &&
        memory.SubjectId == subjectId && memory.SkipExistingRows == skipExistingRows &&
        memory.SentRows.Count == decision.SentRows.Count &&
        memory.SentRows.Select(row => row.RtId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(decision.SentRows.Select(row => row.RtId));

    private static IReadOnlyList<SentRow> Remember(IReadOnlyList<SentRow> sentRows,
        IEnumerable<NotificationCandidate> rows, DateTime? processedUntil, DateTime now)
    {
        var added = rows.Select(row => new SentRow(row.RtId.ToString(), now, row.ChangedAt)).ToList();
        var addedIds = added.Select(row => row.RtId).ToHashSet(StringComparer.Ordinal);
        var all = added.Concat(sentRows.Where(row => !addedIds.Contains(row.RtId))).ToList();
        var readFrom = ReadFrom(processedUntil);
        var readAgain = all.Where(row => CanBeReadAgain(row, readFrom)).ToList();
        var rest = all.Where(row => !CanBeReadAgain(row, readFrom) && now - row.SentAt < SentRowRetention)
            .OrderByDescending(row => row.SentAt)
            .Take(Math.Max(0, MaxSentRows - readAgain.Count));
        return readAgain.Concat(rest).ToList();
    }

    public static bool HasOlderTwin(NotificationRecipient recipient, IEnumerable<NotificationRecipient> recipients) =>
        !string.IsNullOrEmpty(recipient.SubjectId) && recipients.Any(other =>
            other.RtId != recipient.RtId &&
            string.Equals(other.SubjectId, recipient.SubjectId, StringComparison.Ordinal) &&
            IsOlder(other, recipient));

    private static bool IsOlder(NotificationRecipient candidate, NotificationRecipient than)
    {
        var byCreation = Nullable.Compare(candidate.CreatedAt, than.CreatedAt);
        return byCreation != 0 ? byCreation < 0 : string.CompareOrdinal(candidate.RtId, than.RtId) < 0;
    }

    public static RenderedNotification Render(string? subjectTemplate, string? bodyTemplate,
        NotificationBodyKind bodyKind, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> newRows,
        TimeZoneInfo timeZone, int totalCount, string? queryName)
    {
        var count = totalCount.ToString(CultureInfo.InvariantCulture);
        var unknown = new List<string>();

        var subject = Resolve(subjectTemplate,
            token => IsCount(token) ? PlaceholderValue.Of(count) : PlaceholderValue.NoSource, unknown);

        var blocks = newRows.Take(MaxRowsPerMail).Select(row => Resolve(bodyTemplate, token =>
        {
            if (IsCount(token))
            {
                return PlaceholderValue.Of(count);
            }

            var index = IndexOf(columns, token);
            if (index < 0)
            {
                return PlaceholderValue.NoSource;
            }

            var text = Format(index < row.Count ? row[index] : null, timeZone);
            return PlaceholderValue.Of(bodyKind == NotificationBodyKind.Markup
                ? NotificationPlaceholderResolver.EncodeForMarkup(text)
                : text);
        }, unknown)).ToList();

        if (unknown.Count > 0)
        {
            throw new NotificationRenderException(unknown);
        }

        if (totalCount > MaxRowsPerMail)
        {
            var more = totalCount - MaxRowsPerMail;
            if (string.IsNullOrWhiteSpace(queryName))
            {
                blocks.Add($"...and {more} more. Open the query to see all.");
            }
            else
            {
                var name = bodyKind == NotificationBodyKind.Markup
                    ? NotificationPlaceholderResolver.EncodeForMarkup(queryName)
                    : queryName;
                blocks.Add($"...and {more} more. Run the query '{name}' in the Studio to see all.");
            }
        }

        return new RenderedNotification(subject, string.Join("\n\n", blocks));
    }

    private static string Resolve(string? template, Func<string, PlaceholderValue> lookup, List<string> unknown)
    {
        var result = NotificationPlaceholderResolver.Resolve(template, lookup);
        foreach (var token in result.SourceMissing.Where(token =>
                     !unknown.Contains(token, StringComparer.OrdinalIgnoreCase)))
        {
            unknown.Add(token);
        }

        return result.Text;
    }

    private static bool IsCount(string token) =>
        string.Equals(token.Trim(), CountPlaceholder, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> columns, string token)
    {
        var name = token.Trim();
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Format(object? value, TimeZoneInfo timeZone) => value switch
    {
        null => string.Empty,
        string text => text,
        DateTime dateTime => TimeZoneInfo.ConvertTimeFromUtc(
                dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
                timeZone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset offset => TimeZoneInfo.ConvertTime(offset, timeZone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
