namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// HTTP API access resolved from a tenant GlobalConfiguration entry. The members are deliberately
/// not <c>required</c>: a half-filled entry has to reach the resolver's message instead of failing
/// deserialization with a JSON path.
/// </summary>
public record HttpApiSettings
{
    /// <summary>API base, for example "https://tenant.example.com/webapp/api/v1".</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>The key sent in the configured auth header - never log it.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Records synthesize a ToString over every member; keep the key out of it.</summary>
    public override string ToString() => $"HttpApiSettings {{ BaseUrl = {BaseUrl}, ApiKey = *** }}";
}
