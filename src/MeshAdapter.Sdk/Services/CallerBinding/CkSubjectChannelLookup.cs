using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Tenant-repository-backed <see cref="ISubjectChannelLookup" /> (AB#5152). Reads the AB#5122
///     verified-identifier directory in the REVERSE direction of <see cref="CkPhoneUserLookup" /> /
///     <see cref="CkEntraIdUserLookup" />: loads the <c>System.Identity/User</c> by its runtime id
///     (the subjectId) and walks the INBOUND <c>IdentifiesUser</c> associations to the user's
///     <c>VerifiedExternalIdentifier</c> bindings. Same generic CK entity access as the siblings —
///     the identity CK model lives in the tenant's own runtime database, so no service-to-service
///     call is needed, and the adapter SDK addresses type ids, attribute names and association
///     roles by their (non-versioned) full names.
/// </summary>
internal sealed class CkSubjectChannelLookup(
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    ILogger<CkSubjectChannelLookup> logger) : ISubjectChannelLookup
{
    private static readonly RtCkId<CkTypeId> UserTypeId = new("System.Identity/User");

    private static readonly RtCkId<CkAssociationRoleId> IdentifiesUserRoleId =
        new("System.Identity/IdentifiesUser");

    public async Task<SubjectChannelRecord?> FindBySubjectIdAsync(string tenantId, string subjectId,
        CancellationToken cancellationToken = default)
    {
        var tenantRepository = await systemContext.TryFindTenantRepositoryAsync(tenantId);
        if (tenantRepository == null)
        {
            logger.LogDebug("[{TenantId}] No tenant repository; cannot resolve subjectId", tenantId);
            return null;
        }

        OctoObjectId userRtId;
        try
        {
            userRtId = new OctoObjectId(subjectId);
        }
        catch (Exception)
        {
            // Not a runtime id (e.g. a cross-tenant provisioning source user id, AB#4661) —
            // unresolved, the caller falls back to e-mail.
            logger.LogDebug("[{TenantId}] SubjectId '{SubjectId}' is no runtime id; unresolved",
                tenantId, subjectId);
            return null;
        }

        try
        {
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var userEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(UserTypeId, userRtId));
            if (userEntity == null)
            {
                await session.CommitTransactionAsync();
                logger.LogDebug("[{TenantId}] No user with subjectId '{SubjectId}'", tenantId, subjectId);
                return null;
            }

            var identifiers = await GetValidIdentifiersAsync(tenantRepository, session, userEntity);
            await session.CommitTransactionAsync();

            return new SubjectChannelRecord(
                userEntity.RtId.ToString(),
                userEntity.GetAttributeValueOrDefault("Email") as string,
                userEntity.GetAttributeValueOrDefault("UserName") as string,
                ReadPreferredChannelBindingId(userEntity),
                PreferredChannelResolver.ReadLegacyPreferredChannel(userEntity),
                identifiers);
        }
        catch (CkCacheException ex)
        {
            // The tenant has no System.Identity model imported: unresolved, not an error — the
            // consumer applies the e-mail fallback.
            logger.LogDebug(ex,
                "[{TenantId}] System.Identity model not available; subjectId is unresolved", tenantId);
            return null;
        }
    }

    /// <summary>
    ///     Walks the INBOUND <c>IdentifiesUser</c> associations (binding → user is declared on the
    ///     binding, so from the user's side the bindings are the association ORIGINS) and returns
    ///     every valid, non-expired binding. An expired binding (<c>ValidUntil</c> passed) is
    ///     dropped here — the "preference respected only with a valid binding" rule (AB#5152) —
    ///     mirroring <see cref="CkPhoneUserLookup" />'s expiry treatment of the forward direction.
    /// </summary>
    private static async Task<IReadOnlyList<SubjectChannelIdentifier>> GetValidIdentifiersAsync(
        ITenantRepository tenantRepository, IOctoSession session, RtEntity userEntity)
    {
        var associations = await tenantRepository.GetRtAssociationsAsync(session,
            userEntity.ToRtEntityId(),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, roleId: IdentifiesUserRoleId));

        var identifiers = new List<SubjectChannelIdentifier>();
        foreach (var association in associations.Items)
        {
            var binding = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(association.OriginCkTypeId, association.OriginRtId));
            if (binding == null || IsExpired(binding))
            {
                continue;
            }

            if (binding.GetAttributeValueOrDefault<int>("IdentifierKind") is not { } kindKey ||
                !Enum.IsDefined(typeof(ChannelIdentifierKind), kindKey))
            {
                continue;
            }

            if (binding.GetAttributeValueOrDefault("IdentifierValue") is not string value ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            identifiers.Add(new SubjectChannelIdentifier(
                (ChannelIdentifierKind)kindKey,
                value,
                binding.RtId.ToString(),
                binding.GetAttributeValueOrDefault<DateTime>("EnrolledAt"),
                binding.GetAttributeValueOrDefault<DateTime>("LastVerifiedAt")));
        }

        return identifiers;
    }

    /// <summary>
    ///     The user's binding-specific channel preference (AB#5149/AB#5152, System.Identity 2.18.0):
    ///     the rtId of the chosen <c>VerifiedExternalIdentifier</c>, verbatim and unresolved — the
    ///     node matches it against the valid identifiers, so a dangling reference stays visible.
    /// </summary>
    private static string? ReadPreferredChannelBindingId(RtEntity userEntity)
    {
        var bindingId = userEntity.GetAttributeValueOrDefault("PreferredChannelBindingId") as string;
        return string.IsNullOrWhiteSpace(bindingId) ? null : bindingId;
    }

    /// <summary>A binding whose stored <c>ValidUntil</c> not-after has passed is invalid (AB#5123).</summary>
    private static bool IsExpired(RtEntity binding)
        => binding.GetAttributeValueOrDefault<DateTime>("ValidUntil") is { } validUntil &&
           validUntil < DateTime.UtcNow;
}
