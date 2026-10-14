using System.Globalization;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

internal enum NotificationDigestAction
{
    RecordSilently,

    NothingNew,

    Send
}

internal sealed record NotificationDigestDecision(
    NotificationDigestAction Action,
    IReadOnlyList<string> NewRtIds,
    IReadOnlyList<string> SeenRtIds,
    IReadOnlyList<string> SkippedRtIds);

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

    public const int MaxTrackedRows = 50_000;

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

    public static bool RecipientChanged(string? recordedSubjectId, string? currentSubjectId) =>
        recordedSubjectId != null && recordedSubjectId != currentSubjectId;

    public static bool StartOptionChanged(bool? recordedSkipExistingRows, bool currentSkipExistingRows) =>
        recordedSkipExistingRows != null && recordedSkipExistingRows != currentSkipExistingRows;

    public static DateTime? LastRunOfQuery(DateTime? lastRunAt, string? recordedQueryRtId, string currentQueryRtId) =>
        recordedQueryRtId == null || recordedQueryRtId == currentQueryRtId ? lastRunAt : null;

    public static NotificationDigestDecision Decide(IReadOnlyCollection<string> currentRtIds,
        IReadOnlyCollection<string>? seenRtIds, IReadOnlyCollection<string>? skippedRtIds, bool skipExistingRows,
        DateTime? lastRunAt, DateTime start)
    {
        var seen = new HashSet<string>(seenRtIds ?? [], StringComparer.Ordinal);
        var firstRunAfterStart = lastRunAt == null || lastRunAt < start;

        IReadOnlyList<string> skippedAfter;
        if (!skipExistingRows)
        {
            skippedAfter = [];
        }
        else if (firstRunAfterStart)
        {
            skippedAfter = currentRtIds.Where(rtId => !seen.Contains(rtId)).ToList();
        }
        else
        {
            var skipped = new HashSet<string>(skippedRtIds ?? [], StringComparer.Ordinal);
            skippedAfter = currentRtIds.Where(skipped.Contains).ToList();
        }

        var skippedAfterSet = new HashSet<string>(skippedAfter, StringComparer.Ordinal);
        var seenAfter = currentRtIds.Where(rtId => !skippedAfterSet.Contains(rtId)).ToList();

        if (firstRunAfterStart && skipExistingRows)
        {
            return new NotificationDigestDecision(NotificationDigestAction.RecordSilently, [], seenAfter, skippedAfter);
        }

        var newRtIds = seenAfter.Where(rtId => !seen.Contains(rtId)).ToList();
        return new NotificationDigestDecision(
            newRtIds.Count == 0 ? NotificationDigestAction.NothingNew : NotificationDigestAction.Send,
            newRtIds, seenAfter, skippedAfter);
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

    public static bool StateUnchanged(NotificationDigestDecision decision, IReadOnlyCollection<string>? seenRtIds,
        IReadOnlyCollection<string>? skippedRtIds) =>
        seenRtIds != null &&
        new HashSet<string>(seenRtIds, StringComparer.Ordinal).SetEquals(decision.SeenRtIds) &&
        new HashSet<string>(skippedRtIds ?? [], StringComparer.Ordinal).SetEquals(decision.SkippedRtIds);

    public static IReadOnlyList<string> MailOrder(IReadOnlyList<string> currentRtIds, bool querySorts)
    {
        return querySorts ? currentRtIds : currentRtIds.Order(StringComparer.Ordinal).ToList();
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
