namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     Decides whether a trigger's status line is worth a report (AB#5619 / AB#5620). Every report
///     ends as a write on the pipeline's DeployableEntity (controller, AB#5618), so a trigger that
///     polls every 5 s must NOT report every poll. A report is due when
///     <list type="number">
///         <item>nothing was reported yet (the first line after start),</item>
///         <item>the outcome flipped (success → error or error → success),</item>
///         <item>the event carried activity (messages handled) and <c>activityInterval</c> has passed, or</item>
///         <item><c>heartbeatInterval</c> has passed since the last report (same outcome as before).</item>
///     </list>
///     Thread-safe: a push trigger (Teams) may handle several activities concurrently.
/// </summary>
internal sealed class TriggerStatusThrottle
{
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _activityInterval;
    private readonly Lock _gate = new();

    private bool? _lastWasError;
    private DateTime _lastReportedAtUtc;

    /// <param name="heartbeatInterval">Minimum gap between two reports of the same outcome without activity.</param>
    /// <param name="activityInterval">Minimum gap between two reports of the same outcome WITH activity
    ///     (<see cref="TimeSpan.Zero" />: every event with activity is reported).</param>
    public TriggerStatusThrottle(TimeSpan heartbeatInterval, TimeSpan activityInterval)
    {
        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "must be positive");
        }

        if (activityInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activityInterval), "must not be negative");
        }

        _heartbeatInterval = heartbeatInterval;
        _activityInterval = activityInterval;
    }

    /// <summary>
    ///     True when this event should be reported; the throttle then records it as reported, so the
    ///     caller must report on <c>true</c> (a failed delivery is the reporter's concern — it never throws).
    /// </summary>
    public bool ShouldReport(DateTime utcNow, bool isError, bool hasActivity)
    {
        lock (_gate)
        {
            var due = _lastWasError is null
                      || _lastWasError != isError
                      || (hasActivity && utcNow - _lastReportedAtUtc >= _activityInterval)
                      || utcNow - _lastReportedAtUtc >= _heartbeatInterval;
            if (due)
            {
                _lastWasError = isError;
                _lastReportedAtUtc = utcNow;
            }

            return due;
        }
    }

    /// <summary>
    ///     The heartbeat a trigger configures in seconds; a missing/zero/negative value (YamlDotNet
    ///     writes 0 for a present-but-null key) falls back to <paramref name="defaultSeconds" />.
    /// </summary>
    public static TimeSpan ResolveInterval(int configuredSeconds, int defaultSeconds)
    {
        return TimeSpan.FromSeconds(configuredSeconds > 0 ? configuredSeconds : defaultSeconds);
    }
}
