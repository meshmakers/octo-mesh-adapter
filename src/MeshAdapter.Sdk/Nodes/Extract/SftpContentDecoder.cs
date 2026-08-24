using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Decodes downloaded bytes honouring the configured encoding and error handling. Read
/// counterpart of <see cref="Load.SftpContentEncoder" />. Single-byte code pages such as
/// ISO-8859-1 map every byte, so the failure path is only reachable for multi-byte encodings.
/// </summary>
internal static class SftpContentDecoder
{
    internal static string Decode(byte[] content, string encodingName, EncodingErrorHandling onEncodingError,
        INodeContext nodeContext)
    {
        // Strict first, so an undecodable byte is detected rather than silently replaced. The
        // second, lenient pass only runs once that has been decided.
        var strict = (Encoding)SftpUploadEncoding.Resolve(encodingName).Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;

        try
        {
            return StripByteOrderMark(strict.GetString(content));
        }
        catch (DecoderFallbackException)
        {
            if (onEncodingError == EncodingErrorHandling.Fail)
            {
                throw MeshAdapterPipelineExecutionException.CannotDecodeContent(nodeContext, encodingName);
            }

            nodeContext.Warning(
                "Content is not valid '{0}'; undecodable bytes were replaced. Check the encoding option against what the source system writes.",
                encodingName);

            return StripByteOrderMark(SftpUploadEncoding.Resolve(encodingName).GetString(content));
        }
    }

    /// <summary>
    /// Drops a leading byte-order mark. A mark is valid UTF-8, so nothing on the decode path
    /// reports it - kept, it travels on as an invisible first character and turns a downstream
    /// header comparison or split into a mismatch that reads like bad data.
    /// <para />
    /// Applied to the decoded string rather than to the bytes, so it only ever fires for an
    /// encoding that actually produces U+FEFF. Under a single-byte code page the same three
    /// bytes are three ordinary characters, and reading them as a mark would be guessing that
    /// the operator picked the wrong encoding.
    /// </summary>
    private static string StripByteOrderMark(string text)
    {
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
