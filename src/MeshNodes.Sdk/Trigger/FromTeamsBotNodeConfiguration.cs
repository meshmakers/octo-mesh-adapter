using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for the FromTeamsBot trigger node — hosts an HTTP endpoint that receives
/// Microsoft Bot Framework activities (the messaging endpoint configured on the Azure Bot
/// resource, e.g. <c>POST /{tenant}/teamsBot</c>). Inbound counterpart of
/// <c>TeamsBotReply@1</c>. Enables bidirectional Teams conversations: employees upload
/// invoices and ask questions in a 1:1 chat with the bot.
/// </summary>
/// <remarks>
/// File attachments are downloaded and normalised into the same <c>EmailData</c>/
/// <c>AttachmentData</c> shape produced by <c>FromEmail@1</c>/<c>FromMicrosoftGraph@1</c>,
/// so the downstream OCR/AI/document pipeline is channel-agnostic. Channel messages carry
/// SharePoint <c>reference</c> attachments (downloaded via Microsoft Graph using the
/// resolved <see cref="ServerConfiguration"/>); 1:1 chats carry
/// <c>application/vnd.microsoft.teams.file.download.info</c> attachments with a
/// pre-authenticated download URL.
/// </remarks>
[NodeName("FromTeamsBot", 1)]
public record FromTeamsBotNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// WellKnownName of the <c>MicrosoftGraphConfiguration</c> global configuration. Its
    /// <c>ClientId</c>/<c>ClientSecret</c> double as the bot App ID/secret; its
    /// <c>AzureTenantId</c>/<c>ClientId</c>/<c>ClientSecret</c> are used to obtain a Graph
    /// token for downloading channel (SharePoint) file attachments.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Relative route of the messaging endpoint (the tenant prefix is added by the adapter).
    /// Must match the messaging endpoint configured on the Azure Bot resource. Default
    /// <c>/teamsBot</c>.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string Route { get; set; } = "/teamsBot";

    /// <summary>
    /// When true, the inbound Bot Framework JWT (Authorization header) is validated before the
    /// pipeline runs. Default <c>false</c> for local development against the Bot Framework
    /// Emulator / a private dev tunnel.
    /// </summary>
    /// <remarks>
    /// HARDENING: the current check validates the token's issuer, audience and expiry claims
    /// but does NOT yet verify the cryptographic signature against the Bot Framework signing
    /// keys (that requires an OpenID/JWKS dependency and the ability to return HTTP 401, which
    /// the adapter's HTTP hosting does not expose yet). Enable full signature validation before
    /// exposing this endpoint publicly (test-2/prod).
    /// </remarks>
    [PropertyGroup("Security", 0)]
    public bool ValidateInboundToken { get; set; } = false;

    /// <summary>
    /// Expected audience of the inbound token (the bot App ID). When empty, the resolved
    /// configuration's <c>ClientId</c> is used. Only relevant when
    /// <see cref="ValidateInboundToken"/> is true.
    /// </summary>
    [PropertyGroup("Security", 1)]
    public string? BotAppId { get; set; }
}
