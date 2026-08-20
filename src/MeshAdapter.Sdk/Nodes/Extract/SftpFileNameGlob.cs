using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// File name matching for remote listings: '*' matches any run of characters, '?' exactly one,
/// the pattern is anchored at both ends and matching is case insensitive. Every other character
/// is literal, so a dot in the pattern is a dot and not a regular expression wildcard.
/// </summary>
internal static class SftpFileNameGlob
{
    internal static bool Matches(string fileName, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }
}
