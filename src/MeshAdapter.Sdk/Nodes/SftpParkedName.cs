using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// The temporary name <c>SftpDelete@1</c> moves a file to before it deletes it (AB#8284): same
/// directory, a leading dot, the original name, <c>.sftpdelete-</c> and a fresh GUID as 32
/// lower-case hex digits, for example <c>/out/.AR00001.TXT.sftpdelete-3f2b...</c>. The one
/// place that builds the name and the one place that recognizes it, so <c>SftpList@1</c> skips
/// exactly what <c>SftpDelete@1</c> creates.
/// </summary>
internal static partial class SftpParkedName
{
    /// <summary>The text between the original name and the GUID.</summary>
    internal const string Marker = ".sftpdelete-";

    /// <summary>The <c>SftpList@1</c> file pattern that lists every file under a temporary name.</summary>
    internal const string ListingPattern = ".*.sftpdelete-*";

    /// <summary>
    /// The temporary name for a file. Unique per call, so no other writer targets it and the file
    /// under it is the one that was under the original name at the moment of the move.
    /// </summary>
    internal static string PathFor(string remotePath)
    {
        var slash = remotePath.LastIndexOf('/');
        return $"{remotePath[..(slash + 1)]}.{remotePath[(slash + 1)..]}{Marker}{Guid.NewGuid():N}";
    }

    /// <summary>
    /// True when a file name has exactly the form <see cref="PathFor" /> gives it: the whole name,
    /// at least one character of the original name, the marker in lower case and exactly 32
    /// lower-case hex digits at the end. Strict on purpose: a delivery that merely resembles the
    /// form is not hidden from a listing.
    /// </summary>
    internal static bool IsParkedName(string fileName)
    {
        return Shape().IsMatch(fileName);
    }

    // Singleline: a POSIX file name may contain a line break, and the temporary name of such a
    // file carries it too. \A and \z instead of ^ and $, so a line break at the end does not pass.
    // Case-sensitive: Guid's format N writes lower-case hex digits, and the marker is written as is.
    [GeneratedRegex(@"\A\..+\.sftpdelete-[0-9a-f]{32}\z", RegexOptions.Singleline)]
    private static partial Regex Shape();
}
