using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     Why <see cref="GenericCreateGuard" /> refused an insert. The tokens are language-neutral and stable
///     (AB#7462), safe to match on in a client.
/// </summary>
public static class GenericCreateRefusalCause
{
    /// <summary>The run carries no method-invocation marker (manual run, cron, event, chained pipeline).</summary>
    public const string MissingMarker = "MISSING_METHOD_INVOCATION";

    /// <summary>The marker names another method than the one that replaces the generic create of the type.</summary>
    public const string MethodMismatch = "METHOD_MISMATCH";

    /// <summary>The marker belongs to another pipeline than the one that is running.</summary>
    public const string PipelineMismatch = "PIPELINE_MISMATCH";
}

/// <summary>
///     One refused insert: the type, the method that replaces its generic create and the cause.
/// </summary>
/// <param name="CkTypeId">Version-less CK type id of the entity that was to be created</param>
/// <param name="MethodId">Qualified id of the method that replaces the generic create of the type</param>
/// <param name="Cause">One of the <see cref="GenericCreateRefusalCause" /> tokens</param>
public sealed record GenericCreateRefusal(string CkTypeId, string MethodId, string Cause);

/// <summary>
///     Thrown by <see cref="GenericCreateGuard" /> before any write when an insert would create an entity of a type
///     whose CK method replaces the generic create (<c>replacesGenericCreate</c>) outside that method's own pipeline
///     run. The whole batch is refused (AB#7462).
/// </summary>
/// <remarks>
///     The structured members (<see cref="Code" />, <see cref="Refusals" />) are the contract; the message is for
///     humans and logs only and may change.
/// </remarks>
public sealed class GenericCreateReplacedByMethodException : PipelineExecutionException
{
    private const int MaxListedRefusals = 20;

    /// <summary>
    ///     Creates the exception for a refused batch.
    /// </summary>
    /// <param name="refusals">The refused inserts, at least one</param>
    public GenericCreateReplacedByMethodException(IReadOnlyList<GenericCreateRefusal> refusals)
        : base(BuildMessage(refusals))
    {
        Refusals = refusals;
    }

    /// <summary>
    ///     The stable error code, <see cref="GenericCreateReplacement.ReasonToken" />.
    /// </summary>
    public string Code => GenericCreateReplacement.ReasonToken;

    /// <summary>
    ///     Every refused insert of the batch (type, method, cause); the first one is the primary offender.
    /// </summary>
    public IReadOnlyList<GenericCreateRefusal> Refusals { get; }

    private static string BuildMessage(IReadOnlyList<GenericCreateRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        if (refusals.Count == 0)
        {
            throw new ArgumentException("At least one refusal is required.", nameof(refusals));
        }

        var sb = new StringBuilder();
        sb.Append(GenericCreateReplacement.ReasonToken)
            .Append(": the batch is refused, ").Append(refusals.Count)
            .Append(" insert(s) create entities of types whose generic create is replaced by a method: ");
        sb.AppendJoin("; ", refusals.Take(MaxListedRefusals)
            .Select(r => $"type '{r.CkTypeId}', method '{r.MethodId}', reason {r.Cause}"));
        if (refusals.Count > MaxListedRefusals)
        {
            sb.Append("; ... and ").Append(refusals.Count - MaxListedRefusals).Append(" more");
        }

        return sb.ToString();
    }
}
