using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Encodes string content for SFTP upload honouring the configured encoding and
/// encoding-error handling. Detection uses a strict (exception-fallback) probe so
/// replacement is single-pass and deterministic: exactly one '?' per Unicode scalar.
/// A surrogate pair collapses to one '?' — the built-in EncoderReplacementFallback
/// would emit one per UTF-16 unit, i.e. two.
/// </summary>
internal static class SftpContentEncoder
{
    private const int MaxReportedCodePoints = 20;

    internal static byte[] Encode(string content, string encodingName, EncodingErrorHandling onEncodingError,
        INodeContext nodeContext)
    {
        var strict = (Encoding)SftpUploadEncoding.Resolve(encodingName).Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;

        try
        {
            return strict.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            // At least one character is not representable; fall through to the scalar walk.
        }

        var (sanitized, distinctCodePoints, count) = Sanitize(content, strict);

        var reported = string.Join(", ", distinctCodePoints.Take(MaxReportedCodePoints));
        if (distinctCodePoints.Count > MaxReportedCodePoints)
        {
            reported += ", …";
        }

        if (onEncodingError == EncodingErrorHandling.Fail)
        {
            throw MeshAdapterPipelineExecutionException.UnencodableContent(nodeContext, encodingName, count, reported);
        }

        nodeContext.Warning(
            "SftpUpload: {0} character(s) not representable in encoding '{1}' were replaced with '?'. Offending code points: {2}",
            count, encodingName, reported);

        return strict.GetBytes(sanitized);
    }

    private static (string Sanitized, List<string> DistinctCodePoints, int Count) Sanitize(string content,
        Encoding strict)
    {
        var builder = new StringBuilder(content.Length);
        var distinctCodePoints = new List<string>();
        var seen = new HashSet<int>();
        var count = 0;

        for (var i = 0; i < content.Length;)
        {
            if (Rune.TryGetRuneAt(content, i, out var rune))
            {
                var length = rune.Utf16SequenceLength;
                if (CanEncode(strict, content.AsSpan(i, length)))
                {
                    builder.Append(content, i, length);
                }
                else
                {
                    builder.Append('?');
                    count++;
                    if (seen.Add(rune.Value))
                    {
                        distinctCodePoints.Add($"U+{rune.Value:X4}");
                    }
                }

                i += length;
            }
            else
            {
                // Lone surrogate: not a valid scalar in any encoding, one '?' per UTF-16 unit.
                builder.Append('?');
                count++;
                int value = content[i];
                if (seen.Add(value))
                {
                    distinctCodePoints.Add($"U+{value:X4}");
                }

                i += 1;
            }
        }

        return (builder.ToString(), distinctCodePoints, count);
    }

    private static bool CanEncode(Encoding strict, ReadOnlySpan<char> chars)
    {
        try
        {
            _ = strict.GetByteCount(chars);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
