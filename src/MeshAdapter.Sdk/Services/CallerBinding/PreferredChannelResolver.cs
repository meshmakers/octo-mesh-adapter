using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

/// <summary>
///     Resolves a user's preferred outbound channel from the BINDING-SPECIFIC preference
///     (AB#5149/AB#5152, System.Identity 2.18.0): <c>User.PreferredChannelBindingId</c> carries the
///     rtId of the chosen <c>VerifiedExternalIdentifier</c>, whose kind yields the channel
///     (PhoneNumber → "SIGNAL", EntraIdObjectId → "TEAMS", EmailAddress → "EMAIL") and whose
///     identifier value is the EXACT delivery target. Shared by the three forward caller-binding
///     lookups (which only need the derived channel string for
///     <c>VerifiedPrincipal.PreferredChannel</c> — that contract stays a channel string) and
///     documented against <see cref="CkSubjectChannelLookup" />, which surfaces the raw values so
///     ResolveNotificationChannel@1 can select the concrete target itself.
///     <para>
///     Rollout-skew tolerance: a user written before System.Identity 2.18.0 may still carry the
///     LEGACY kind-level <c>PreferredChannel</c> string ("TEAMS" | "SIGNAL") and no binding id;
///     that value is used transitionally when no (valid) binding reference exists.
///     </para>
/// </summary>
internal static class PreferredChannelResolver
{
    private static readonly RtCkId<CkTypeId> VerifiedExternalIdentifierTypeId =
        new("System.Identity/VerifiedExternalIdentifier");

    /// <summary>
    ///     Derives the user's preferred channel STRING: the referenced binding's channel when the
    ///     reference resolves to a valid (non-expired) binding of a channel-bearing kind, else the
    ///     legacy kind-level string, else null. A dangling/invalid/unreadable reference is warned
    ///     and treated as "no binding-specific preference" — never guessed at.
    /// </summary>
    public static async Task<string?> ResolveChannelAsync(ITenantRepository tenantRepository,
        IOctoSession session, RtEntity userEntity, ILogger logger, string tenantId)
    {
        var preferred = await ResolvePreferredBindingAsync(tenantRepository, session, userEntity, logger, tenantId);
        return preferred?.Channel ?? ReadLegacyPreferredChannel(userEntity);
    }

    /// <summary>
    ///     Resolves the referenced preferred binding itself, or null when the user carries no
    ///     <c>PreferredChannelBindingId</c> or the reference is dangling/expired/channel-less
    ///     (each warned — deleted mid-flight is an expected race, not an error).
    /// </summary>
    public static async Task<PreferredChannelBinding?> ResolvePreferredBindingAsync(
        ITenantRepository tenantRepository, IOctoSession session, RtEntity userEntity,
        ILogger logger, string tenantId)
    {
        if (userEntity.GetAttributeValueOrDefault("PreferredChannelBindingId") is not string bindingId ||
            string.IsNullOrWhiteSpace(bindingId))
        {
            return null;
        }

        OctoObjectId bindingRtId;
        try
        {
            bindingRtId = new OctoObjectId(bindingId);
        }
        catch (Exception)
        {
            logger.LogWarning(
                "[{TenantId}] User '{UserRtId}' has a malformed PreferredChannelBindingId '{BindingId}'; ignoring the preference",
                tenantId, userEntity.RtId, bindingId);
            return null;
        }

        var binding = await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(VerifiedExternalIdentifierTypeId, bindingRtId));
        if (binding == null)
        {
            logger.LogWarning(
                "[{TenantId}] User '{UserRtId}' references preferred binding '{BindingId}' which no longer exists; ignoring the preference",
                tenantId, userEntity.RtId, bindingId);
            return null;
        }

        if (IsExpired(binding))
        {
            logger.LogWarning(
                "[{TenantId}] User '{UserRtId}' references preferred binding '{BindingId}' which is expired; ignoring the preference",
                tenantId, userEntity.RtId, bindingId);
            return null;
        }

        var kindKey = binding.GetAttributeValueOrDefault<int>("IdentifierKind") ?? -1;
        var channel = Enum.IsDefined(typeof(ChannelIdentifierKind), kindKey)
            ? ChannelForKind((ChannelIdentifierKind)kindKey)
            : null;
        if (channel == null)
        {
            logger.LogWarning(
                "[{TenantId}] User '{UserRtId}' references preferred binding '{BindingId}' of a kind ({KindKey}) no outbound channel maps to; ignoring the preference",
                tenantId, userEntity.RtId, bindingId, kindKey);
            return null;
        }

        if (binding.GetAttributeValueOrDefault("IdentifierValue") is not string value ||
            string.IsNullOrWhiteSpace(value))
        {
            logger.LogWarning(
                "[{TenantId}] User '{UserRtId}' references preferred binding '{BindingId}' with no identifier value; ignoring the preference",
                tenantId, userEntity.RtId, bindingId);
            return null;
        }

        return new PreferredChannelBinding(bindingId, channel, value);
    }

    /// <summary>
    ///     Maps a binding kind onto the channel a system-initiated message would use — the AB#5149
    ///     channel-name contract ("SIGNAL" | "TEAMS" | "EMAIL"). A certificate fingerprint carries
    ///     no outbound channel: null.
    /// </summary>
    public static string? ChannelForKind(ChannelIdentifierKind kind) => kind switch
    {
        ChannelIdentifierKind.PhoneNumber => "SIGNAL",
        ChannelIdentifierKind.EntraIdObjectId => "TEAMS",
        ChannelIdentifierKind.EmailAddress => "EMAIL",
        _ => null
    };

    /// <summary>
    ///     TRANSITIONAL (pre-System.Identity-2.18.0): the legacy kind-level preference string
    ///     ("TEAMS" | "SIGNAL"), stored verbatim on the user. Read only when no valid
    ///     binding-specific reference exists, until the 2.18.0 rollout has converged.
    /// </summary>
    public static string? ReadLegacyPreferredChannel(RtEntity userEntity)
    {
        var preferredChannel = userEntity.GetAttributeValueOrDefault("PreferredChannel") as string;
        return string.IsNullOrWhiteSpace(preferredChannel) ? null : preferredChannel;
    }

    /// <summary>A binding whose stored <c>ValidUntil</c> not-after has passed is invalid (AB#5123).</summary>
    public static bool IsExpired(RtEntity binding)
        => binding.GetAttributeValueOrDefault<DateTime>("ValidUntil") is { } validUntil &&
           validUntil < DateTime.UtcNow;
}

/// <summary>
///     The resolved binding-specific channel preference of a user (AB#5152): which binding, which
///     channel its kind maps to, and the exact delivery target value.
/// </summary>
/// <param name="BindingRtId">The referenced <c>VerifiedExternalIdentifier</c>'s rtId.</param>
/// <param name="Channel">The channel derived from the binding kind ("SIGNAL" | "TEAMS" | "EMAIL").</param>
/// <param name="Value">The binding's identifier value — the exact delivery target.</param>
internal sealed record PreferredChannelBinding(
    string BindingRtId,
    string Channel,
    string Value);
