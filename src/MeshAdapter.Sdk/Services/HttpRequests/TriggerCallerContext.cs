using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;

/// <summary>
///     Everything <see cref="HttpRequestService" /> knows about the caller of a route, handed to the
///     trigger node's execute delegate. Both members are null for an anonymous invocation.
/// </summary>
/// <remarks>
///     <para>
///         A record rather than a second positional parameter on the delegate: the two values are one
///         concept ("who called, and with what credential"), they are always produced together, and a
///         further caller-scoped value later would otherwise churn every registration site again.
///         Two adjacent parameters — a nullable reference type and a <c>string?</c> — also invite a
///         silent transposition at the call site that the compiler cannot catch.
///     </para>
///     <para>
///         🔴 <b><see cref="RawAccessToken" /> never reaches the pipeline data.</b> It exists only so
///         a node can act as the caller against another service (delegation / on-behalf-of,
///         AB#5031). The data root is echoed into the HTTP response, persistable by
///         <c>SetPipelineExecutionResult@1</c> and shown in the Studio debug panel; the
///         <c>CredentialHeaders</c> filter in <see cref="HttpRequestService" /> exists for exactly
///         that reason and is unaffected by this record. Never log it either.
///     </para>
/// </remarks>
/// <param name="Principal">
///     The token-free, safe projection of the verified caller (AB#4975), or null when the route was
///     invoked anonymously.
/// </param>
/// <param name="RawAccessToken">
///     The raw bearer token the caller presented, or null when the caller presented none (anonymous
///     route) or presented a non-<c>Bearer</c> credential.
/// </param>
internal sealed record TriggerCallerContext(VerifiedPrincipal? Principal, string? RawAccessToken)
{
    /// <summary>An anonymous caller: no verified principal and no credential.</summary>
    public static readonly TriggerCallerContext Anonymous = new(null, null);
}
