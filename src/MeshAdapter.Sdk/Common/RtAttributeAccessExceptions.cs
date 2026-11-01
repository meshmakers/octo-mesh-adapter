using System.Text;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     Stable, language-neutral error codes of the attribute-access guard of the mesh adapter (AB#7768, CK v2 attribute
///     access <c>Hidden</c> / <c>MethodOnly</c> / <c>ReadOnly</c>). Safe to match on in a client.
/// </summary>
public static class RtAttributeAccessCodes
{
    /// <summary>A node was configured to filter, sort or select on a <c>Hidden</c> attribute.</summary>
    public const string HiddenAttribute = "RT_ATTRIBUTE_HIDDEN";

    /// <summary>A write was refused because of the access of an attribute (Hidden, MethodOnly, ReadOnly).</summary>
    public const string WriteRestricted = "RT_ATTRIBUTE_WRITE_RESTRICTED";

    /// <summary>The CK cache was not available, so the access of the attributes could not be judged (fail closed).</summary>
    public const string CheckUnavailable = "RT_ATTRIBUTE_ACCESS_CHECK_UNAVAILABLE";
}

/// <summary>Why <see cref="RtAttributeAccessGuard" /> refused a write. The tokens are stable.</summary>
public static class RtAttributeWriteRefusalCause
{
    /// <summary>The attribute is <c>Hidden</c>: only a CK method may write it, and a method run is not enough for a pipeline node to see it.</summary>
    public const string Hidden = "HIDDEN";

    /// <summary>The attribute is <c>MethodOnly</c> and the run carries no method-invocation marker.</summary>
    public const string MethodOnlyMissingMarker = "METHOD_ONLY_MISSING_METHOD_INVOCATION";

    /// <summary>The attribute is <c>MethodOnly</c> and the marker names a method that does not belong to the entity's type.</summary>
    public const string MethodOnlyMethodMismatch = "METHOD_ONLY_METHOD_MISMATCH";

    /// <summary>The attribute is <c>MethodOnly</c> and the marker belongs to another pipeline than the one that is running.</summary>
    public const string MethodOnlyPipelineMismatch = "METHOD_ONLY_PIPELINE_MISMATCH";

    /// <summary>The attribute is <c>ReadOnly</c>: a pipeline never writes it.</summary>
    public const string ReadOnly = "READ_ONLY";

    /// <summary>
    ///     A <c>Replace</c> of an entity whose type has an attribute that this run may not write: the replacement
    ///     document would clear the stored value.
    /// </summary>
    public const string ReplaceWouldClear = "REPLACE_WOULD_CLEAR";

    /// <summary>
    ///     AB#8059: an identity-owned attribute of <c>System.Identity/User</c> or an edge of an identity association role
    ///     (<c>IdentityOwnedWrites</c> of the engine). Only the identity service writes them; a pipeline only in the method
    ///     run of a System.Identity type.
    /// </summary>
    public const string IdentityOwned = "IDENTITY_OWNED";

    /// <summary>
    ///     The <see cref="RtAttributeWriteRefusal.Access" /> value of a refused association change (an association has no
    ///     attribute access).
    /// </summary>
    public const string AssociationAccess = "Association";
}

/// <summary>One refused attribute write: type, attribute path, its access and the cause.</summary>
/// <param name="CkTypeId">Version-less CK type id of the entity</param>
/// <param name="AttributePath">
///     The attribute, <c>Record.Member</c> for a record member, or <c>association &lt;roleId&gt; -&gt; &lt;target type&gt;</c>
///     for an association change (AB#8059)
/// </param>
/// <param name="Access">The declared access of the attribute, <c>Association</c> for an association change</param>
/// <param name="Cause">One of the <see cref="RtAttributeWriteRefusalCause" /> tokens</param>
public sealed record RtAttributeWriteRefusal(string CkTypeId, string AttributePath, string Access, string Cause);

/// <summary>
///     Thrown when a node is configured to filter, sort or select by a <c>Hidden</c> attribute (AB#7768). A
///     filter on a hidden attribute is an oracle for its value (<c>passwordHash startsWith "a…"</c>), so it is
///     refused like a read. The message names the node and the path, never a probed value.
/// </summary>
public sealed class RtAttributeHiddenException : PipelineExecutionException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="node">Path of the node in the pipeline</param>
    /// <param name="attributePath">The configured path that reaches a hidden attribute</param>
    /// <param name="hiddenAttribute">The hidden attribute the path reaches</param>
    /// <param name="usage">What the path was used for (field filter, sort order, query column)</param>
    public RtAttributeHiddenException(string node, string attributePath, string hiddenAttribute, string usage)
        : base($"{RtAttributeAccessCodes.HiddenAttribute}: [{node}]: the {usage} '{attributePath}' reaches the " +
               $"hidden attribute '{hiddenAttribute}'. Hidden attributes cannot be read, filtered or sorted by in a " +
               "pipeline.")
    {
        Node = node;
        AttributePath = attributePath;
        HiddenAttribute = hiddenAttribute;
        Usage = usage;
    }

    /// <summary>The stable error code.</summary>
    public string Code => RtAttributeAccessCodes.HiddenAttribute;

    /// <summary>Path of the node in the pipeline.</summary>
    public string Node { get; }

    /// <summary>The configured path.</summary>
    public string AttributePath { get; }

    /// <summary>The hidden attribute the path reaches.</summary>
    public string HiddenAttribute { get; }

    /// <summary>What the path was used for.</summary>
    public string Usage { get; }
}

