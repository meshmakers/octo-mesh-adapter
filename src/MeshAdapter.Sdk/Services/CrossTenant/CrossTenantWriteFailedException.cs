using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>
///     Stable error code of a cross-tenant write of ApplyChanges@2 that failed for at least one target tenant (AB#9770):
///     a master-data sync (<c>targetTenants</c>) or a write with <c>targetTenant</c> that the target's engine refused.
/// </summary>
public static class CrossTenantWriteCodes
{
    /// <summary>At least one target tenant was refused or failed; the others were written.</summary>
    public const string WriteFailed = "CROSS_TENANT_WRITE_FAILED";
}

/// <summary>
///     The master-data sync of ApplyChanges@2 (<c>targetTenants</c>) wrote every target it could and failed for the
///     targets named here (AB#9770, decision "fail after all targets"). Thrown after the last target, so the execution
///     is marked failed with every failed target in its error message.
/// </summary>
public sealed class CrossTenantWriteFailedException : PipelineExecutionException
{
    private CrossTenantWriteFailedException(string message, IReadOnlyList<CrossTenantWriteFailure> failures,
        IReadOnlyList<string> writtenTenantIds, Exception? cause) : base(message, cause!)
    {
        Failures = failures;
        WrittenTenantIds = writtenTenantIds;
    }

    /// <summary>The stable error code, <see cref="CrossTenantWriteCodes.WriteFailed" />.</summary>
    public string ErrorCode => CrossTenantWriteCodes.WriteFailed;

    /// <summary>The targets that were refused or failed, in target order.</summary>
    public IReadOnlyList<CrossTenantWriteFailure> Failures { get; }

    /// <summary>The targets that were written (or had nothing to change).</summary>
    public IReadOnlyList<string> WrittenTenantIds { get; }

    internal static CrossTenantWriteFailedException Create(string nodePath, int targetCount,
        IReadOnlyList<CrossTenantWriteFailure> failures, IReadOnlyList<string> writtenTenantIds)
    {
        var failed = string.Join(", ", failures.Select(f => $"'{f.TenantId}' ({f.ErrorCode})"));
        var written = writtenTenantIds.Count == 0
            ? "none"
            : string.Join(", ", writtenTenantIds.Select(t => $"'{t}'"));
        return new CrossTenantWriteFailedException(
            $"[{nodePath}]: {CrossTenantWriteCodes.WriteFailed}: the write failed for {failures.Count} of {targetCount} " +
            $"target tenants: {failed}. Written: {written}.", failures, writtenTenantIds,
            failures.Select(f => f.Cause).FirstOrDefault(c => c != null));
    }
}

/// <summary>
///     One failed target: the tenant and the stable code of its failure. The message of the execution names only these
///     two (never a value); the <see cref="Cause" /> stays on the exception object for the adapter's own log and for tests
///     (the first one is the exception's inner exception).
/// </summary>
public sealed record CrossTenantWriteFailure(string TenantId, string ErrorCode)
{
    /// <summary>The exception that failed the target, if any. Not part of any message.</summary>
    public Exception? Cause { get; init; }
}
