using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Captcha;

internal static class TurnstileSiteVerify
{
    private const int MaxResponseBytes = 64 * 1024;

    private static readonly HashSet<string> TokenErrorCodes = new(StringComparer.Ordinal)
    {
        "missing-input-response",
        "invalid-input-response",
        "timeout-or-duplicate"
    };

    public static string MapErrorCodes(IReadOnlyCollection<string> errorCodes)
    {
        var everyCodeConcernsTheToken = errorCodes.Count > 0 && errorCodes.All(TokenErrorCodes.Contains);
        return everyCodeConcernsTheToken ? CaptchaVerdict.InvalidToken : CaptchaVerdict.VerificationUnavailable;
    }

    public static async Task<string?> VerifyAsync(HttpClient httpClient, Uri endpoint, string secret, string token,
        string? remoteIp, TimeSpan timeout, TimeProvider timeProvider, INodeContext nodeContext)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("secret", secret),
            new("response", token)
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form.Add(new KeyValuePair<string, string>("remoteip", remoteIp.Trim()));
        }

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new FormUrlEncodedContent(form);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(nodeContext, $"the provider answered HTTP {(int)response.StatusCode}");
            }

            var body = await ReadBoundedAsync(response, timeoutSource.Token);
            return Interpret(body, nodeContext);
        }
        catch (OperationCanceledException)
        {
            return Unavailable(nodeContext, $"no answer from the provider within {timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException e)
        {
            return Unavailable(nodeContext, $"the provider could not be reached ({e.HttpRequestError})");
        }
    }

    private static string? Interpret(byte[]? body, INodeContext nodeContext)
    {
        if (body == null)
        {
            return Unavailable(nodeContext, "the provider's answer is too large");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Unavailable(nodeContext, "the provider's answer has no verdict");
            }

            if (success.GetBoolean())
            {
                return null;
            }

            var errorCodes = ReadErrorCodes(root);
            if (errorCodes == null)
            {
                return Unavailable(nodeContext, "the provider's answer has unusable error codes");
            }

            var reason = MapErrorCodes(errorCodes);
            if (reason == CaptchaVerdict.VerificationUnavailable)
            {
                return Unavailable(nodeContext,
                    $"the provider refused the request with error codes [{string.Join(", ", errorCodes)}]");
            }

            nodeContext.Debug("CAPTCHA token refused by the provider: [{0}]", string.Join(", ", errorCodes));
            return reason;
        }
        catch (JsonException)
        {
            return Unavailable(nodeContext, "the provider's answer is not JSON");
        }
    }

    private static List<string>? ReadErrorCodes(JsonElement root)
    {
        if (!root.TryGetProperty("error-codes", out var codes))
        {
            return [];
        }

        if (codes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var code in codes.EnumerateArray())
        {
            result.Add(code.ValueKind == JsonValueKind.String ? code.GetString() ?? string.Empty : code.ToString());
        }

        return result;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Unavailable(INodeContext nodeContext, string detail)
    {
        nodeContext.Warning("CAPTCHA verification unavailable: {0}", detail);
        return CaptchaVerdict.VerificationUnavailable;
    }
}
