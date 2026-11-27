using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     A write the engine's released-document lock refused, as the error of a pipeline node. The engine alone decides
///     (it refuses every caller, system sessions included); the node only maps the refusal to its stable code, the
///     engine's <see cref="ErrorCode" />: <c>RELEASED_DOCUMENT_LOCKED</c> (a released document would change or be
///     deleted, message 168), <c>STORNO_REFUSED</c> (169 to 172) or <c>DOCUMENT_AUDIT_ENTRY_APPEND_ONLY</c> (173).
///     The whole batch is refused and nothing of it is written. Safe to match on the code in a client.
/// </summary>
public sealed class ReleasedDocumentWriteRefusedException : PipelineExecutionException
{
    private ReleasedDocumentWriteRefusedException(string errorCode, string message, Exception inner)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The engine's stable code of the refusal.</summary>
    public string ErrorCode { get; }

    /// <summary>
    ///     The node error of an engine refusal, or <c>null</c> when <paramref name="exception" /> is none: the message is
    ///     <c>[nodePath]: </c> followed by the engine's message, which starts with the code and names every refused
    ///     document with its message number.
    /// </summary>
    internal static ReleasedDocumentWriteRefusedException? FromEngine(string nodePath, Exception exception)
    {
        var errorCode = exception switch
        {
            ReleasedDocumentLockedException => ReleasedDocumentLockedException.ErrorCode,
            StornoRefusedException => StornoRefusedException.ErrorCode,
            DocumentAuditEntryAppendOnlyException => DocumentAuditEntryAppendOnlyException.ErrorCode,
            _ => null
        };
        return errorCode == null
            ? null
            : new ReleasedDocumentWriteRefusedException(errorCode, $"[{nodePath}]: {exception.Message}", exception);
    }
}

/// <summary>
///     The released-document lock lives in the engine (design note D3, D8): the write nodes only map its refusal to
///     <see cref="ReleasedDocumentWriteRefusedException" />, never check a document themselves.
/// </summary>
internal static class ReleasedDocumentWriteRefusal
{
    /// <summary>Runs <paramref name="write" />; an engine refusal becomes <see cref="ReleasedDocumentWriteRefusedException" />.</summary>
    public static async Task RunAsync(string nodePath, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (PersistenceException e) when (ReleasedDocumentWriteRefusedException.FromEngine(nodePath, e) is
                                                 { } refused)
        {
            throw refused;
        }
    }
}
