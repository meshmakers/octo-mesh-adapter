using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     Thrown by <see cref="GenericCreateGuard" /> when it has to check an insert but the tenant CK cache is not loaded
///     and could not be loaded (AB#7463). The batch is refused (fail closed): without the cache the guard cannot know
///     whether the type's generic create is replaced by a method. Nothing was written; the run can be repeated.
/// </summary>
/// <remarks>
///     Deliberately not <see cref="GenericCreateReplacedByMethodException" />: the insert was not judged, so the
///     error must not tell the caller that the type is replaced. The <see cref="Code" /> is stable and retryable.
/// </remarks>
public sealed class GenericCreateCheckUnavailableException : PipelineExecutionException
{
    /// <summary>Stable error code.</summary>
    public const string ErrorCode = "GENERIC_CREATE_CHECK_UNAVAILABLE";

    /// <summary>Creates the exception.</summary>
    /// <param name="tenantId">The tenant whose CK cache is not available</param>
    /// <param name="innerException">The failure of the cache load, <c>null</c> when it ended without a loaded cache</param>
    public GenericCreateCheckUnavailableException(string tenantId, Exception? innerException)
        : base($"{ErrorCode}: the Construction Kit cache of tenant '{tenantId}' is not loaded and could not be loaded, "
               + "so the insert cannot be checked against methods that replace the generic create; the batch is "
               + "refused, nothing was written. Retry the run.", innerException!)
    {
    }

    /// <summary>The stable error code, <see cref="ErrorCode" />.</summary>
    public string Code => ErrorCode;
}
