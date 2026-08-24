using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// File name matching for remote listings: '*' matches any run of characters, '?' exactly one,
/// the pattern is anchored at both ends and matching is case insensitive. Every other character
/// is literal, so a dot in the pattern is a dot and not a regular expression wildcard.
/// </summary>
internal static class SftpFileNameGlob
{
    // NonBacktracking: every '*' becomes an independent '.*', and a backtracking engine needs
    // O(n^k) steps for k wildcards on a near miss - sixty characters against nine wildcards
    // runs for minutes. The file name comes from the remote server, so the worst case is not
    // the operator's to pick. The non-backtracking engine is linear by construction.
    // CultureInvariant: case folding would otherwise follow the pod's culture, and under tr-TR
    // 'I' does not fold to 'i', so a pattern like AI* would quietly stop matching ai_*.
    // \A and \z instead of ^ and $: '$' also matches before a trailing newline, and a POSIX
    // file name may contain one - such a file would pass as though the newline were not there.
    private const RegexOptions MatchOptions =
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Translates a pattern into the matcher a listing is filtered with. Built once per
    /// listing rather than per entry: the pattern is the same for every entry, and a directory
    /// with thousands of files would otherwise re-escape and re-parse it thousands of times.
    /// </summary>
    internal static Regex Compile(string pattern)
    {
        return new Regex(@"\A" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + @"\z",
            MatchOptions);
    }

    /// <summary>
    /// Matches a single name against a pattern. Builds the matcher for this one call, so
    /// anything filtering more than one name takes <see cref="Compile" /> and keeps the result.
    /// </summary>
    internal static bool Matches(string fileName, string pattern)
    {
        return Compile(pattern).IsMatch(fileName);
    }
}
