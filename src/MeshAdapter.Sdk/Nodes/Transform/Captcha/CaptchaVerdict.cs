using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Captcha;

internal sealed record CaptchaVerdict(bool Valid, string Source, string? Reason)
{
    public const string SourceTenant = "tenant";
    public const string SourceDefault = "default";
    public const string SourceNone = "none";

    public const string NotConfigured = "not-configured";
    public const string InvalidToken = "invalid-token";
    public const string VerificationUnavailable = "verification-unavailable";

    public static CaptchaVerdict Accepted(string source) => new(true, source, null);

    public static CaptchaVerdict Refused(string source, string reason) => new(false, source, reason);

    public static CaptchaVerdict Unconfigured() => new(false, SourceNone, NotConfigured);

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["valid"] = Valid,
            ["source"] = Source
        };
        if (!Valid && Reason != null)
        {
            json["reason"] = Reason;
        }

        return json;
    }
}
