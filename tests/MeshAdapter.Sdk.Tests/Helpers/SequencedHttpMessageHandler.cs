using System.Diagnostics;
using System.Net;
using System.Text;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Answers a scripted sequence of outcomes and records every request it saw. A step is either a
/// response or an exception to throw, so retry and paging behaviour can be pinned without a
/// server. The last step repeats once the script runs out, which keeps a paging test from having
/// to script the exact number of pages it expects.
/// </summary>
public sealed class SequencedHttpMessageHandler(
    params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    : HttpMessageHandler
{
    private int _callCount;

    public List<HttpRequestMessage> Requests { get; } = [];

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
        Requests.Add(request);
        var index = Math.Min(_callCount, steps.Length - 1);
        _callCount++;
        return steps[index](request, cancellationToken);
    }
}
