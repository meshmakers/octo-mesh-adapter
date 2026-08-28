using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Sends one request, retrying transient failures. Everything it can fail with leaves as a
/// <see cref="MeshAdapterPipelineExecutionException" />: a raw cancellation would escape the
/// per-iteration isolation of a surrounding loop, which treats cancellation as a reason to stop
/// altogether.
/// </summary>
internal static class HttpRequestSender
{
    private const int MaxDetailLength = 300;

    /// <summary>
    /// Sends one request until it succeeds or its attempts are used up.
    /// </summary>
    /// <param name="client">The shared HTTP client; its own timeout is never changed</param>
    /// <param name="requestFactory">Builds the message for one attempt - a sent message cannot be sent again</param>
    /// <param name="retry">Attempts and backoff base</param>
    /// <param name="timeoutSeconds">Timeout per attempt, or null for the client's own</param>
    /// <param name="timeProvider">Clock behind both the timeout and the backoff</param>
    /// <param name="nodeContext">The node context, for error reporting</param>
    /// <returns>The successful response, which the caller disposes</returns>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        Func<HttpRequestMessage> requestFactory, HttpRetryOptions retry, int? timeoutSeconds,
        TimeProvider timeProvider, INodeContext nodeContext)
    {
        // A definition can present either value as an explicit null, which overwrites the property
        // initializer, so both are resolved here rather than trusted.
        // A backstop for a direct caller. The node validates the same settings during its
        // configuration checks, because a check that lives here runs inside the node's own net and
        // would be answered with a log entry under the default error handling.
        ValidateRetryOptions(retry, nodeContext);

        var attempts = ResolveAttempts(retry);
        var backoffBaseSeconds = ResolveBackoffBaseSeconds(retry);
        string? lastResponseBody = null;
        string url = "";
        int? lastStatus = null;
        var lastDetail = "no detail";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // The TimeProvider overload, so the timeout is measured on the same clock as the
            // backoff and a test can drive it instead of waiting for the wall clock.
            using var timeoutSource = timeoutSeconds is > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value), timeProvider)
                : null;

            try
            {
                using var request = requestFactory();
                url = request.RequestUri?.ToString() ?? url;
                var response = await client.SendAsync(request,
                    timeoutSource?.Token ?? CancellationToken.None);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                // Kept untruncated alongside the shortened form: the message stays readable when
                // it is thrown, while a caller that reports rather than throws can log the whole
                // body, which is what the node did before it could throw at all.
                var responseBody = await response.Content.ReadAsStringAsync();
                var body = Truncate(responseBody, MaxDetailLength);
                response.Dispose();

                if (!IsTransient(status))
                {
                    throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
                        nodeContext, url, status, attempt, body, responseBody);
                }

                lastStatus = status;
                lastDetail = body;
                lastResponseBody = responseBody;
            }
            catch (HttpRequestException e)
            {
                lastStatus = null;
                lastDetail = e.Message;
                lastResponseBody = null;
            }
            catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
            {
                // The pipeline hands nodes no token to observe, so a cancellation here is a
                // timeout - but not necessarily ours. The per-attempt timeout cannot undercut the
                // HTTP client's own, which is process-wide, so a value above it never takes
                // effect and the client ends the attempt instead. Only the source that actually
                // fired may be named, or an operator goes looking for a setting that did nothing.
                lastStatus = null;
                lastResponseBody = null;
                lastDetail = timeoutSource?.IsCancellationRequested == true
                    ? $"the attempt exceeded the configured timeout of {timeoutSeconds} s"
                    : timeoutSeconds is > 0
                        ? "the attempt was cancelled before the configured timeout elapsed, which is " +
                          "the HTTP client's own timeout when it is the shorter of the two"
                        : "the attempt was cancelled before it answered, which is the HTTP client's " +
                          "own timeout";
            }

            if (attempt < attempts && backoffBaseSeconds > 0)
            {
                // Capped per wait: doubling is unbounded, and beyond the cap the delay would stop
                // being a backoff and start being an outage of its own - at the far end, a value
                // the timer refuses, which would leave the wait itself as the failure.
                var seconds = Math.Min(backoffBaseSeconds * Math.Pow(2, attempt - 1),
                    HttpRetryOptions.MaxBackoffSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), timeProvider);
            }
        }

        throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
            nodeContext, url, lastStatus, attempts, lastDetail, lastResponseBody);
    }

    /// <summary>
    /// Rejects retry settings a pipeline definition can produce but no sender can honour. Called
    /// by the node while it checks its configuration - that is, before the block that turns a
    /// failure into a log entry, because a configuration mistake has to fail whatever the
    /// configured error handling says.
    /// </summary>
    /// <param name="retry">The settings to check, already defaulted for absent sections</param>
    /// <param name="nodeContext">The node context, for error reporting</param>
    public static void ValidateRetryOptions(HttpRetryOptions retry, INodeContext nodeContext)
    {
        var attempts = ResolveAttempts(retry);
        if (attempts > HttpRetryOptions.MaxAllowedAttempts)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpRetryOptions(nodeContext,
                $"retry.maxAttempts is {attempts}, which is beyond the limit of " +
                $"{HttpRetryOptions.MaxAllowedAttempts}. A request that needs more attempts than that " +
                "is broken rather than slow.");
        }

        var backoffBaseSeconds = ResolveBackoffBaseSeconds(retry);
        if (backoffBaseSeconds < 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpRetryOptions(nodeContext,
                $"retry.backoffBaseSeconds is {backoffBaseSeconds}. Use zero to retry without waiting.");
        }
    }

    // A definition can present either value as an explicit null, which overwrites the property
    // initializer, so both are resolved rather than trusted.
    private static int ResolveAttempts(HttpRetryOptions retry)
    {
        return Math.Max(1, retry.MaxAttempts ?? HttpRetryOptions.DefaultMaxAttempts);
    }

    private static double ResolveBackoffBaseSeconds(HttpRetryOptions retry)
    {
        return retry.BackoffBaseSeconds ?? HttpRetryOptions.DefaultBackoffBaseSeconds;
    }

    private static bool IsTransient(int statusCode)
    {
        return statusCode >= 500 || statusCode == 408 || statusCode == 429;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Never split a UTF-16 surrogate pair at the cut.
        if (char.IsHighSurrogate(value[maxLength - 1]))
        {
            maxLength--;
        }

        return value[..maxLength];
    }
}
