using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

internal enum NotificationDigestAction
{
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

internal sealed record NotificationPlan(bool Restarted, DateTime? ReadFrom, DateTime? ProcessedUntil,
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

    private static readonly Regex MarkdownControlCharacter =
        new("""&(?:amp|lt|gt|quot|#39);|[\\`*_\[\]()!#|~^${}+]""", RegexOptions.Compiled);

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
        !sameRows || !string.IsNullOrEmpty(lastError) ||
        lastRunAt is not { } ranAt || now - ranAt >= IdleRecordInterval;

    public static NotificationPlan Plan(DeliveryMemory? memory, string? subjectId, bool skipExistingRows,
        string queryRtId, DateTime start, DateTime? preferenceChangedAt, DateTime? subscriptionChangedAt, DateTime now)
    {
        var otherRecipient = memory?.SubjectId != null && memory.SubjectId != subjectId;
        var switchedToCatchUp = memory?.SkipExistingRows == true && !skipExistingRows;
        var sentRows = otherRecipient || switchedToCatchUp
            ? []
            : Prune(memory?.SentRows ?? [], ReadFrom(memory?.ProcessedUntil), now);
        var optionChanged = memory?.SkipExistingRows != null && memory.SkipExistingRows != skipExistingRows;
        var queryChanged = memory?.QueryRtId != null && memory.QueryRtId != queryRtId;
        var restart = memory?.LastRunAt is not { } lastRunAt || skipExistingRows && lastRunAt < start || otherRecipient ||
                      queryChanged || optionChanged || skipExistingRows && memory.ProcessedUntil == null;

        if (!restart)
        {
            return new NotificationPlan(false, ReadFrom(memory!.ProcessedUntil), memory.ProcessedUntil, sentRows);
        }

        if (!skipExistingRows)
        {
            return new NotificationPlan(true, null, null, sentRows);
        }

        var startedAt = start;
        if ((otherRecipient || optionChanged) && preferenceChangedAt > startedAt)
        {
            startedAt = preferenceChangedAt.Value;
        }

        if (queryChanged)
        {
            foreach (var changedAt in new[] { memory!.LastRunAt, subscriptionChangedAt })
            {
                if (changedAt > startedAt)
                {
                    startedAt = changedAt.Value;
                }
            }
        }

        return new NotificationPlan(true, startedAt, startedAt + Overlap, sentRows);
    }

    public static NotificationDigestDecision? Reconcile(NotificationPlan plan,
        IReadOnlyList<NotificationCandidate> page, NotificationDigestDecision decision, IEnumerable<string> requested,
        IReadOnlySet<string> read, DateTime now)
    {
        if (read.Count == 0)
        {
            return null;
        }

        var unlisted = requested.Where(rtId => !read.Contains(rtId)).ToHashSet(StringComparer.Ordinal);
        return unlisted.Count == 0 ? decision : Decide(plan, page, now, unlisted);
    }

    public static DateTime? ReadFrom(DateTime? processedUntil) =>
        processedUntil is { } until && until > DateTime.MinValue + Overlap ? until - Overlap : null;

    public static NotificationDigestDecision Decide(NotificationPlan plan, IReadOnlyList<NotificationCandidate> page,
        DateTime now, IReadOnlySet<string>? unlisted = null)
    {
        var sent = new HashSet<string>(plan.SentRows.Select(row => row.RtId), StringComparer.Ordinal);
        var newRows = page.Where(row => !sent.Contains(row.RtId.ToString())).DistinctBy(row => row.RtId).ToList();
        var processedUntil = page.Count > 0 ? page.Max(row => row.ChangedAt) : plan.ProcessedUntil;
        if (plan.ProcessedUntil is { } before && processedUntil < before)
        {
            processedUntil = before;
        }

        DateTime? earliestUnlisted = null;
        if (unlisted is { Count: > 0 })
        {
            earliestUnlisted = newRows.Where(row => unlisted.Contains(row.RtId.ToString()))
                .Select(row => (DateTime?)row.ChangedAt).Min();
            newRows = newRows.Where(row => !unlisted.Contains(row.RtId.ToString())).ToList();
            if (earliestUnlisted < processedUntil)
            {
                processedUntil = earliestUnlisted;
            }
        }

        var (sentRows, remembered) = Remember(plan.SentRows, newRows, processedUntil, now, earliestUnlisted);
        return new NotificationDigestDecision(
            newRows.Count == 0 ? NotificationDigestAction.NothingNew : NotificationDigestAction.Send,
            newRows.Select(row => row.RtId.ToString()).ToList(), remembered, sentRows);
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

    private static (IReadOnlyList<SentRow> SentRows, DateTime? ProcessedUntil) Remember(
        IReadOnlyList<SentRow> sentRows, IEnumerable<NotificationCandidate> rows, DateTime? processedUntil,
        DateTime now, DateTime? earliestUnlisted)
    {
        var added = rows.Select(row => new SentRow(row.RtId.ToString(), now, row.ChangedAt)).ToList();
        var addedIds = added.Select(row => row.RtId).ToHashSet(StringComparer.Ordinal);
        var all = added.Concat(sentRows.Where(row => !addedIds.Contains(row.RtId))).ToList();
        var lastRead = added.Max(row => (DateTime?)row.ChangedAt);
        if (earliestUnlisted is { } unlistedAt && !(lastRead <= unlistedAt))
        {
            lastRead = unlistedAt;
        }

        processedUntil = PastOverflow(all, processedUntil, lastRead);
        var readFrom = ReadFrom(processedUntil);
        var readAgain = all.Where(row => CanBeReadAgain(row, readFrom)).ToList();
        var rest = all.Where(row => !CanBeReadAgain(row, readFrom) && now - row.SentAt < SentRowRetention)
            .OrderByDescending(row => row.SentAt)
            .Take(Math.Max(0, MaxSentRows - readAgain.Count));
        return (readAgain.Concat(rest).ToList(), processedUntil);
    }

    private static DateTime? PastOverflow(IReadOnlyList<SentRow> rows, DateTime? processedUntil, DateTime? lastRead)
    {
        var readFrom = ReadFrom(processedUntil);
        var kept = 0;
        foreach (var changedAt in rows.Where(row => CanBeReadAgain(row, readFrom)).GroupBy(row => row.ChangedAt)
                     .OrderByDescending(group => group.Key))
        {
            kept += changedAt.Count();
            if (kept > MaxSentRows)
            {
                return lastRead is { } last && changedAt.Key >= last ? processedUntil : changedAt.Key + Overlap;
            }
        }

        return processedUntil;
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
            return PlaceholderValue.Of(bodyKind == NotificationBodyKind.Markup ? EncodeForMarkdown(text) : text);
        }, unknown)).ToList();

        if (unknown.Count > 0)
        {
            throw new NotificationRenderException(unknown);
        }

        if (totalCount > blocks.Count)
        {
            var more = totalCount - blocks.Count;
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

    private static string EncodeForMarkdown(string text) =>
        MarkdownControlCharacter.Replace(NotificationPlaceholderResolver.EncodeForMarkup(text),
            match => match.Length > 1 ? match.Value : $"&#{(int)match.Value[0]};");

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
