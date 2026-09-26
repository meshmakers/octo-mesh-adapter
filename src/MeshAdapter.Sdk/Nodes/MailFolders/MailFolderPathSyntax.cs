namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     The path syntax of the Microsoft 365 (Graph) mail channel, in ONE place (AB#5370).
///     <para>
///     A Graph folder path is the folder's display names from the mailbox root joined with
///     <c>/</c>. A <c>/</c> INSIDE a display name is escaped as <c>\/</c> — a real case: one
///     operator's folder is called <c>02_Steuern / Finanzen</c>. <see cref="JoinGraphPath(string?, string)" />
///     produces that form and <see cref="SplitGraphPath" /> is the matching inverse; AB#5385 part 3
///     adopts <see cref="SplitGraphPath" /> in <c>FromMicrosoftGraphEmail@1</c>, which until then
///     splits on every <c>/</c>. The two MUST stay inverses of each other: the picker stores what
///     <see cref="JoinGraphPath(string?, string)" /> returns, and the trigger resolves it with
///     <see cref="SplitGraphPath" />.
///     </para>
///     <para>
///     Only <c>/</c> is escaped. A backslash that is not followed by <c>/</c> is an ordinary
///     character, so every path written before this rule existed still reads exactly as it did.
///     </para>
///     <para>
///     The IMAP channel has NO client-side syntax: its paths are the folder names as the server
///     reports them, with the server's own hierarchy delimiter, and are handed back verbatim.
///     </para>
/// </summary>
internal static class MailFolderPathSyntax
{
    /// <summary>The separator between the segments of a Graph folder path.</summary>
    public const char GraphSeparator = '/';

    /// <summary>The character that turns the following <see cref="GraphSeparator" /> into a literal.</summary>
    public const char GraphEscape = '\\';

    /// <summary>Escapes one display name so it can sit inside a Graph path: <c>/</c> becomes <c>\/</c>.</summary>
    public static string EscapeGraphSegment(string displayName)
    {
        return displayName.Replace(GraphSeparator.ToString(), $"{GraphEscape}{GraphSeparator}");
    }

    /// <summary>
    ///     Appends a display name to its parent's path. <paramref name="parentPath" /> is null or
    ///     empty for a root folder, whose path is its own (escaped) name.
    /// </summary>
    public static string JoinGraphPath(string? parentPath, string displayName)
    {
        var segment = EscapeGraphSegment(displayName);
        return string.IsNullOrEmpty(parentPath) ? segment : $"{parentPath}{GraphSeparator}{segment}";
    }

    /// <summary>Builds a Graph path from its display names, root first.</summary>
    public static string JoinGraphPath(IEnumerable<string> displayNames)
    {
        string? path = null;
        foreach (var name in displayNames)
        {
            path = JoinGraphPath(path, name);
        }

        return path ?? string.Empty;
    }

    /// <summary>
    ///     The inverse of <see cref="JoinGraphPath(string?, string)" />: splits a stored path into
    ///     display names. <c>/</c> separates, <c>\/</c> is a literal slash inside a name. Segments are
    ///     trimmed and empty ones dropped, as the trigger has always done with its input.
    /// </summary>
    public static IReadOnlyList<string> SplitGraphPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == GraphEscape && i + 1 < path.Length && path[i + 1] == GraphSeparator)
            {
                current.Append(GraphSeparator);
                i++;
                continue;
            }

            if (c == GraphSeparator)
            {
                AddSegment(segments, current);
                continue;
            }

            current.Append(c);
        }

        AddSegment(segments, current);
        return segments;
    }

    private static void AddSegment(List<string> segments, System.Text.StringBuilder current)
    {
        var segment = current.ToString().Trim();
        current.Clear();
        if (segment.Length > 0)
        {
            segments.Add(segment);
        }
    }
}
