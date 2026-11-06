using System.Net;
using System.Text;

namespace MeshAdapter.Sdk.Tests.Secrets.Captcha;

/// <summary>
/// Stands in for Cloudflare's siteverify endpoint. By default it answers like Turnstile does for its public test
/// secrets: <c>1x…AA</c> always passes, <c>2x…AA</c> always fails. Nothing leaves the process.
/// </summary>
public sealed class FakeTurnstileHandler : HttpMessageHandler
{
    public const string AlwaysPassSecret = "1x0000000000000000000000000000000AA";
    public const string AlwaysFailSecret = "2x0000000000000000000000000000000AA";
    public const string DummyToken = "XXXX.DUMMY.TOKEN.XXXX";

    public const string PassBody =
        """{"success":true,"error-codes":[],"challenge_ts":"2026-11-06T08:00:00.000Z","hostname":"example.com","action":"","cdata":""}""";

    public const string FailBody = """{"success":false,"error-codes":["invalid-input-response"],"messages":[]}""";

    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<HttpResponseMessage>> _responder;

    public FakeTurnstileHandler(
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<HttpResponseMessage>>? responder = null)
    {
        _responder = responder ?? ((form, _) => Task.FromResult(TurnstileAnswer(form)));
    }

    public List<(Uri? Uri, HttpMethod Method, IReadOnlyDictionary<string, string> Form)> Requests { get; } = [];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage FailWith(params string[] codes) =>
        Json($$"""{"success":false,"error-codes":[{{string.Join(",", codes.Select(code => $"\"{code}\""))}}]}""");

    private static HttpResponseMessage TurnstileAnswer(IReadOnlyDictionary<string, string> form)
    {
        var secret = form.GetValueOrDefault("secret");
        return secret == AlwaysPassSecret ? Json(PassBody) : Json(FailBody);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>();
        if (request.Content != null)
        {
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                form[WebUtility.UrlDecode(parts[0])] = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            }
        }

        Requests.Add((request.RequestUri, request.Method, form));
        return await _responder(form, cancellationToken);
    }
}
