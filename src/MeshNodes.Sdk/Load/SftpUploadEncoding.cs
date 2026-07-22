using System.Text;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Resolves and validates encoding names for the SftpUpload node. Registers the
/// <see cref="CodePagesEncodingProvider"/> so legacy code pages such as windows-1252
/// are available on modern .NET runtimes.
/// </summary>
public static class SftpUploadEncoding
{
    static SftpUploadEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Resolves an encoding name (e.g. utf-8, windows-1252, iso-8859-1) to an <see cref="Encoding"/>.
    /// </summary>
    /// <param name="encodingName">The IANA or code-page name to resolve</param>
    /// <returns>The resolved encoding</returns>
    /// <exception cref="ArgumentException">The name is empty or not a known encoding</exception>
    public static Encoding Resolve(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            throw new ArgumentException(
                "Encoding must not be empty; omit the property to use the utf-8 default.",
                nameof(encodingName));
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(encodingName);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException(
                $"Unknown encoding '{encodingName}'. Use an IANA or code-page name such as utf-8, windows-1252 or iso-8859-1.",
                nameof(encodingName), e);
        }

        if (encoding is UnicodeEncoding or UTF32Encoding)
        {
            throw new ArgumentException(
                $"Encoding '{encodingName}' is not supported: SftpUpload writes no byte-order mark, so multi-byte encodings with byte-order semantics would produce ambiguous files. Use utf-8 or a single-byte code page such as windows-1252.",
                nameof(encodingName));
        }

        return encoding;
    }
}
