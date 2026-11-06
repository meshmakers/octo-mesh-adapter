using System.Text;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

public class VerifyCaptchaSecretHygieneTests : VerifyCaptchaTestBase
{
    private const string Sentinel = "sentinel-captcha-secret-6f1d2c";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<HttpResponseMessage>> AcceptSentinel =>
        (form, _) => Task.FromResult(form.GetValueOrDefault("secret") == Sentinel
            ? FakeTurnstileHandler.Json(FakeTurnstileHandler.PassBody)
            : FakeTurnstileHandler.Json(FakeTurnstileHandler.FailBody));

    public static TheoryData<string> Outcomes => new() { "pass", "fail", "http-error", "network", "timeout" };

    private void Answer(string outcome)
    {
        Handler = outcome switch
        {
            "pass" => new FakeTurnstileHandler(AcceptSentinel),
            "fail" => new FakeTurnstileHandler((_, _) => Task.FromResult(FakeTurnstileHandler.FailWith("invalid-input-secret"))),
            "http-error" => new FakeTurnstileHandler((_, _) =>
                Task.FromResult(FakeTurnstileHandler.Json("upstream error", System.Net.HttpStatusCode.BadGateway))),
            "network" => new FakeTurnstileHandler((_, _) => throw new HttpRequestException("refused")),
            _ => new FakeTurnstileHandler((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()))
        };
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task TenantSentinel_NeverReachesTheDataContextDebugPointsOrTheLog(string outcome)
    {
        Answer(outcome);
        StubTenantEntity(TenantEntity(Sentinel));
        await AssertSentinelAbsentAfterRun(Config(TenantSecretByRtId()));
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task DefaultSentinel_NeverReachesTheDataContextDebugPointsOrTheLog(string outcome)
    {
        Answer(outcome);
        StubDefaults(DefaultEntity(Sentinel));
        await AssertSentinelAbsentAfterRun(Config());
    }

    [Fact]
    public async Task TheSentinelIsWhatWasSent_SoTheAbsenceIsMeaningful()
    {
        Handler = new FakeTurnstileHandler(AcceptSentinel);
        StubDefaults(DefaultEntity(Sentinel));

        AssertResult(await Run(Config()), true, "default", null);
        Assert.Equal(Sentinel, Assert.Single(Handler.Requests).Form["secret"]);
    }

    [Fact]
    public async Task DamagedTenantSecret_IsVerificationUnavailable_WithoutFallingBackToTheDefault()
    {
        var envelope = Protector.Protect(Sentinel).Envelope!;
        var last = envelope[^2] == 'A' ? 'B' : 'A';
        var entity = TenantEntity(null);
        entity.SetAttributeRawValue("Password", RtSecretValue.Protected(envelope[..^2] + last + envelope[^1]));
        StubTenantEntity(entity);
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var logger = A.Fake<IPipelineLogger>();

        var result = await Run(Config(TenantSecretByRtId()), logger: logger);

        AssertResult(result, false, "tenant", "verification-unavailable");
        Assert.Empty(Handler.Requests);
        AssertDefaultNotRead();
        Assert.Contains("Password", Logged(logger));
    }

    [Fact]
    public async Task TenantSecretWithAForeignKeyId_CountsAsNotSet_AndTheDefaultIsUsed()
    {
        var foreign = SecretTestSupport.CreateProtector("k9");
        var entity = TenantEntity(null);
        entity.SetAttributeRawValue("Password", foreign.Protect(Sentinel));
        StubTenantEntity(entity);
        StubDefaults(DefaultEntity(FakeTurnstileHandler.AlwaysPassSecret));
        var logger = A.Fake<IPipelineLogger>();

        var result = await Run(Config(TenantSecretByRtId()), logger: logger);

        AssertResult(result, true, "default", null);
        Assert.Contains("k9", Logged(logger));
        Assert.DoesNotContain(Sentinel, Logged(logger));
    }

    [Fact]
    public async Task AdapterWithoutKeyRing_IsVerificationUnavailable()
    {
        StubDefaults(DefaultEntity(Sentinel));
        var unconfigured = SecretTestSupport.CreateProtector(keys: null);
        var (dc, nc, _) = Context(Config(), Input());

        await Node(protector: unconfigured).ProcessObjectAsync(dc, nc);

        AssertResult(dc.Get<JsonNode>("$.captcha")!.AsObject(), false, "default", "verification-unavailable");
        Assert.Empty(Handler.Requests);
    }

    private async Task AssertSentinelAbsentAfterRun(Meshmakers.Octo.MeshAdapter.Nodes.Transform.VerifyCaptchaNodeConfiguration config)
    {
        var logger = A.Fake<IPipelineLogger>();
        var (dc, nc, debugger) = Context(config, Input(), logger);

        await Node().ProcessObjectAsync(dc, nc);
        nc.Unregister(dc);

        var everything = new StringBuilder()
            .Append(dc.Get<JsonNode>("$")!.ToJsonString())
            .Append(System.Text.Json.JsonSerializer.Serialize(debugger.GetDebugInformation()))
            .Append(Logged(logger))
            .ToString();
        Assert.NotEmpty(Handler.Requests);
        Assert.DoesNotContain(Sentinel, everything);
        Assert.DoesNotContain(Base64(Sentinel), everything);
        Assert.False(dc.Get<JsonNode>("$.captcha")!["valid"] is null);
    }

    private static string Logged(IPipelineLogger logger) =>
        string.Join("|", Fake.GetCalls(logger).SelectMany(c => c.Arguments)
            .SelectMany(a => a is object[] list ? list.Cast<object?>() : new[] { a })
            .Select(a => a?.ToString()));
}
