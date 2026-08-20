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
            return strict.GetString(content);
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

            return SftpUploadEncoding.Resolve(encodingName).GetString(content);
        }
    }
}
