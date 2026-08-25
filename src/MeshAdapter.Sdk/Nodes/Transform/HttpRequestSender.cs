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

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        Func<HttpRequestMessage> requestFactory, HttpRetryOptions retry, int? timeoutSeconds,
        TimeProvider timeProvider, INodeContext nodeContext)
    {
        var attempts = Math.Max(1, retry.MaxAttempts); // a misconfigured 0 must still try once
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
                var request = requestFactory();
                url = request.RequestUri?.ToString() ?? url;
                var response = await client.SendAsync(request,
                    timeoutSource?.Token ?? CancellationToken.None);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                var body = Truncate(await response.Content.ReadAsStringAsync(), MaxDetailLength);
                response.Dispose();

                if (!IsTransient(status))
                {
                    throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
                        nodeContext, url, status, attempt, body);
                }

                lastStatus = status;
                lastDetail = body;
            }
            catch (HttpRequestException e)
            {
                lastStatus = null;
                lastDetail = e.Message;
            }
            catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
            {
                // The only cancellation reaching here is this node's own timeout: the pipeline
                // hands nodes no token to observe.
                lastStatus = null;
                lastDetail = timeoutSeconds is > 0
                    ? $"the attempt exceeded the configured timeout of {timeoutSeconds} s"
                    : "the request was cancelled";
            }

            if (attempt < attempts && retry.BackoffBaseSeconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(retry.BackoffBaseSeconds * Math.Pow(2, attempt - 1)),
                    timeProvider);
            }
        }

        throw MeshAdapterPipelineExecutionException.HttpRequestFailed(
            nodeContext, url, lastStatus, attempts, lastDetail);
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
