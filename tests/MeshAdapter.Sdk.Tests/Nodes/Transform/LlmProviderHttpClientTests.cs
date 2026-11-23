using System.Net;
using System.Net.Sockets;
using System.Text;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
///     The HTTP client the adapter registers for the LLM providers follows no redirect: the API key travels
///     in <c>x-api-key</c> (Anthropic) or <c>Authorization</c> (OpenAI-compatible), and a handler that follows
///     redirects would send a custom header on to the origin a redirect names.
///     Real sockets on loopback, the client as the adapter registers it.
/// </summary>
public class LlmProviderHttpClientTests
{
    [Fact]
    public async Task ARedirectToAnotherOrigin_IsNotFollowed_AndTheKeyGoesNowhereElse()
    {
        using var target = new LoopbackServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        using var redirecting = new LoopbackServer(_ =>
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/v1/messages\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");

        var services = new ServiceCollection();
        services.AddLlmProviderHttpClient();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = new AnthropicMessagesChatClient(factory, "the-api-key",
            new Uri($"http://127.0.0.1:{redirecting.Port}"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Q")],
            new ChatOptions { ModelId = "m", MaxOutputTokens = 10 }, TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 307", exception.Message);
        Assert.Contains("redirect", exception.Message);
        Assert.Contains("x-api-key: the-api-key", Assert.Single(redirecting.Requests),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task ACookieTheProviderSets_IsNotSentWithTheNextRequest()
    {
        const string body = """{"stop_reason":"end_turn","content":[{"type":"text","text":"a"}]}""";
        using var provider = new LoopbackServer(_ =>
            "HTTP/1.1 200 OK\r\nSet-Cookie: session=from-the-provider; Path=/\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");

        var services = new ServiceCollection();
        services.AddLlmProviderHttpClient();
        await using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IHttpClientFactory>();

        for (var i = 0; i < 2; i++)
        {
            using var client = new AnthropicMessagesChatClient(factory, "the-api-key",
                new Uri($"http://127.0.0.1:{provider.Port}"));
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")],
                new ChatOptions { ModelId = "m", MaxOutputTokens = 10 }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, head =>
            Assert.DoesNotContain("cookie:", head, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheOpenAiCompatibleClient_FollowsNoRedirect_AndTheKeyGoesNowhereElse()
    {
        using var target = new LoopbackServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
        using var redirecting = new LoopbackServer(_ =>
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/v1/chat/completions\r\n" +
            "Content-Length: 0\r\nConnection: close\r\n\r\n");

        var services = new ServiceCollection();
        services.AddLlmProviderHttpClient();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = new OpenAiCompatibleChatClient(factory, "the-api-key",
            new Uri($"http://127.0.0.1:{redirecting.Port}/v1"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Q")],
            new ChatOptions { ModelId = "m", MaxOutputTokens = 10 }, TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 307", exception.Message);
        Assert.Contains("redirect", exception.Message);
        Assert.Contains("authorization: Bearer the-api-key", Assert.Single(redirecting.Requests),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task TheOpenAiCompatibleClient_SendsNoCookieTheProviderSet()
    {
        const string body =
            """{"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"a"},"finish_reason":"stop"}]}""";
        using var provider = new LoopbackServer(_ =>
            "HTTP/1.1 200 OK\r\nSet-Cookie: session=from-the-provider; Path=/\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");

        var services = new ServiceCollection();
        services.AddLlmProviderHttpClient();
        await using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IHttpClientFactory>();

        for (var i = 0; i < 2; i++)
        {
            using var client = new OpenAiCompatibleChatClient(factory, "the-api-key",
                new Uri($"http://127.0.0.1:{provider.Port}/v1"));
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Q")],
                new ChatOptions { ModelId = "m", MaxOutputTokens = 10 }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, provider.Requests.Count);
        Assert.All(provider.Requests, head =>
            Assert.DoesNotContain("cookie:", head, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A one-answer-per-connection HTTP server on a loopback port; keeps the request heads.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackServer(Func<string, string> answer)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient connection;
                    try
                    {
                        connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    using (connection)
                    {
                        var stream = connection.GetStream();
                        var head = await ReadHeadAsync(stream);
                        lock (Requests)
                        {
                            Requests.Add(head);
                        }

                        var bytes = Encoding.ASCII.GetBytes(answer(head));
                        await stream.WriteAsync(bytes);
                    }
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Requests { get; } = [];

        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var head = new StringBuilder();
            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return head.ToString();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The loop ends with the listener.
            }

            _stop.Dispose();
        }
    }
}
