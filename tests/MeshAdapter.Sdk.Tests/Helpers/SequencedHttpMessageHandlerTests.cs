using System.Net;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// The handler is test infrastructure several node tests assert through, so the two ways it could
/// mislead them are pinned here: a script it cannot answer from, and a recorded request that no
/// longer reflects what was sent.
/// </summary>
public class SequencedHttpMessageHandlerTests
{
    [Fact]
    public void Constructor_WithoutSteps_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SequencedHttpMessageHandler());
    }

    [Fact]
    public async Task Requests_SurviveTheDisposalOfTheMessageTheyCameFrom()
    {
        // The sender disposes every request once it has been sent. A recorded request must still
        // answer for the URL and the headers afterwards, or a test asserts against a corpse.
        var handler = new SequencedHttpMessageHandler(SequencedHttpMessageHandler.Json("{}"));
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://host/api?page=1");
        request.Headers.TryAddWithoutValidation("AuthenticationToken", "token-1");
        request.Content = new StringContent("body");
        (await client.SendAsync(request, TestContext.Current.CancellationToken)).Dispose();
        request.Dispose();

        var recorded = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("https://host/api?page=1", recorded.RequestUri!.ToString());
        Assert.Equal("token-1", recorded.Headers.GetValues("AuthenticationToken").Single());
    }

    [Fact]
    public async Task LastStep_RepeatsOnceTheScriptRunsOut()
    {
        var handler = new SequencedHttpMessageHandler(
            SequencedHttpMessageHandler.Status(HttpStatusCode.InternalServerError),
            SequencedHttpMessageHandler.Json("{\"ok\":true}"));
        using var client = new HttpClient(handler);

        (await client.GetAsync("https://host/api", TestContext.Current.CancellationToken)).Dispose();
        (await client.GetAsync("https://host/api", TestContext.Current.CancellationToken)).Dispose();
        using var third = await client.GetAsync("https://host/api", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }
}
