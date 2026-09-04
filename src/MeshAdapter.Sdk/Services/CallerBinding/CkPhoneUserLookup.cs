using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Tenant-repository-backed <see cref="IPhoneUserLookup" /> (AB#5123). Reads the AB#5122
///     verified-identifier directory (<c>System.Identity/VerifiedExternalIdentifier</c>) with the
///     SAME generic CK entity access as <see cref="CkEntraIdUserLookup" /> (AB#5124) — the identity CK
///     model lives in the tenant's own runtime database, so no service-to-service call to the identity
///     service is needed. The adapter SDK cannot reference the generated identity CK model, so the
///     type ids, attribute names and association roles are addressed by their (non-versioned) full
///     names, exactly as the EntraID lookup and the service-account read do.
/// </summary>
internal sealed class CkPhoneUserLookup(
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    ILogger<CkPhoneUserLookup> logger) : IPhoneUserLookup
{
    private static readonly RtCkId<CkTypeId> VerifiedExternalIdentifierTypeId =
        new("System.Identity/VerifiedExternalIdentifier");

    private static readonly RtCkId<CkAssociationRoleId> IdentifiesUserRoleId =
        new("System.Identity/IdentifiesUser");

    private static readonly RtCkId<CkAssociationRoleId> AssignedRoleRoleId =
        new("System.Identity/AssignedRole");

    // Numeric key of RtIdentifierKindEnum.PhoneNumber in System.Identity (kept identical to
    // ChannelIdentifierKind.PhoneNumber — the AB#5122/5126 enums mirror each other by value).
    private const int PhoneNumberKind = (int)ChannelIdentifierKind.PhoneNumber;

    public async Task<PhoneCallerRecord?> FindByPhoneNumberAsync(string tenantId, string phoneNumber,
        CancellationToken cancellationToken = default)
    {
        var tenantRepository = await systemContext.TryFindTenantRepositoryAsync(tenantId);
        if (tenantRepository == null)
        {
            logger.LogDebug("[{TenantId}] No tenant repository; cannot resolve phone number", tenantId);
            return null;
        }

        try
        {
            await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var binding = await FindBindingAsync(tenantRepository, session, phoneNumber);
            if (binding == null)
            {
                await session.CommitTransactionAsync();
                return null;
            }

            // A self-service certificate would carry a ValidUntil; a phone binding never does, but an
            // expired binding (if the identity side ever stamps one) is treated as unresolved here.
            if (IsExpired(binding))
            {
                await session.CommitTransactionAsync();
                logger.LogDebug("[{TenantId}] Phone binding '{BindingRtId}' is expired; treating as unresolved",
                    tenantId, binding.RtId);
                return null;
            }

            var userEntity = await GetBoundUserAsync(tenantRepository, session, binding);
            if (userEntity == null)
            {
                await session.CommitTransactionAsync();
                logger.LogWarning(
                    "[{TenantId}] VerifiedExternalIdentifier '{BindingRtId}' (phone) has no bound user; treating as unresolved",
                    tenantId, binding.RtId);
                return null;
            }

            var roles = await GetUserRoleNamesAsync(tenantRepository, session, userEntity);
            await session.CommitTransactionAsync();

            var enrollmentTrust = ToCallerTrustLevel(
                binding.GetAttributeValueOrDefault<int>("EnrollmentTrust") ?? 0);

            return new PhoneCallerRecord(
                userEntity.RtId.ToString(),
                tenantRepository.TenantId,
                userEntity.GetAttributeValueOrDefault("Email") as string,
                userEntity.GetAttributeValueOrDefault("UserName") as string,
                roles,
                enrollmentTrust);
        }
        catch (CkCacheException ex)
        {
            // The tenant has no System.Identity model imported: unresolved, not an error — the binder
            // applies the trigger's anonymous-mode decision.
            logger.LogDebug(ex,
                "[{TenantId}] System.Identity model not available; phone number is unresolved", tenantId);
            return null;
        }
    }

    /// <summary>
    ///     Finds the single <c>VerifiedExternalIdentifier</c> for the phone number. Filters on the
    ///     highly selective <c>IdentifierValue</c> and disambiguates the kind in memory — the (kind,
    ///     value) pair is unique per tenant (AB#5122 Unique index), so at most one row survives.
    /// </summary>
    private static async Task<RtEntity?> FindBindingAsync(ITenantRepository tenantRepository,
        IOctoSession session, string phoneNumber)
    {
        var queryOptions = RtEntityQueryOptions.Create();
        queryOptions.AddFieldFilter("IdentifierValue", FieldFilterOperator.Equals, phoneNumber);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            VerifiedExternalIdentifierTypeId, queryOptions);

        return result.Items.FirstOrDefault(e =>
            e.GetAttributeValueOrDefault<int>("IdentifierKind") == PhoneNumberKind);
    }

    private static async Task<RtEntity?> GetBoundUserAsync(ITenantRepository tenantRepository,
        IOctoSession session, RtEntity binding)
    {
        var associations = await tenantRepository.GetRtAssociationsAsync(session,
            binding.ToRtEntityId(),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, roleId: IdentifiesUserRoleId));

        var userAssociation = associations.Items.FirstOrDefault();
        if (userAssociation == null)
        {
            return null;
        }

        return await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(userAssociation.TargetCkTypeId, userAssociation.TargetRtId));
    }

    private static async Task<IReadOnlyList<string>> GetUserRoleNamesAsync(ITenantRepository tenantRepository,
        IOctoSession session, RtEntity userEntity)
    {
        var associations = await tenantRepository.GetRtAssociationsAsync(session,
            userEntity.ToRtEntityId(),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, roleId: AssignedRoleRoleId));

        var roleNames = new List<string>();
        foreach (var association in associations.Items)
        {
            var roleEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(association.TargetCkTypeId, association.TargetRtId));
            if (roleEntity?.GetAttributeValueOrDefault("Name") is string roleName &&
                !string.IsNullOrWhiteSpace(roleName))
            {
                roleNames.Add(roleName);
            }
        }

        return roleNames;
    }

    /// <summary>A binding whose stored <c>ValidUntil</c> not-after has passed is invalid (AB#5123).</summary>
    private static bool IsExpired(RtEntity binding)
        => binding.GetAttributeValueOrDefault<DateTime>("ValidUntil") is { } validUntil &&
           validUntil < DateTime.UtcNow;

    /// <summary>
    ///     Maps the stored <c>RtTrustLevelEnum</c> key onto <see cref="CallerTrustLevel" /> — the two
    ///     enums share their numeric keys one-for-one (None=0, Weak=1, Strong=2). An out-of-range value
    ///     is clamped to <see cref="CallerTrustLevel.None" /> fail-closed.
    /// </summary>
    private static CallerTrustLevel ToCallerTrustLevel(int storedTrust)
        => storedTrust switch
        {
            (int)CallerTrustLevel.Weak => CallerTrustLevel.Weak,
            (int)CallerTrustLevel.Strong => CallerTrustLevel.Strong,
            _ => CallerTrustLevel.None
        };
}
