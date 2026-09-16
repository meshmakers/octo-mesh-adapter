using System.Text.Json;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Answers, once per pipeline execution, <b>which identity that execution acts as</b> (AB#5028).
///     Consumed by <see cref="IMeshEtlContext.GetScopedSessionAsync" />.
/// </summary>
public interface IPipelineIdentityResolver
{
    /// <summary>
    ///     The effective <see cref="RtSecurityContext" /> of this execution. Resolved lazily on the
    ///     first call and memoised — many executions never open a session at all.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask<RtSecurityContext> ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The <see cref="RtSecurityContext" /> of the pipeline's <b>effective service account</b>
    ///     with its <b>full roles</b>, <b>ignoring any verified caller</b> (AB#5127). This is the
    ///     identity a node opts into with <c>identity: ServiceAccount</c>: the elevation that runs the
    ///     node as the service account even though the execution was invoke-gated as a user. Falls
    ///     back to <see cref="RtSecurityContext.System" /> only when no service account is configured
    ///     (the same legacy tenants <see cref="ResolveAsync" /> falls back for); a configured account
    ///     whose token cannot be acquired fails the execution, never falls open to System.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask<RtSecurityContext> ResolveServiceAccountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="IPipelineIdentityResolver" />. Built per execution by
///     <see cref="MeshContextCreatorService" /> — the single point every execution flows through.
/// </summary>
/// <remarks>
///     <para><b>Precedence</b></para>
///     <list type="number">
///         <item>
///             The trigger-verified caller (<see cref="VerifiedPrincipal" />, AB#4975). Free — it is
///             already on the execution options — and it is the most specific identity there is.
///         </item>
///         <item>
///             The adapter's / pipeline's own service account (AB#5027). Its credentials are projected
///             into the pipeline's <see cref="IGlobalConfiguration" /> by the communication controller,
///             so resolving it costs no repository read; the <b>roles</b> however only exist on the
///             issued token and cost one cached token round trip.
///         </item>
///         <item><see cref="RtSecurityContext.System" />, for a tenant with no service account yet.</item>
///     </list>
///     <para><b>Failure semantics: fail-closed once an account is configured.</b></para>
///     A configured service account is an explicit statement about which identity this pipeline runs
///     as. Substituting <see cref="RtSecurityContext.System" /> when its token cannot be acquired would
///     fail <i>open</i> — the system context bypasses data-level permissions entirely (AB#4969), so an
///     identity-service outage would silently widen every read and leave every write unstamped, and
///     nothing downstream could tell that apart from a correctly restricted run. So a failed
///     acquisition throws and the execution fails, loudly and repairably.
///     <para>
///         The System fallback survives only where nothing was configured: that is the pre-AB#5027
///         fleet and every tenant until provisioning has run, and there the behaviour is exactly what
///         it was before this work item — a change of behaviour there would take the whole fleet down.
///     </para>
/// </remarks>
internal sealed class PipelineIdentityResolver : IPipelineIdentityResolver
{
    /// <summary>
    ///     The CK type whose configurations carry service-account credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠️ Matched against <c>ConfigurationTypeId.SemanticVersionedFullName</c>, which appends
    ///     <c>-N</c> as soon as the CK <b>type</b> version passes 1. A type bump would therefore make
    ///     this match silently stop finding the configuration and every pipeline fall back to the
    ///     system context — the failure would look like "nothing happened", not like an error. A CK
    ///     bump of this type therefore has to be paired with a change here.
    /// </remarks>
    internal const string ServiceAccountConfigurationCkTypeId = "System.Communication/ServiceAccountConfiguration";

    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _tenantId;
    private readonly VerifiedPrincipal? _verifiedPrincipal;
    private readonly IGlobalConfiguration _globalConfiguration;
    private readonly IServiceAccountTokenService _serviceAccountTokenService;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RtSecurityContext? _resolved;
    private RtSecurityContext? _resolvedServiceAccount;

    public PipelineIdentityResolver(string tenantId, VerifiedPrincipal? verifiedPrincipal,
        IGlobalConfiguration globalConfiguration, IServiceAccountTokenService serviceAccountTokenService,
        ILogger logger)
    {
        _tenantId = tenantId;
        _verifiedPrincipal = verifiedPrincipal;
        _globalConfiguration = globalConfiguration;
        _serviceAccountTokenService = serviceAccountTokenService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<RtSecurityContext> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // The fast path matters: a scoped node inside a ForEach opens a session per item.
        if (_resolved != null)
        {
            return _resolved;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Two nodes of one execution can race here; the gate makes the token round trip happen once.
            _resolved ??= await ResolveCoreAsync(cancellationToken);
            return _resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RtSecurityContext> ResolveServiceAccountAsync(
        CancellationToken cancellationToken = default)
    {
        // Memoised independently of the caller-aware ResolveAsync: a pipeline can mix `identity: Caller`
        // and `identity: ServiceAccount` nodes and each must get its own decision. When there is no
        // verified caller the two happen to agree, and the token service's own identity cache (AB#5028)
        // keeps the second acquisition a cache hit either way.
        if (_resolvedServiceAccount != null)
        {
            return _resolvedServiceAccount;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _resolvedServiceAccount ??= await ResolveServiceAccountOrSystemCoreAsync(cancellationToken);
            return _resolvedServiceAccount;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RtSecurityContext> ResolveCoreAsync(CancellationToken cancellationToken)
    {
        if (_verifiedPrincipal != null)
        {
            _logger.LogDebug(
                "[{TenantId}] Pipeline execution acts as the verified caller '{SubjectId}' with {RoleCount} role(s)",
                _tenantId, _verifiedPrincipal.SubjectId, _verifiedPrincipal.Roles.Count);
            return RtSecurityContext.ForUser(_verifiedPrincipal.SubjectId, _verifiedPrincipal.Roles);
        }

        // No caller: the effective identity IS the service account (or the system context when none is
        // configured) — the same result `identity: ServiceAccount` asks for explicitly.
        return await ResolveServiceAccountOrSystemCoreAsync(cancellationToken);
    }

    /// <summary>
    ///     Resolves the pipeline's effective service account, <b>independent of any caller</b>:
    ///     the SA's full-role context, or the system context when nothing is configured. The
    ///     fail-closed contract of <see cref="ResolveCoreAsync" /> applies unchanged — a configured
    ///     account whose token cannot be acquired throws rather than widening to System.
    /// </summary>
    private async Task<RtSecurityContext> ResolveServiceAccountOrSystemCoreAsync(
        CancellationToken cancellationToken)
    {
        var credentials = TryReadServiceAccountCredentials();
        if (credentials == null)
        {
            _logger.LogDebug(
                "[{TenantId}] Pipeline execution has no service account configured and acts as the system context",
                _tenantId);
            return RtSecurityContext.System;
        }

        var identity = await _serviceAccountTokenService
            .AcquireServiceAccountIdentityAsync(credentials, cancellationToken);

        if (identity == null)
        {
            // Fail-closed — see the class remarks. The cause is already logged by the token service.
            throw MeshAdapterPipelineExecutionException.ServiceAccountIdentityUnavailable(
                _tenantId, credentials.ClientId);
        }

        return RtSecurityContext.ForUser(identity.SubjectId, identity.Roles);
    }

    /// <summary>
    ///     Picks the service account out of the pipeline's configuration list. The controller already
    ///     guarantees at most one (a per-pipeline override wins over the adapter default, AB#5027);
    ///     a hand-linked pipeline can still carry several, and the tie is broken the same way the
    ///     controller breaks it — deterministically, so every pod and every redeploy agrees.
    /// </summary>
    private ServiceAccountCredentials? TryReadServiceAccountCredentials()
    {
        List<string> rawConfigurations;
        try
        {
            rawConfigurations = _globalConfiguration
                .GetAllRawJsonByCkTypeId(ServiceAccountConfigurationCkTypeId)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{TenantId}] Could not read the pipeline's service account configuration", _tenantId);
            return null;
        }

        var candidates = rawConfigurations
            .Select(TryParseCredentials)
            .Where(c => c != null)
            .Select(c => c!)
            .OrderBy(c => c.ClientId, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count > 1)
        {
            _logger.LogWarning(
                "[{TenantId}] Pipeline carries {Count} service account configurations; acting as '{ChosenClientId}'. Link exactly one to make the identity unambiguous (AB#5027).",
                _tenantId, candidates.Count, candidates[0].ClientId);
        }

        return candidates[0];
    }

    private ServiceAccountCredentials? TryParseCredentials(string rawJson)
    {
        ServiceAccountConfigurationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ServiceAccountConfigurationPayload>(rawJson,
                ConfigurationJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "[{TenantId}] A service account configuration of this pipeline could not be read", _tenantId);
            return null;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.ClientId))
        {
            // Only ClientId is mandatory (AB#5115): an empty IssuerUri means "the adapter's own
            // installation" and an empty ClientSecret selects the impersonation path (AB#5114) —
            // both are resolved downstream by the token service, not repair-worthy defects here.
            _logger.LogWarning(
                "[{TenantId}] A service account configuration of this pipeline carries no ClientId and is ignored",
                _tenantId);
            return null;
        }

        // The grant needs acr_values=tenant:X; the configuration's own TenantId is authoritative, and
        // the pipeline's tenant is the only sane default when the attribute was never filled in.
        var tenantId = string.IsNullOrWhiteSpace(payload.TenantId) ? _tenantId : payload.TenantId;

        return new ServiceAccountCredentials(payload.IssuerUri ?? string.Empty, payload.ClientId!,
            payload.ClientSecret, tenantId);
    }

    /// <summary>
    ///     The serialized CK entity as it reaches the pipeline. Every declared attribute is a present
    ///     key and an unset optional one carries <c>null</c>, so every member is nullable here.
    /// </summary>
    private sealed record ServiceAccountConfigurationPayload
    {
        public string? IssuerUri { get; init; }
        public string? ClientId { get; init; }
        public string? ClientSecret { get; init; }
        public string? TenantId { get; init; }
    }
}
