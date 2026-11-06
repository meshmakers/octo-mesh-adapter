using System.Net;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public class VerifyCaptchaVerdictTests : VerifyCaptchaTestBase
{
    [Fact]
    public async Task AlwaysPassTestSecret_IsValid_AndPostsToTheFixedSiteVerifyEndpoint()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config());

        AssertResult(result, true, "default", null);
        var request = Assert.Single(Handler.Requests);
        Assert.Equal(new Uri("https://challenges.cloudflare.com/turnstile/v0/siteverify"), request.Uri);
        Assert.Equal(VerifyCaptchaNode.TurnstileSiteVerifyEndpoint, request.Uri);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(FakeTurnstileHandler.DummyToken, request.Form["response"]);
        Assert.Equal("203.0.113.7", request.Form["remoteip"]);
    }

    [Fact]
    public async Task AlwaysFailTestSecret_IsInvalidToken()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysFailSecret));

        var result = await Run(Config());

        AssertResult(result, false, "default", "invalid-token");
    }

    [Fact]
    public async Task WithoutRemoteIp_NoRemoteIpIsSent()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        await Run(Config(), """{"body":{"captchaToken":"t"}}""");

        Assert.False(Assert.Single(Handler.Requests).Form.ContainsKey("remoteip"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingToken_IsInvalidToken_WithoutAnHttpCall(string? token)
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config(), Input(token));

        AssertResult(result, false, "default", "invalid-token");
        Assert.Empty(Handler.Requests);
    }

    [Theory]
    [InlineData("missing-input-response")]
    [InlineData("invalid-input-response")]
    [InlineData("timeout-or-duplicate")]
    public async Task TokenErrorCodes_MapToInvalidToken(string code)
    {
        await AssertMapped(FakeTurnstileHandler.FailWith(code), "invalid-token");
    }

    [Theory]
    [InlineData("missing-input-secret")]
    [InlineData("invalid-input-secret")]
    [InlineData("bad-request")]
    [InlineData("internal-error")]
    [InlineData("some-future-code")]
    public async Task OtherErrorCodes_MapToVerificationUnavailable(string code)
    {
        await AssertMapped(FakeTurnstileHandler.FailWith(code), "verification-unavailable");
    }

    [Fact]
    public async Task ATokenCodeTogetherWithASecretCode_IsVerificationUnavailable()
    {
        await AssertMapped(FakeTurnstileHandler.FailWith("invalid-input-response", "invalid-input-secret"),
            "verification-unavailable");
    }

    [Fact]
    public async Task FailureWithoutErrorCodes_IsVerificationUnavailable()
    {
        await AssertMapped(FakeTurnstileHandler.Json("""{"success":false,"error-codes":[]}"""),
            "verification-unavailable");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ErrorStatus_IsVerificationUnavailable(HttpStatusCode status)
    {
        await AssertMapped(FakeTurnstileHandler.Json(FakeTurnstileHandler.PassBody, status), "verification-unavailable");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"error-codes":[]}""")]
    [InlineData("""{"success":"true"}""")]
    [InlineData("""{"success":false,"error-codes":"invalid-input-response"}""")]
    public async Task UnusableBody_IsVerificationUnavailable(string body)
    {
        await AssertMapped(FakeTurnstileHandler.Json(body), "verification-unavailable");
    }

    [Fact]
    public async Task NetworkFailure_IsVerificationUnavailable()
    {
        Handler = new FakeTurnstileHandler((_, _) => throw new HttpRequestException("connection refused"));
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        AssertResult(await Run(Config()), false, "default", "verification-unavailable");
    }

    [Fact]
    public async Task ConnectionLostWhileTheAnswerIsRead_IsVerificationUnavailable()
    {
        Handler = new FakeTurnstileHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }));
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        AssertResult(await Run(Config()), false, "default", "verification-unavailable");
    }

    [Fact]
    public async Task ProviderThatDoesNotAnswerInTime_IsVerificationUnavailable()
    {
        var clock = new FakeTimeProvider();
        Clock = clock;
        Handler = new FakeTurnstileHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return FakeTurnstileHandler.Json(FakeTurnstileHandler.PassBody);
        });
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var config = Config() with { TimeoutSeconds = 5 };
        var (dc, nc, _) = Context(config, Input());

        var run = Node().ProcessObjectAsync(dc, nc);
        for (var i = 0; i < 200 && !run.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await run;
        AssertResult(dc.Get<System.Text.Json.Nodes.JsonNode>("$.captcha")!.AsObject(), false, "default",
            "verification-unavailable");
    }

    [Fact]
    public async Task SiteVerifyFieldsBeyondTheVerdict_AreNotPassedOn()
    {
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var (dc, nc, _) = Context(Config(), Input());
        await Node().ProcessObjectAsync(dc, nc);

        var whole = dc.Get<System.Text.Json.Nodes.JsonNode>("$")!.ToJsonString();
        Assert.DoesNotContain("hostname", whole);
        Assert.DoesNotContain("challenge_ts", whole);
        Assert.DoesNotContain("error-codes", whole);
        Assert.DoesNotContain("example.com", whole);
    }

    private async Task AssertMapped(HttpResponseMessage answer, string reason)
    {
        Handler = new FakeTurnstileHandler((_, _) => Task.FromResult(answer));
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));

        var result = await Run(Config());

        AssertResult(result, false, "default", reason);
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The response ended prematurely.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("The response ended prematurely.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("The response ended prematurely.");
    }
}
