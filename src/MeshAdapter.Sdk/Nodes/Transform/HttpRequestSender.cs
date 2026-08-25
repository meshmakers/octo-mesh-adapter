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
        var attempts = Math.Max(1, retry.MaxAttempts ?? HttpRetryOptions.DefaultMaxAttempts);
        var backoffBaseSeconds = retry.BackoffBaseSeconds ?? HttpRetryOptions.DefaultBackoffBaseSeconds;
        string? lastResponseBody = null;

        // Configuration mistakes, so they fail before a request goes out and whatever the caller's
        // error handling says. Both are values only a definition can produce.
        if (attempts > HttpRetryOptions.MaxAllowedAttempts)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpRetryOptions(nodeContext,
                $"retry.maxAttempts is {attempts}, which is beyond the limit of " +
                $"{HttpRetryOptions.MaxAllowedAttempts}. A request that needs more attempts than that " +
                "is broken rather than slow.");
        }

        if (backoffBaseSeconds < 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidHttpRetryOptions(nodeContext,
                $"retry.backoffBaseSeconds is {backoffBaseSeconds}. Use zero to retry without waiting.");
        }
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
                // The only cancellation reaching here is this node's own timeout: the pipeline
                // hands nodes no token to observe.
                lastStatus = null;
                lastResponseBody = null;
                lastDetail = timeoutSeconds is > 0
                    ? $"the attempt exceeded the configured timeout of {timeoutSeconds} s"
                    : "the request was cancelled";
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