/// <summary>
///     Thrown by <see cref="RtAttributeAccessGuard" /> before any write when the batch contains a value for an attribute
///     that a pipeline may not write (AB#7768) or an identity-owned attribute or association change (AB#8059). The whole
///     batch is refused. The message lists types, attribute paths or association roles and causes, never values or rtIds.
/// </summary>
public sealed class RtAttributeWriteRestrictedException : PipelineExecutionException
{
    private const int MaxListedRefusals = 20;

    /// <summary>Creates the exception for a refused batch.</summary>
    /// <param name="refusals">The refused writes, at least one</param>
    public RtAttributeWriteRestrictedException(IReadOnlyList<RtAttributeWriteRefusal> refusals)
        : base(BuildMessage(refusals))
    {
        Refusals = refusals;
    }

    /// <summary>The stable error code.</summary>
    public string Code => RtAttributeAccessCodes.WriteRestricted;

    /// <summary>Every refused write of the batch; the first one is the primary offender.</summary>
    public IReadOnlyList<RtAttributeWriteRefusal> Refusals { get; }

    private static string BuildMessage(IReadOnlyList<RtAttributeWriteRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        if (refusals.Count == 0)
        {
            throw new ArgumentException("At least one refusal is required.", nameof(refusals));
        }

        var sb = new StringBuilder();
        sb.Append(RtAttributeAccessCodes.WriteRestricted).Append(": the batch is refused, ").Append(refusals.Count)
            .Append(" write(s) touch attributes or associations that a pipeline may not write: ");
        sb.AppendJoin("; ", refusals.Take(MaxListedRefusals)
            .Select(r => $"type '{r.CkTypeId}', attribute '{r.AttributePath}' ({r.Access}), reason {r.Cause}"));
        if (refusals.Count > MaxListedRefusals)
        {
            sb.Append("; ... and ").Append(refusals.Count - MaxListedRefusals).Append(" more");
        }

        return sb.ToString();
    }
}

/// <summary>
///     Thrown when the attribute-access guard has to judge entities but the tenant CK cache is not loaded and could
///     not be loaded (AB#7768). Fail closed: without the cache the guard cannot know which attributes are hidden, so
///     nothing is emitted or written. The run can be repeated.
/// </summary>
public sealed class RtAttributeAccessCheckUnavailableException : PipelineExecutionException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="tenantId">The tenant whose CK cache is not available</param>
    /// <param name="innerException">The failure of the cache load, <c>null</c> when it ended without a loaded cache</param>
    public RtAttributeAccessCheckUnavailableException(string tenantId, Exception? innerException)
        : base($"{RtAttributeAccessCodes.CheckUnavailable}: the Construction Kit cache of tenant '{tenantId}' is not " +
               "loaded and could not be loaded, so the access of the attributes cannot be checked; nothing was " +
               "emitted or written. Retry the run.", innerException!)
    {
    }

    /// <summary>The stable error code.</summary>
    public string Code => RtAttributeAccessCodes.CheckUnavailable;
}
