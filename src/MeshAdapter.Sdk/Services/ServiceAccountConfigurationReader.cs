using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
///     Reads the pipeline's service account from its configuration list (AB#5027): the one reader the identity resolver
///     (AB#5028) and the cross-tenant proof (AB#9767) share, so both act as the same account.
/// </summary>
internal static class ServiceAccountConfigurationReader
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

    /// <summary>
    ///     Picks the service account out of the pipeline's configuration list. The controller already
    ///     guarantees at most one (a per-pipeline override wins over the adapter default, AB#5027);
    ///     a hand-linked pipeline can still carry several, and the tie is broken the same way the
    ///     controller breaks it — deterministically, so every pod and every redeploy agrees.
    /// </summary>
    /// <returns>The credentials with <c>TenantId</c> = <paramref name="tenantId" />, or <c>null</c> when none is configured.</returns>
    /// <exception cref="MeshAdapterPipelineExecutionException">A configuration names another tenant (AB#9776).</exception>
    internal static ServiceAccountCredentials? TryRead(IGlobalConfiguration globalConfiguration, string tenantId,
        ILogger logger)
    {
        List<string> rawConfigurations;
        try
        {
            rawConfigurations = globalConfiguration
                .GetAllRawJsonByCkTypeId(ServiceAccountConfigurationCkTypeId)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[{TenantId}] Could not read the pipeline's service account configuration", tenantId);
            return null;
        }

        var candidates = rawConfigurations
            .Select(raw => TryParseCredentials(raw, tenantId, logger))
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
            logger.LogWarning(
                "[{TenantId}] Pipeline carries {Count} service account configurations; acting as '{ChosenClientId}'. Link exactly one to make the identity unambiguous (AB#5027).",
                tenantId, candidates.Count, candidates[0].ClientId);
        }

        return candidates[0];
    }

    private static ServiceAccountCredentials? TryParseCredentials(string rawJson, string tenantId, ILogger logger)
    {
        ServiceAccountConfigurationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ServiceAccountConfigurationPayload>(rawJson,
                ConfigurationJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "[{TenantId}] A service account configuration of this pipeline could not be read", tenantId);
            return null;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.ClientId))
        {
            // Only ClientId is mandatory (AB#5115): an empty IssuerUri means "the adapter's own
            // installation" and an empty ClientSecret selects the impersonation path (AB#5114) —
            // both are resolved downstream by the token service, not repair-worthy defects here.
            logger.LogWarning(
                "[{TenantId}] A service account configuration of this pipeline carries no ClientId and is ignored",
                tenantId);
            return null;
        }

        // The grant needs acr_values=tenant:X, and it is always the PIPELINE's tenant (AB#9776): the roles
        // of the token are applied to this pipeline's repository, so a configuration naming another tenant
        // is refused — thrown, never skipped, because skipping it could widen the execution to System.
        // Case and surrounding blanks are ignored like everywhere tenant ids are compared in the adapter.
        if (!string.IsNullOrWhiteSpace(payload.TenantId)
            && !string.Equals(payload.TenantId.Trim(), tenantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "[{TenantId}] Service account configuration of client '{ClientId}' names the tenant '{ConfiguredTenantId}'; a service account acts only in the pipeline's own tenant (AB#9776)",
                tenantId, payload.ClientId, payload.TenantId);
            throw MeshAdapterPipelineExecutionException.ServiceAccountTenantMismatch(tenantId, payload.ClientId!,
                payload.TenantId);
        }

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

/// <summary>
///     The service account's own, verified client-credentials token of the pipeline's tenant, as the subject of the
///     cross-tenant exchange (AB#9767/AB#9777). Implemented by <see cref="ServiceAccountTokenService" />; internal, so
///     the public <see cref="IServiceAccountTokenService" /> stays unchanged.
/// </summary>
internal interface IServiceAccountSubjectTokenSource
{
    /// <summary>
    ///     The account's token (own issuer only, <c>tenant_id</c> = <see cref="ServiceAccountCredentials.TenantId" />,
    ///     signature verified), or <c>null</c> when it cannot be acquired (the cause is logged). Credential material:
    ///     never log, store or return it beyond the exchange request.
    /// </summary>
    Task<string?> AcquireSubjectTokenAsync(ServiceAccountCredentials credentials,
        CancellationToken cancellationToken = default);
}
