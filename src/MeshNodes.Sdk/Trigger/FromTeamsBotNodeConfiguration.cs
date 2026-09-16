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
[NodeRequiresRunningProcess]
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
    /// When true (the default, AB#5010), the inbound Bot Framework JWT (Authorization header)
    /// is fully validated before the pipeline runs: cryptographic signature against the Bot
    /// Framework's published signing keys (<see cref="OpenIdMetadataUrl"/>), issuer, audience
    /// (the bot App ID) and lifetime. Set to <c>false</c> ONLY for local development against
    /// the Bot Framework Emulator, which sends no token — never on a publicly reachable
    /// messaging endpoint: without validation anyone who can reach the route can inject
    /// documents and query the assistant.
    /// </summary>
    [PropertyGroup("Security", 0)]
    public bool ValidateInboundToken { get; set; } = true;

    /// <summary>
    /// Expected audience of the inbound token (the bot App ID). When empty, the resolved
    /// configuration's <c>ClientId</c> is used. Only relevant when
    /// <see cref="ValidateInboundToken"/> is true.
    /// </summary>
    [PropertyGroup("Security", 1)]
    public string? BotAppId { get; set; }

    /// <summary>
    /// OpenID metadata document the Bot Framework signing keys are resolved from. The default
    /// is the public-cloud endpoint; override for sovereign clouds (e.g. Azure Government)
    /// or tests. Only relevant when <see cref="ValidateInboundToken"/> is true.
    /// </summary>
    [PropertyGroup("Security", 2)]
    public string OpenIdMetadataUrl { get; set; } =
        "https://login.botframework.com/v1/.well-known/openidconfiguration";

    /// <summary>
    /// Accepted token issuers. Empty = the Bot Framework public-cloud issuer
    /// (<c>https://api.botframework.com</c>). Override together with
    /// <see cref="OpenIdMetadataUrl"/> for sovereign clouds. Only relevant when
    /// <see cref="ValidateInboundToken"/> is true.
    /// </summary>
    [PropertyGroup("Security", 3)]
    public string[]? ValidTokenIssuers { get; set; }
}
