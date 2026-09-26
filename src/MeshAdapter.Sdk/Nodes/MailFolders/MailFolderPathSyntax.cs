using System.Text;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>
///     The path syntax of the Microsoft 365 (Graph) mail channel, in ONE place (AB#5370).
///     <para>
///     A Graph folder path is the folder's display names from the mailbox root joined with
///     <c>/</c>. Inside a display name, <c>/</c> is written as <c>\/</c> and <c>\</c> as <c>\\</c>
///     — the first is a real case (one operator's folder is called <c>02_Steuern / Finanzen</c>),
///     the second is what keeps the two directions inverses of each other: without it a name
///     ending in a backslash followed by a child (<c>a\</c> → <c>b</c>) would join to <c>a\/b</c>
///     and split back into the single name <c>a/b</c>. <see cref="JoinGraphPath(string?, string)" />
///     produces the form and <see cref="SplitGraphPath" /> is its exact inverse.
///     </para>
///     <para>
///     Reading is lenient where writing is strict: <see cref="SplitGraphPath" /> consumes only the
///     two escapes <c>\\</c> and <c>\/</c>; any other <c>\x</c> stays the two literal characters
///     it always was, so a path stored before this rule existed (<c>Rechnungen\Verträge/Done</c>)
///     still reads exactly as it did.
///     </para>
///     <para>
///     <c>FromMicrosoftGraphEmail@1</c> resolves its source, done and failed folder paths with
///     <see cref="SplitGraphPath" /> (AB#5385 part 3), and prints the names in its "available
///     folders" hint through <see cref="EscapeGraphSegment" />. The picker stores what
///     <see cref="JoinGraphPath(string?, string)" /> returns, and the trigger resolves it with
///     <see cref="SplitGraphPath" /> — the two MUST stay inverses.
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

    /// <summary>The escape character; escapes <see cref="GraphSeparator" /> and itself.</summary>
    public const char GraphEscape = '\\';

    /// <summary>
    ///     Escapes one display name so it can sit inside a Graph path: <c>\</c> becomes <c>\\</c>
    ///     (first, so the escapes introduced next are not doubled), then <c>/</c> becomes <c>\/</c>.
    /// </summary>
    public static string EscapeGraphSegment(string displayName)
    {
        var sb = new StringBuilder(displayName.Length + 4);
        foreach (var c in displayName)
        {
            if (c == GraphEscape || c == GraphSeparator)
            {
                sb.Append(GraphEscape);
            }

            sb.Append(c);
        }

        return sb.ToString();
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
    ///     display names. <c>/</c> separates; <c>\\</c> is a literal backslash and <c>\/</c> a
    ///     literal slash inside a name; a backslash before any other character is itself literal.
    ///     Segments are trimmed and empty ones dropped, as the trigger has always done with its input.
    /// </summary>
    public static IReadOnlyList<string> SplitGraphPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var segments = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == GraphEscape && i + 1 < path.Length &&
                (path[i + 1] == GraphSeparator || path[i + 1] == GraphEscape))
            {
                current.Append(path[i + 1]);
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

    private static void AddSegment(List<string> segments, StringBuilder current)
    {
        var segment = current.ToString().Trim();
        current.Clear();
        if (segment.Length > 0)
        {
            segments.Add(segment);
        }
    }
}
