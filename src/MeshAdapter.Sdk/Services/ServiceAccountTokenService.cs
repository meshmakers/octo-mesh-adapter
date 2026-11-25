using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Acquires and manages OAuth2 access tokens from a ServiceAccountConfiguration entity.
/// Reads client credentials from the runtime repository and uses them to obtain tokens
/// via the client credentials grant.
/// </summary>
public interface IServiceAccountTokenService
{
    /// <summary>
    ///     Acquires a <b>delegated</b> access token that runs on the end user's <c>sub</c> — the
    ///     service account of <paramref name="wellKnownName" /> acting on behalf of the caller who
    ///     presented <paramref name="subjectToken" /> (OctoMesh delegation grant, AB#5026/AB#5031).
    ///     The issued token carries the <b>intersection</b> of the service account's and the user's
    ///     roles plus an <c>act</c> claim naming the service account, so a downstream service applies
    ///     the user's own permissions instead of the service account's full reach.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    /// <param name="subjectToken">
    ///     The end user's raw access token, presented as <c>subject_token</c>. Credential material —
    ///     never log it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The delegated access token, or <c>null</c> when it could not be acquired.</returns>
    /// <remarks>
    ///     🔴 <b>The token is RETURNED, not written into <see cref="IServiceClientAccessToken" />.</b>
    ///     That instance is a process-wide singleton and doubles — as
    ///     <c>ICommunicationServiceClientAccessToken</c> — as the adapter's own service identity
    ///     towards the communication controller. Storing a user-bound token there would leak one
    ///     caller's identity into every concurrent request and into the adapter's own service calls.
    ///     No method of this service writes there (AB#8699).
    /// </remarks>
    Task<string?> AcquireDelegatedTokenAsync(ITenantRepository tenantRepository, string wellKnownName,
        string subjectToken, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Answers <b>who the service account is</b>: the subject and the roles its own
    ///     client-credentials token carries (AB#5028). Grant path next to
    ///     <see cref="AcquireServiceAccountTokenAsync" /> (which returns the account's own token)
    ///     and <see cref="AcquireDelegatedTokenAsync" /> (which returns a user-bound token).
    /// </summary>
    /// <param name="credentials">
    ///     The account's credentials — projected into the pipeline configuration by the controller
    ///     (AB#5027), so no repository read is needed to get here.
    /// </param>
    /// <param name="pipelineTenantId">
    ///     The tenant of the pipeline the identity is resolved for — required (AB#9776). The token is requested
    ///     for this tenant and must carry it; a <see cref="ServiceAccountCredentials.TenantId" /> naming another
    ///     tenant, or an empty value here, is refused (<c>null</c>), so the roles of a foreign tenant can never
    ///     be applied to the pipeline's repository.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The identity, or <c>null</c> when no token could be acquired.</returns>
    /// <remarks>
    ///     <b>Roles only exist on the token.</b> They are assigned to the client in the identity
    ///     service and are not part of the <c>ServiceAccountConfiguration</c> entity, so the only way
    ///     to learn them is to request a token and read its <c>role</c> claims. The request goes only to
    ///     the adapter's own identity service, and the token's signature, issuer and tenant are verified
    ///     before a claim is read (AB#9776, <see cref="OwnIssuerPolicy" />). It is neither returned nor written into
    ///     <see cref="IServiceClientAccessToken" />: this call answers an identity question, it does
    ///     not hand out a credential.
    ///     <para>
    ///         Results are cached per <c>(TenantId, ClientId)</c> until shortly before the token's own
    ///         <c>exp</c>. It never holds a credential, so it is separate from the token cache of
    ///         <see cref="AcquireServiceAccountTokenAsync" />.
    ///     </para>
    /// </remarks>
    Task<ServiceAccountIdentity?> AcquireServiceAccountIdentityAsync(ServiceAccountCredentials credentials,
        string pipelineTenantId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Acquires the service account's <b>own</b> access token (client credentials, or the
    ///     impersonation grant when the configuration has no secret, AB#5114) and <b>returns</b> it
    ///     (AB#8658). Used by nodes that act as a dedicated service identity — <c>McpToolCall@1</c>
    ///     calls MCP tools as e.g. <c>knowledge-curator</c>.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository to read the configuration from</param>
    /// <param name="wellKnownName">Well-known name of the ServiceAccountConfiguration entity</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The access token, or <c>null</c> when it could not be acquired (the cause is logged).</returns>
    /// <remarks>
    ///     🔴 <b>Never written into <see cref="IServiceClientAccessToken" /></b> — that process-wide
    ///     singleton is the adapter's own identity and is shared by every concurrent pipeline, so a
    ///     service-account token stored there could be handed to another pipeline's account, or replace the
    ///     adapter's own identity (AB#8699). Tokens are cached per <c>(TenantId, ClientId, IssuerUri)</c> until shortly before they
    ///     expire, so a busy pipeline does not pay a token round trip per execution.
    /// </remarks>
    Task<string?> AcquireServiceAccountTokenAsync(ITenantRepository tenantRepository, string wellKnownName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of <see cref="IServiceAccountTokenService"/> that reads client credentials
/// from a ServiceAccountConfiguration runtime entity and acquires tokens via OAuth2 client credentials grant.
/// </summary>
internal class ServiceAccountTokenService : IServiceAccountTokenService, IServiceAccountSubjectTokenSource
{
    /// <summary>
    ///     The OctoMesh delegation ("on-behalf-of") grant type. Deliberately an own URN and not the
    ///     RFC 8693 token-exchange one — the identity service binds one validator, and with it one
    ///     per-client opt-in, per grant type. Must stay byte-identical to
    ///     <c>DelegationConstants.OnBehalfOfGrantType</c> in octo-identity-services.
    /// </summary>
    internal const string OnBehalfOfGrantType = "urn:meshmakers:params:oauth:grant-type:on-behalf-of";

    /// <summary>
    ///     The OctoMesh impersonation grant type (AB#5114): the adapter's OWN confidential client
    ///     asks for a client-credentials-shaped token that runs as the service account named by
    ///     <see cref="RequestedClientIdParameterName" />, authorized by a MayActAs grant in the
    ///     identity service. Must stay byte-identical to the identity-side constant.
    /// </summary>
    internal const string ImpersonationGrantType = "urn:meshmakers:params:oauth:grant-type:impersonate";

    /// <summary>
    ///     Names the TARGET service-account client of an impersonation (and of an adapter-
    ///     authenticated on-behalf-of) request. Must stay byte-identical to the identity-side
    ///     constant.
    /// </summary>
    internal const string RequestedClientIdParameterName = "requested_client_id";

    /// <summary>
    ///     Deploy-time template token an older communication controller projects UNRESOLVED into a
    ///     configuration's <c>IssuerUri</c>. Semantically the same statement as an empty value —
    ///     "use the adapter's own installation" (AB#5115) — so it is treated exactly like one.
    ///     Compared case-insensitively.
    /// </summary>
    internal const string UnresolvedAuthorityTemplateToken = OwnIssuerPolicy.UnresolvedAuthorityTemplateToken;

    /// <summary>RFC 8693 token type identifier for an access token, sent as <c>subject_token_type</c>.</summary>
    internal const string AccessTokenTypeIdentifier = "urn:ietf:params:oauth:token-type:access_token";

    private static readonly HttpClient SharedTokenHttpClient = new();

    /// <summary>
    ///     Safety margin subtracted from a token's <c>exp</c> before it is treated as expired, so a
    ///     cached identity is never handed out for a token that dies in flight.
    /// </summary>
    private static readonly TimeSpan IdentityCacheSkew = TimeSpan.FromSeconds(60);

    private readonly HttpClient _tokenHttpClient;
    private readonly ILogger<ServiceAccountTokenService> _logger;
    private readonly AdapterOptions _adapterOptions;
    private readonly MeshAdapterConfiguration _meshAdapterConfiguration;
    private readonly ISecretAttributeProtector? _secretAttributeProtector;
    private readonly OwnIssuerPolicy _ownIssuer;
    private readonly IssuedTokenVerifier _verifier;

    /// <summary>
    ///     Cached service-account identities, keyed by <c>(TenantId, ClientId)</c> — the pair that
    ///     decides which token would be issued. Process-wide because the answer is too: several
    ///     pipelines of the same adapter share one account, and the whole point of the cache is that a
    ///     high-frequency event trigger does not pay a token round trip per execution. The impersonated
    ///     path (AB#5114) shares this cache and keys by the TARGET service-account client id, not by
    ///     the adapter's own — the issued token runs as the target, so the target is the identity.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string TenantId, string ClientId),
        ServiceAccountIdentity> _identityCache = new();

    /// <summary>
    ///     Service-account tokens handed out by <see cref="AcquireServiceAccountTokenAsync" />, keyed by
    ///     <c>(TenantId, ClientId, IssuerUri)</c> (AB#8658). Since AB#9776 the issuer is always the adapter's
    ///     own identity service, so it no longer distinguishes entries; it stays in the key so a changed
    ///     Adapter:IssuerUri can never serve a token of the previous one. Separate from <see cref="_identityCache" /> because that
    ///     one deliberately never holds a credential, and from the process-wide <c>IServiceClientAccessToken</c>
    ///     singleton because that is the adapter's own identity.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string TenantId, string ClientId,
        string IssuerUri), (string AccessToken, DateTime ExpiresAtUtc)> _accountTokenCache = new();

    public ServiceAccountTokenService(ILogger<ServiceAccountTokenService> logger,
        IOptions<AdapterOptions> adapterOptions, IOptions<MeshAdapterConfiguration> meshAdapterConfiguration,
        ISecretAttributeProtector secretAttributeProtector)
        : this(logger, SharedTokenHttpClient, adapterOptions.Value,
            meshAdapterConfiguration.Value, secretAttributeProtector)
    {
    }

    /// <summary>Test seam: lets a unit test script the token endpoint without a server.</summary>
    internal ServiceAccountTokenService(ILogger<ServiceAccountTokenService> logger, HttpClient tokenHttpClient,
        AdapterOptions? adapterOptions = null, MeshAdapterConfiguration? meshAdapterConfiguration = null,
        ISecretAttributeProtector? secretAttributeProtector = null)
    {
        _secretAttributeProtector = secretAttributeProtector;
        _logger = logger;
        _tokenHttpClient = tokenHttpClient;
        _adapterOptions = adapterOptions ?? new AdapterOptions();
        _meshAdapterConfiguration = meshAdapterConfiguration ?? new MeshAdapterConfiguration();
        _ownIssuer = new OwnIssuerPolicy(_adapterOptions, _meshAdapterConfiguration);
        _verifier = new IssuedTokenVerifier(_ownIssuer, logger);
    }

    /// <summary>
    ///     Whether <paramref name="clientSecret" /> can authenticate a token request: any non-blank
    ///     value. A blueprint that provisions the configuration without a secret leaves the attribute
    ///     empty, which means "no secret" (AB#5114). A placeholder-looking value is an ordinary secret
    ///     (decisions 2026-10-06, item 1); legacy seed placeholders stored as clear text are mapped to
    ///     "not set" when the attribute is read (<see cref="TryReadClientSecret" />).
    /// </summary>
    internal static bool IsSecretUsable(string? clientSecret)
    {
        return !string.IsNullOrWhiteSpace(clientSecret);
    }

    /// <summary>
    ///     Whether the adapter can authenticate AS ITSELF — the precondition of the impersonation
    ///     and adapter-authenticated delegation paths (AB#5114). Stricter than
    ///     <see cref="AdapterOptions.IsEnabled" /> on purpose: both grants authenticate with a client
    ///     secret, so a public adapter client cannot use them.
    /// </summary>
    private bool HasOwnClientCredentials =>
        !string.IsNullOrWhiteSpace(_adapterOptions.ClientId)
        && !string.IsNullOrWhiteSpace(_adapterOptions.ClientSecret);

    /// <inheritdoc />
    public async Task<string?> AcquireDelegatedTokenAsync(ITenantRepository tenantRepository, string wellKnownName,
        string subjectToken, CancellationToken cancellationToken = default)
    {
        // Not cached, deliberately. A delegated token is bound to ONE user, so any cache would have
        // to be keyed by (subject, configuration) — and the only key material available here is the
        // subject token itself, which must not be held in a process-wide dictionary any longer than
        // the request that carried it. The account token cache is not reused either: it is keyed by
        // (tenant, client, issuer) and holds the service account's OWN token, which is a different
        // identity from the delegated one.
        // The cost is one token round trip per AI query, against an LLM call that takes seconds.
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            _logger.LogError(
                "Delegation via ServiceAccountConfiguration '{WellKnownName}' requested without a caller token",
                wellKnownName);
            return null;
        }

        var configuration = await ReadConfigurationAsync(tenantRepository, wellKnownName);
        if (configuration == null)
        {
            return null;
        }

        var issuerUri = ResolveIssuerUri(configuration.IssuerUri, wellKnownName);
        if (issuerUri == null)
        {
            return null;
        }

        if (!TryResolveTenantId(configuration.TenantId, tenantRepository.TenantId, wellKnownName,
                out var tenantId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // The grant is same-tenant and the identity service requires acr_values=tenant:X to wire
            // the request to a tenant at all; without it the token request is rejected with a
            // message about a tenant that could not be resolved, which reads like an outage.
            // Since AB#5115 an empty TenantId on the configuration means "the adapter's tenant",
            // so this only fires when the adapter itself does not know its tenant either.
            _logger.LogError(
                "ServiceAccountConfiguration '{WellKnownName}' carries no TenantId and the adapter's own tenant is unknown; the delegation grant requires acr_values=tenant:{{tenantId}}",
                wellKnownName);
            return null;
        }

        var mode = SelectAcquisitionMode(configuration, wellKnownName);
        if (mode == null)
        {
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery throw
            // ("Malformed URL") rather than report an error — the AB#4541 shape. This method
            // promises null on failure, so it is caught here; the CALLER decides whether that is
            // fatal, and for the delegation path it is.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError("Failed to discover token endpoint at {IssuerUri} for delegation: {Error}",
                issuerUri, disco.Error);
            return null;
        }

        var tokenRequest = new TokenRequest
        {
            Address = disco.TokenEndpoint,
            GrantType = OnBehalfOfGrantType,
            Parameters =
            {
                { OidcConstants.TokenRequest.SubjectToken, subjectToken },
                { OidcConstants.TokenRequest.SubjectTokenType, AccessTokenTypeIdentifier },
                { OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{tenantId}" },
                {
                    // DefaultScopes.None → exactly "octo_api", notably WITHOUT offline_access: the
                    // role intersection is computed at issuance, so a refresh token would freeze it
                    // and keep a revoked role alive. The identity service rejects the scope outright
                    // (invalid_scope) for that reason — asking for it would fail the whole request.
                    OidcConstants.TokenRequest.Scope,
                    CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                }
            }
        };

        if (mode == AcquisitionMode.ServiceAccountSecret)
        {
            // The service account authenticates with its own client credentials; the identity
            // service proves client_id before the delegation validator ever runs.
            tokenRequest.ClientId = configuration.ClientId;
            tokenRequest.ClientSecret = configuration.ClientSecret;
        }
        else
        {
            // AB#5114: the ADAPTER authenticates with its own client credentials and names the
            // service account it acts through; a MayActAs grant in the identity service authorizes
            // the pairing. subject_token / acr semantics are unchanged.
            tokenRequest.ClientId = _adapterOptions.ClientId!;
            tokenRequest.ClientSecret = _adapterOptions.ClientSecret;
            tokenRequest.Parameters.Add(RequestedClientIdParameterName, configuration.ClientId);
        }

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Returning null rather than throwing: the caller decides whether a missing delegated
            // token is fatal (it is, for the fail-closed MCP path) — but it must decide with a
            // message, so the cause is logged here where it is known.
            _logger.LogError(ex,
                "Delegated token request to {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (response.IsError)
        {
            _logger.LogError(
                "Delegation rejected by {IssuerUri} for ServiceAccountConfiguration '{WellKnownName}' in tenant '{TenantId}': {Error} ({ErrorDescription})",
                issuerUri, wellKnownName, tenantId, response.Error,
                response.ErrorDescription ?? "no description");
            return null;
        }

        if (string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Delegation via ServiceAccountConfiguration '{WellKnownName}' succeeded but returned no access token",
                wellKnownName);
            return null;
        }

        if (await ValidateIssuedTokenAsync(response.AccessToken, disco, tenantId, wellKnownName) == null)
        {
            return null;
        }

        // 🔴 NOT written to the process-wide IServiceClientAccessToken — see the interface remarks. The token is
        // never logged either; only the fact that one was issued.
        _logger.LogInformation(
            "Delegated token acquired via ServiceAccountConfiguration '{WellKnownName}' (client {ClientId}, {Mode}) in tenant '{TenantId}', expires in {ExpiresIn}s",
            wellKnownName, configuration.ClientId, mode.Value, tenantId, response.ExpiresIn);

        return response.AccessToken;
    }

    /// <summary>
    /// Reads the <c>System.Communication/ServiceAccountConfiguration</c> entity and validates that it
    /// carries the credentials any grant needs. Returns null (after logging) when it is missing or
    /// incomplete — shared by both grant paths so they fail identically on a broken configuration.
    /// Only <c>ClientId</c> is mandatory: an empty <c>IssuerUri</c> or <c>TenantId</c> means "the
    /// adapter's own installation / tenant" since AB#5115, and an empty <c>ClientSecret</c> selects
    /// the impersonation path since AB#5114.
    /// </summary>
    private async Task<ServiceAccountCredentials?> ReadConfigurationAsync(ITenantRepository tenantRepository,
        string wellKnownName)
    {
        // Deliberately the parameterless SYSTEM session (AB#5028): this read is what ANSWERS the
        // identity question, so scoping it to the identity it is about would be circular — and the
        // configuration is platform credential material, not tenant business data.
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();
        queryOptions.AddFieldFilter("rtWellKnownName", FieldFilterOperator.Equals, wellKnownName);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            new RtCkId<CkTypeId>("System.Communication/ServiceAccountConfiguration"),
            queryOptions, take: 1);

        var configEntity = result.Items.FirstOrDefault();
        if (configEntity == null)
        {
            _logger.LogWarning("ServiceAccountConfiguration '{WellKnownName}' not found, cannot acquire token",
                wellKnownName);
            return null;
        }

        var issuerUri = configEntity.GetAttributeValueOrDefault("IssuerUri") as string;
        var clientId = configEntity.GetAttributeValueOrDefault("ClientId") as string;
        if (!TryReadClientSecret(configEntity, wellKnownName, tenantRepository.TenantId, out var clientSecret))
        {
            return null;
        }

        var tenantId = configEntity.GetAttributeValueOrDefault("TenantId") as string;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning(
                "ServiceAccountConfiguration '{WellKnownName}' carries no ClientId, cannot acquire token",
                wellKnownName);
            return null;
        }

        return new ServiceAccountCredentials(issuerUri ?? string.Empty, clientId, clientSecret, tenantId);
    }

    /// <summary>
    /// Reads <c>ClientSecret</c> of the configuration entity. Before System.Communication 3.41 it is a
    /// <c>String</c>; afterwards a <c>Secret</c> (AB#5528), read as an <see cref="RtSecretValue" />
    /// that is decrypted here, in process (a counted <c>octo.secrets.decrypt</c>). Reading the secret
    /// with <c>as string</c> would yield null, and an empty secret silently selects the impersonation
    /// path (AB#5114) — so a secret that is stored but cannot be used (no keys, unknown key id —
    /// <see cref="SecretValueState.KeyMissing" />, tampered, corrupt) fails the read instead, with an
    /// error log naming the reason or key id, never the value.
    /// </summary>
    private bool TryReadClientSecret(RtEntity configEntity, string wellKnownName, string? tenantId,
        out string? clientSecret)
    {
        clientSecret = null;
        switch (configEntity.GetAttributeValueOrDefault("ClientSecret"))
        {
            case null:
                return true;
            case string text:
                // ClientSecret is still a String attribute (System.Communication before 3.41): the stored
                // text is legacy clear text. An exact legacy seed placeholder (TODO_SET_<NAME> or a single
                // <...>) reads as not set, as the engine reads such a value in a Secret slot until the
                // migration converts it (decisions 2026-10-06, item 1).
                clientSecret = SecretAttributeConventions.IsLegacyPlaceholder(text) ? null : text;
                return true;
            case RtSecretValue when _secretAttributeProtector == null:
                _logger.LogError(
                    "ServiceAccountConfiguration '{WellKnownName}' stores ClientSecret as a Secret, but no secret protector is available, cannot acquire token",
                    wellKnownName);
                return false;
            case RtSecretValue secret:
                var accessContext = new SecretAccessContext(tenantId, configEntity.CkTypeId?.ToString(), "ClientSecret");
                var readInfo = _secretAttributeProtector.DescribeSecret(secret, accessContext);
                if (readInfo.KeyMissing && _secretAttributeProtector.IsConfigured)
                {
                    // Decisions 2026-10-06, item 2: stored, but the key id is not in this adapter's ring
                    // (restore from another environment). Unusable like an undecryptable value: no token and,
                    // deliberately, no impersonation fallback either.
                    _logger.LogError(
                        "ServiceAccountConfiguration '{WellKnownName}': ClientSecret is encrypted with key id '{KeyId}', which is not in the key ring; treated as not usable, cannot acquire token",
                        wellKnownName, readInfo.KeyId);
                    return false;
                }

                if (readInfo.Form == SecretStorageForm.Corrupt)
                {
                    // Stored, but can never be read (the engine logged a warning): unusable as well.
                    _logger.LogError(
                        "ServiceAccountConfiguration '{WellKnownName}': ClientSecret is stored in a form that cannot be read; cannot acquire token",
                        wellKnownName);
                    return false;
                }

                if (readInfo.State == SecretValueState.NotSet)
                {
                    return true;
                }

                try
                {
                    clientSecret = _secretAttributeProtector.Unprotect(secret, accessContext);
                    return true;
                }
                catch (Exception e) when (e is SecretEncryptionNotConfiguredException or UnknownSecretKeyIdException
                                              or LegacyPlaintextSecretRejectedException
                                              or SecretEnvelopeNotAllowedException
                                              or System.Security.Cryptography.CryptographicException)
                {
                    _logger.LogError(
                        "ServiceAccountConfiguration '{WellKnownName}': ClientSecret cannot be decrypted ({Reason}), cannot acquire token",
                        wellKnownName, e.GetType().Name);
                    return false;
                }
            default:
                _logger.LogWarning(
                    "ServiceAccountConfiguration '{WellKnownName}': ClientSecret has an unexpected type, cannot acquire token",
                    wellKnownName);
                return false;
        }
    }

    /// <inheritdoc />
    public async Task<ServiceAccountIdentity?> AcquireServiceAccountIdentityAsync(
        ServiceAccountCredentials credentials, string pipelineTenantId, CancellationToken cancellationToken = default)
    {
        // AB#9776: the pipeline's tenant is an explicit input and the only tenant the identity is resolved for.
        // credentials.TenantId is checked against it, never trusted on its own — fail closed when it is unknown.
        if (string.IsNullOrWhiteSpace(pipelineTenantId))
        {
            _logger.LogError(
                "The identity of service account client {ClientId} cannot be resolved: the pipeline's tenant is unknown (AB#9776)",
                credentials.ClientId);
            return null;
        }

        if (!TryResolveTenantId(credentials.TenantId, pipelineTenantId, credentials.ClientId, out var tenantId))
        {
            return null;
        }

        // Checked BEFORE the cache: a configuration naming a foreign issuer is refused even when an
        // identity for the same (tenant, client) is cached (AB#9776).
        var issuerUri = ResolveIssuerUri(credentials.IssuerUri, credentials.ClientId);
        if (issuerUri == null)
        {
            return null;
        }

        var cacheKey = (tenantId ?? string.Empty, credentials.ClientId);
        if (_identityCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached;
        }

        var mode = SelectAcquisitionMode(credentials, credentials.ClientId);
        if (mode == null)
        {
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri left behind by a blueprint re-apply makes discovery THROW
            // ("Malformed URL") rather than report an error — the AB#4541 shape.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                issuerUri, credentials.ClientId);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError(
                "Failed to discover token endpoint at {IssuerUri} while resolving the identity of service account client {ClientId}: {Error}",
                issuerUri, credentials.ClientId, disco.Error);
            return null;
        }

        var tokenRequest = CreateAmbientTokenRequest(disco.TokenEndpoint, credentials, tenantId, mode.Value);

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Token request to {IssuerUri} failed while resolving the identity of service account client {ClientId}",
                issuerUri, credentials.ClientId);
            return null;
        }

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Identity service {IssuerUri} refused a token for service account client {ClientId} in tenant '{TenantId}': {Error} ({ErrorDescription})",
                issuerUri, credentials.ClientId, tenantId,
                response.Error ?? "no token", response.ErrorDescription ?? "no description");
            return null;
        }

        // AB#9776: signature, issuer and tenant are verified before a single claim is used. An opaque
        // (reference) token carries no readable claims and is refused as well: reporting no identity is
        // the only honest answer — inventing an empty-role one would look like a correctly resolved
        // account with no permissions, which fails silently everywhere downstream.
        var claims = await ValidateIssuedTokenAsync(response.AccessToken, disco, tenantId, credentials.ClientId);
        if (claims == null)
        {
            _logger.LogError(
                "The identity of service account client {ClientId} cannot be resolved: the issued token was refused",
                credentials.ClientId);
            return null;
        }

        // A client-credentials token has no 'sub'; 'client_id' is the subject the engine stamps and
        // filters on then — the same precedence RuntimeSecurityContextResolver uses in octo-mcp-service.
        // An impersonated token (AB#5114) is CC-shaped for the TARGET account, so the same read applies;
        // credentials.ClientId (the target) stays the last-resort fallback either way.
        var subjectId = claims.Subject ?? claims.ClientId ?? credentials.ClientId;

        // The token's own exp is the truth; expires_in is the fallback for an issuer that omits it.
        var expiresAt = (claims.ExpiresAtUtc ?? DateTime.UtcNow.AddSeconds(response.ExpiresIn)) - IdentityCacheSkew;

        var identity = new ServiceAccountIdentity(subjectId, claims.Roles, expiresAt);

        if (identity.Roles.Count == 0)
        {
            // Not an error — but an account with no roles produces an empty role set everywhere
            // downstream, and every role-gated read then returns nothing while every log stays quiet.
            // See the AB#5027 note in octo-communication-controller-services/CLAUDE.md.
            _logger.LogWarning(
                "Service account client {ClientId} in tenant '{TenantId}' carries no roles; pipelines running under it see only data that needs none",
                credentials.ClientId, tenantId);
        }
        else
        {
            _logger.LogInformation(
                "Resolved service account identity for client {ClientId} in tenant '{TenantId}': subject {SubjectId} with {RoleCount} role(s)",
                credentials.ClientId, tenantId, subjectId, identity.Roles.Count);
        }

        _identityCache[cacheKey] = identity;
        return identity;
    }

    /// <inheritdoc />
    public async Task<string?> AcquireServiceAccountTokenAsync(ITenantRepository tenantRepository,
        string wellKnownName, CancellationToken cancellationToken = default)
    {
        var configuration = await ReadConfigurationAsync(tenantRepository, wellKnownName);
        if (configuration == null)
        {
            return null;
        }

        return await AcquireAccountTokenCoreAsync(configuration, tenantRepository.TenantId, wellKnownName,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> AcquireSubjectTokenAsync(ServiceAccountCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        // credentials.TenantId is the pipeline's tenant: ServiceAccountConfigurationReader refuses a configuration
        // naming another tenant before it gets here (AB#9776).
        return AcquireAccountTokenCoreAsync(credentials, credentials.TenantId, credentials.ClientId,
            cancellationToken);
    }

    /// <summary>
    ///     The account's own token for <paramref name="configuration" /> (shared by
    ///     <see cref="AcquireServiceAccountTokenAsync" /> and <see cref="AcquireSubjectTokenAsync" />): own issuer
    ///     only, tenant of the pipeline, verified before it is cached or returned (AB#9776).
    /// </summary>
    private async Task<string?> AcquireAccountTokenCoreAsync(ServiceAccountCredentials configuration,
        string? pipelineTenantId, string wellKnownName, CancellationToken cancellationToken)
    {
        // The issuer resolves without network access; it is part of the cache key so two configurations
        // with the same tenant and client id but different issuers never share a token.
        var issuerUri = ResolveIssuerUri(configuration.IssuerUri, wellKnownName);
        if (issuerUri == null)
        {
            return null;
        }

        if (!TryResolveTenantId(configuration.TenantId, pipelineTenantId, wellKnownName,
                out var tenantId))
        {
            return null;
        }

        var cacheKey = (tenantId ?? string.Empty, configuration.ClientId, issuerUri);
        if (_accountTokenCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.AccessToken;
        }

        var mode = SelectAcquisitionMode(configuration, wellKnownName);
        if (mode == null)
        {
            return null;
        }

        DiscoveryDocumentResponse disco;
        try
        {
            disco = await _tokenHttpClient.GetDiscoveryDocumentAsync(CreateDiscoveryRequest(issuerUri),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A placeholder IssuerUri makes discovery THROW ("Malformed URL") — the AB#4541 shape.
            _logger.LogError(ex,
                "OIDC discovery at {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (disco.IsError)
        {
            _logger.LogError(
                "Failed to discover token endpoint at {IssuerUri} for ServiceAccountConfiguration '{WellKnownName}': {Error}",
                issuerUri, wellKnownName, disco.Error);
            return null;
        }

        var tokenRequest = CreateAmbientTokenRequest(disco.TokenEndpoint, configuration, tenantId, mode.Value);

        TokenResponse response;
        try
        {
            response = await _tokenHttpClient.RequestTokenAsync(tokenRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Token request to {IssuerUri} failed for ServiceAccountConfiguration '{WellKnownName}'",
                issuerUri, wellKnownName);
            return null;
        }

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken))
        {
            _logger.LogError(
                "Identity service {IssuerUri} refused a token for ServiceAccountConfiguration '{WellKnownName}' (client {ClientId}) in tenant '{TenantId}': {Error} ({ErrorDescription})",
                issuerUri, wellKnownName, configuration.ClientId, tenantId,
                response.Error ?? "no token", response.ErrorDescription ?? "no description");
            return null;
        }

        // AB#9776: only a verified token of the own issuer for the pipeline's tenant is handed out.
        var claims = await ValidateIssuedTokenAsync(response.AccessToken, disco, tenantId, wellKnownName);
        if (claims == null)
        {
            return null;
        }

        // The token's own exp is the truth; expires_in is the fallback. Never logged — only the fact.
        var expiresAt = (claims.ExpiresAtUtc ?? DateTime.UtcNow.AddSeconds(response.ExpiresIn)) - IdentityCacheSkew;
        _accountTokenCache[cacheKey] = (response.AccessToken, expiresAt);

        _logger.LogInformation(
            "Service account token acquired via ServiceAccountConfiguration '{WellKnownName}' (client {ClientId}, {Mode}) in tenant '{TenantId}'",
            wellKnownName, configuration.ClientId, mode.Value, tenantId);
        return response.AccessToken;
    }

    /// <summary>How the token for a service account is obtained (AB#5114).</summary>
    internal enum AcquisitionMode
    {
        /// <summary>The account authenticates itself with the secret stored on the configuration.</summary>
        ServiceAccountSecret,

        /// <summary>
        ///     The adapter authenticates with its OWN client credentials and names the account via
        ///     <c>requested_client_id</c>; a MayActAs grant authorizes the pairing.
        /// </summary>
        Impersonation
    }

    /// <summary>
    ///     Decides between the two acquisition paths — the ONE decision every grant path shares
    ///     (AB#5114): a usable secret on the configuration keeps the pre-AB#5114 behaviour
    ///     byte-for-byte; without one the adapter's own credentials carry the request through the
    ///     impersonation / adapter-authenticated delegation grants. Returns null (after logging the
    ///     one actionable error) when neither is possible.
    /// </summary>
    private AcquisitionMode? SelectAcquisitionMode(ServiceAccountCredentials configuration, string configurationName)
    {
        if (IsSecretUsable(configuration.ClientSecret))
        {
            return AcquisitionMode.ServiceAccountSecret;
        }

        if (HasOwnClientCredentials)
        {
            _logger.LogDebug(
                "ServiceAccountConfiguration '{ConfigurationName}' carries no usable ClientSecret; impersonating client {TargetClientId} with the adapter's own identity '{OwnClientId}' (AB#5114)",
                configurationName, configuration.ClientId, _adapterOptions.ClientId);
            return AcquisitionMode.Impersonation;
        }

        _logger.LogError(
            "ServiceAccountConfiguration '{ConfigurationName}' (client {ClientId}) carries no usable ClientSecret and the adapter has no identity of its own (Adapter:ClientId / Adapter:ClientSecret are not configured). Either store a ClientSecret on the configuration, or give the adapter its own client credentials plus a MayActAs grant in the identity service so it can impersonate the service account (AB#5114)",
            configurationName, configuration.ClientId);
        return null;
    }

    /// <summary>
    ///     Builds the token request of the two AMBIENT paths (<see cref="AcquireServiceAccountTokenAsync" /> and
    ///     <see cref="AcquireServiceAccountIdentityAsync" />): plain client credentials when the
    ///     configuration carries a usable secret, the impersonation grant (AB#5114) when the
    ///     adapter's own identity carries the request. Both responses are CC-shaped tokens for the
    ///     service account, which is why the two callers can treat them identically.
    /// </summary>
    private TokenRequest CreateAmbientTokenRequest(string? tokenEndpoint, ServiceAccountCredentials configuration,
        string? tenantId, AcquisitionMode mode)
    {
        var tokenRequest = new TokenRequest
        {
            Address = tokenEndpoint,
            Parameters =
            {
                {
                    OidcConstants.TokenRequest.Scope,
                    CommonConstants.GetScopes(ApiScopes.OctoApiFullAccess, null, DefaultScopes.None)
                }
            }
        };

        if (mode == AcquisitionMode.ServiceAccountSecret)
        {
            tokenRequest.GrantType = OidcConstants.GrantTypes.ClientCredentials;
            tokenRequest.ClientId = configuration.ClientId;
            tokenRequest.ClientSecret = configuration.ClientSecret;
        }
        else
        {
            tokenRequest.GrantType = ImpersonationGrantType;
            tokenRequest.ClientId = _adapterOptions.ClientId!;
            tokenRequest.ClientSecret = _adapterOptions.ClientSecret;
            tokenRequest.Parameters.Add(RequestedClientIdParameterName, configuration.ClientId);
        }

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tokenRequest.Parameters.Add(OidcConstants.AuthorizeRequest.AcrValues, $"tenant:{tenantId}");
        }

        return tokenRequest;
    }

    /// <summary>
    ///     Resolves the address every token request goes to: always the adapter's OWN identity service
    ///     (AB#9776, decision "own issuer only"). The <c>IssuerUri</c> of the configuration is only
    ///     CHECKED — empty, the unresolved <c>{{service.authority}}</c> token (AB#5115) or one of the
    ///     own names (<see cref="OwnIssuerPolicy" />) — and never used as the address, so neither the
    ///     service account's secret nor the adapter's own client credentials can reach another issuer.
    ///     A configuration naming any other issuer is refused here, before any request leaves the
    ///     adapter. Returns null (after logging) when it is refused or when no own issuer is configured.
    /// </summary>
    private string? ResolveIssuerUri(string? configuredIssuerUri, string configurationName)
    {
        if (!_ownIssuer.IsOwnConfiguredIssuer(configuredIssuerUri))
        {
            _logger.LogError(
                "ServiceAccountConfiguration '{ConfigurationName}' names the issuer '{IssuerUri}', which is not this adapter's own identity service (Adapter:IssuerUri, Adapter:AuthorityUrl, Adapter:AdditionalValidIssuers). Service-account tokens are accepted only from the own issuer; no request was sent. Leave IssuerUri empty or set it to {{{{service.authority}}}} (AB#9776)",
                configurationName, configuredIssuerUri);
            return null;
        }

        if (_ownIssuer.Endpoint == null)
        {
            _logger.LogError(
                "No issuer for service account configuration '{ConfigurationName}': its IssuerUri means 'the adapter's own installation' (AB#5115), but Adapter:IssuerUri (the adapter's own identity, AB#5072) and Adapter:AuthorityUrl (the bearer-validation authority) are empty. Configure one of them",
                configurationName);
            return null;
        }

        _logger.LogDebug(
            "Issuer for service account configuration '{ConfigurationName}' is the adapter's own identity service: {IssuerUri}",
            configurationName, _ownIssuer.Endpoint);
        return _ownIssuer.Endpoint;
    }

    /// <summary>
    ///     Resolves the tenant a token request acts in (AB#5115, AB#9776): the tenant of the pipeline
    ///     (<paramref name="pipelineTenantId" />) when it is known, else the configuration's own
    ///     <c>TenantId</c>, else the adapter's own tenant. A configured <c>TenantId</c> other than the
    ///     pipeline's tenant is refused (returns <c>false</c> after logging): its roles would be applied
    ///     to the pipeline's repository (until a cross-tenant exchange exists, AB#9777). The comparison
    ///     ignores case and surrounding blanks, as tenant ids do elsewhere in the adapter; on a match the
    ///     pipeline's own spelling is used. <paramref name="tenantId" /> is null only when no tenant is
    ///     known at all — the caller decides whether that is fatal (it is for every grant path).
    /// </summary>
    private bool TryResolveTenantId(string? configuredTenantId, string? pipelineTenantId, string configurationName,
        out string? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(pipelineTenantId))
        {
            if (!string.IsNullOrWhiteSpace(configuredTenantId)
                && !string.Equals(configuredTenantId.Trim(), pipelineTenantId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "ServiceAccountConfiguration '{ConfigurationName}' names the tenant '{ConfiguredTenantId}', but the pipeline runs in tenant '{PipelineTenantId}'. A service account acts only in the pipeline's own tenant; no request was sent. Leave TenantId empty or set it to the pipeline's tenant (AB#9776)",
                    configurationName, configuredTenantId, pipelineTenantId);
                tenantId = null;
                return false;
            }

            tenantId = pipelineTenantId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuredTenantId))
        {
            tenantId = configuredTenantId;
            return true;
        }

        // AB#4924: the adapter's OWN tenant, which is a process-level fact — this is the
        // AB#5115 fallback for a service-account configuration that names no tenant, not the
        // tenant of any execution. Null on a pool member, which has no tenant of its own, so the
        // fallback below correctly declines rather than silently picking somebody else's tenant.
        tenantId = string.IsNullOrWhiteSpace(_adapterOptions.DedicatedTenantId)
            ? null
            : _adapterOptions.DedicatedTenantId;
        if (tenantId != null)
        {
            _logger.LogDebug(
                "Service account configuration '{ConfigurationName}' carries no TenantId; using the adapter's own tenant '{TenantId}' (AB#5115)",
                configurationName, tenantId);
        }

        return true;
    }

    /// <summary>
    ///     Checks a token the own identity service issued before any of its claims is used or the token
    ///     is handed out (AB#9776): signature against the keys of <paramref name="discovery" />, own
    ///     issuer, an asymmetric signature algorithm
    ///     (<see cref="IssuedTokenVerifier.AcceptedSigningAlgorithms" />) and <c>tenant_id</c> ==
    ///     <paramref name="tenantId" />. Delegates to the shared <see cref="IssuedTokenVerifier" /> (also used by the cross-tenant proof, AB#9767). Lifetime and
    ///     audience are not checked here: the token is minutes old, and the audience is the downstream
    ///     service's business. Returns null (after logging; the token itself is never logged).
    /// </summary>
    private Task<JwtPayloadReader.JwtClaims?> ValidateIssuedTokenAsync(string accessToken,
        DiscoveryDocumentResponse discovery, string? tenantId, string configurationName)
    {
        return _verifier.VerifyAsync(accessToken, discovery, tenantId,
            $"service account configuration '{configurationName}'");
    }

    /// <summary>
    ///     Discovery request that tolerates a split-horizon issuer: in hybrid installations the
    ///     adapter reaches the identity service under a cluster-visible host (e.g.
    ///     <c>https://mac.local:5003</c> from a local kind pod) while the identity service mints a
    ///     FIXED issuer under its canonical host (<c>https://localhost:5003/</c>). IdentityModel's
    ///     default policy rejects that as "Issuer name does not match authority" — but the
    ///     configured IssuerUri is trusted by definition here (we send the client secret to it),
    ///     TLS still authenticates the host, and the same mismatch is already accepted on the
    ///     validation side via AdditionalValidIssuers (AB#4922).
    ///     <para>
    ///         Endpoint-authority validation is off for the same reason: the identity service
    ///         legitimately emits MIXED endpoint hosts in one split-horizon document — most
    ///         endpoints derived from the request host (e.g. <c>https://mac.local:5003</c>) but
    ///         some, at least <c>check_session_iframe</c>, from the fixed canonical issuer
    ///         (<c>https://localhost:5003</c>). With the default policy IdentityModel rejects that
    ///         as "Endpoint is on a different host than authority". Since the document comes from
    ///         the trusted, TLS-authenticated IssuerUri anyway, endpoint-host consistency checks
    ///         add nothing here either.
    ///     </para>
    /// </summary>
    private static DiscoveryDocumentRequest CreateDiscoveryRequest(string issuerUri)
    {
        return new DiscoveryDocumentRequest
        {
            Address = issuerUri,
            Policy = { ValidateIssuerName = false, ValidateEndpoints = false }
        };
    }

}
