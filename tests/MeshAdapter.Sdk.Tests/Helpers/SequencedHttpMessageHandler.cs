using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Answers a scripted sequence of outcomes and records every request it saw. A step is either a
/// response or an exception to throw, so retry and paging behaviour can be pinned without a
/// server. The last step repeats once the script runs out, which keeps a paging test from having
/// to script the exact number of pages it expects.
/// </summary>
public sealed class SequencedHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _steps;

    private int _callCount;

    public SequencedHttpMessageHandler(
        params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        // Without a step there is nothing to answer with, and the repeat-the-last rule below would
        // reach for index -1 on the first request.
        if (steps.Length == 0)
        {
            throw new ArgumentException("At least one step is required.", nameof(steps));
        }

        _steps = steps;
    }

    public List<RecordedRequest> Requests { get; } = [];

    public int CallCount => _callCount;

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Json(string body,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(
        HttpStatusCode statusCode, string body = "")
    {
        return (_, _) => Task.FromResult(
            new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
    }

    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throws(
        Exception exception)
    {
        return (_, _) => Task.FromException<HttpResponseMessage>(exception);
    }

    /// <summary>A target that accepts the request and never answers, so only a timeout ends it.</summary>
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hangs()
    {
        return async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new UnreachableException();
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Copied while the message is still alive. The sender disposes each request once it has
        // been sent, so holding on to the message itself would hand a later assertion an object
        // whose content is already gone.
        Requests.Add(RecordedRequest.From(request));
        var index = Math.Min(_callCount, _steps.Length - 1);
        _callCount++;
        return _steps[index](request, cancellationToken);
    }
}

/// <summary>What one request looked like when it was sent, independent of its message's lifetime.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, RecordedHeaders Headers)
{
    public static RecordedRequest From(HttpRequestMessage request)
    {
        return new RecordedRequest(request.Method, request.RequestUri, RecordedHeaders.From(request.Headers));
    }
}

/// <summary>The headers of a recorded request, copied out of the live message.</summary>
public sealed class RecordedHeaders(IReadOnlyDictionary<string, string[]> values)
{
    public static RecordedHeaders From(HttpHeaders headers)
    {
        return new RecordedHeaders(headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Mirrors <see cref="HttpHeaders.GetValues" />, including its throw on an absent name.</summary>
    public IEnumerable<string> GetValues(string name)
    {
        return values.TryGetValue(name, out var found)
            ? found
            : throw new InvalidOperationException($"The recorded request carries no '{name}' header.");
    }

    public bool Contains(string name) => values.ContainsKey(name);
}
