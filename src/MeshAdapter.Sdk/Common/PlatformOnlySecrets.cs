using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// The platform-only rule of AB#7246 (Feature AB#7219): a secret default declared on an Application and
/// projected by the Communication Controller into a served tenant may be used only inside platform
/// building blocks that return a result and never the value. No tenant pipeline, user or role - tenant
/// administrators included - may obtain it.
/// </summary>
/// <remarks>
/// <para>
/// Every node that decrypts a Secret consults this class before it reads anything. The set is decided by
/// CK type and not by attribute ownership, because every Secret attribute has ownership Secret and a
/// tenant secret and a platform secret are indistinguishable by it.
/// </para>
/// <para>
/// Two places carry such a value: the entity <c>System.Communication/SecretDefault</c> (attribute
/// <c>Value</c>, in every served tenant) and the record array <c>SecretDefaults</c> of
/// <c>System.Communication/Application</c> (member <c>SecretValue</c>, in the tenant that owns the
/// Application). New decrypting call sites are caught by <c>SecretDecryptionCallSiteArchitectureTests</c>.
/// </para>
/// </remarks>
public static class PlatformOnlySecrets
{
    /// <summary>The platform-owned entity type that holds a projected secret default.</summary>
    public static readonly RtCkId<CkTypeId> SecretDefaultCkTypeId = new("System.Communication/SecretDefault");

    /// <summary>The type whose <see cref="SecretDefaultsAttributeName" /> record array holds the declared values.</summary>
    public static readonly RtCkId<CkTypeId> ApplicationCkTypeId = new("System.Communication/Application");

    /// <summary>The record array attribute of the Application that carries the declared secret values.</summary>
    public const string SecretDefaultsAttributeName = "SecretDefaults";

    private static readonly Regex VersionedModelSuffix = new(@"-\d+(\.\d+)*$", RegexOptions.Compiled);

    /// <summary>True when the type is the secret default entity. Unknown (null) answers false.</summary>
    public static bool IsPlatformOnly(RtCkId<CkTypeId>? ckTypeId)
    {
        return ckTypeId != null && SecretDefaultCkTypeId.Equals(ckTypeId);
    }

    /// <summary>
    /// True when the type is the secret default entity or derives from it. The type is final in the CK model, so
    /// the base chain only matters if that ever changes; a type that is not in the cache is judged by its id alone.
    /// </summary>
    public static bool IsPlatformOnly(ICkCacheService ckCacheService, string tenantId, RtCkId<CkTypeId>? ckTypeId)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        if (ckTypeId == null)
        {
            return false;
        }

        if (IsPlatformOnly(ckTypeId))
        {
            return true;
        }

        return ckCacheService.TryGetRtCkType(tenantId, ckTypeId, out var graph) &&
               graph.GetBaseTypes(includeSelf: true).Any(baseType => SecretDefaultCkTypeId.Equals(baseType));
    }

    /// <summary>
    /// <see cref="IsPlatformOnly(RtCkId{CkTypeId}?)" /> for a type id given as text (<c>Model/Type</c>, with or
    /// without the model version). Text that is not a type id answers false.
    /// </summary>
    public static bool IsPlatformOnlyCkTypeId(string? ckTypeId)
    {
        if (string.IsNullOrWhiteSpace(ckTypeId))
        {
            return false;
        }

        var text = ckTypeId.Trim();
        var separator = text.IndexOf('/');
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        var model = VersionedModelSuffix.Replace(text[..separator], string.Empty);
        return IsPlatformOnly(new RtCkId<CkTypeId>(model, new CkTypeId(text[(separator + 1)..])));
    }

    /// <summary>
    /// True when the first segment of <paramref name="attributeName" /> (a dotted path, an index such as
    /// <c>SecretDefaults[0]</c> is ignored) is the <c>SecretDefaults</c> record array of the Application.
    /// </summary>
    public static bool IsPlatformOnlyAttribute(RtCkId<CkTypeId>? ckTypeId, string? attributeName)
    {
        if (ckTypeId == null || string.IsNullOrWhiteSpace(attributeName) || !ApplicationCkTypeId.Equals(ckTypeId))
        {
            return false;
        }

        var firstSegment = attributeName.Split('.')[0];
        var bracket = firstSegment.IndexOf('[');
        if (bracket >= 0)
        {
            firstSegment = firstSegment[..bracket];
        }

        return string.Equals(firstSegment.Trim(), SecretDefaultsAttributeName, StringComparison.OrdinalIgnoreCase);
    }
}
