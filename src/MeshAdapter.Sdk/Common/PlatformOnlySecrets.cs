using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
/// Secrets that only platform nodes may use; no pipeline may reveal them.
/// </summary>
public static class PlatformOnlySecrets
{
    /// <summary>The secret default entity type.</summary>
    public static readonly RtCkId<CkTypeId> SecretDefaultCkTypeId = new("System.Communication/SecretDefault");

    /// <summary>The Application type that declares secret defaults.</summary>
    public static readonly RtCkId<CkTypeId> ApplicationCkTypeId = new("System.Communication/Application");

    /// <summary>The record array of the Application with the secret defaults.</summary>
    public const string SecretDefaultsAttributeName = "SecretDefaults";

    private static readonly Regex VersionedModelSuffix = new(@"-\d+(\.\d+)*$", RegexOptions.Compiled);

    /// <summary>True when the type is the secret default entity.</summary>
    public static bool IsPlatformOnly(RtCkId<CkTypeId>? ckTypeId)
    {
        return ckTypeId != null && SecretDefaultCkTypeId.Equals(ckTypeId);
    }

    /// <summary>Also true for a type derived from SecretDefault; an uncached type is judged by its id.</summary>
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

    /// <summary>Accepts the type id as text, with or without the model version, in any letter case.</summary>
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
        var typeName = new CkTypeId(text[(separator + 1)..]).Name;
        return string.Equals(model, SecretDefaultCkTypeId.ModelId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(typeName, SecretDefaultCkTypeId.ElementId.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the attribute path starts with the SecretDefaults record array of an Application.</summary>
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
