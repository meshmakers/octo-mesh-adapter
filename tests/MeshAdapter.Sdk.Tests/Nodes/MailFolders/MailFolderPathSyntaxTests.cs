using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

namespace MeshAdapter.Sdk.Tests.Nodes.MailFolders;

/// <summary>
/// The Graph folder path syntax as the TRIGGER sees it (AB#5385 part 3): the separator is the
/// unescaped '/', <c>\/</c> stands for a slash INSIDE a folder name and <c>\\</c> for a
/// backslash, so a folder such as "02_Steuern / Finanzen" becomes addressable at all. These are
/// the trigger's original <c>SplitFolderPath</c> tests, moved onto the shared
/// <see cref="MailFolderPathSyntax.SplitGraphPath"/> when the trigger started delegating to it;
/// the round-trip and pre-existing-path cases already pinned by
/// <c>ListMailFoldersNodeTests</c> are not repeated here.
/// </summary>
public class MailFolderPathSyntaxTests
{
    [Fact]
    public void SplitGraphPath_EscapedSlash_IsPartOfTheSegment()
    {
        var segments = MailFolderPathSyntax.SplitGraphPath(@"Inbox/02_Steuern \/ Finanzen/1_Tecob GmbH");

        Assert.Equal(["Inbox", "02_Steuern / Finanzen", "1_Tecob GmbH"], segments);
    }

    [Fact]
    public void SplitGraphPath_PlainPath_SplitsOnEverySlash()
    {
        // The pre-AB#5385 behaviour for every existing path: split, trim, drop empties —
        // including leading, trailing and doubled separators.
        Assert.Equal(["Archive", "Invoices", "ToDo"],
            MailFolderPathSyntax.SplitGraphPath("Archive/Invoices/ToDo"));
        Assert.Equal(["Archive", "Invoices"],
            MailFolderPathSyntax.SplitGraphPath("/Archive//Invoices/"));
        Assert.Equal(["Archive", "Invoices"],
            MailFolderPathSyntax.SplitGraphPath("  Archive / Invoices  "));
    }

    [Fact]
    public void SplitGraphPath_EscapedSlashesOnly_IsOneSegment()
    {
        Assert.Equal(["a/b/c"], MailFolderPathSyntax.SplitGraphPath(@"a\/b\/c"));
    }

    [Fact]
    public void SplitGraphPath_BackslashNotBeforeSlashOrBackslash_IsLiteral()
    {
        // Only the backslash directly in front of a slash or another backslash escapes; any
        // other backslash is part of the folder name, including one at the very end of the path.
        Assert.Equal([@"Rechnungen\Verträge", "Done"],
            MailFolderPathSyntax.SplitGraphPath(@"Rechnungen\Verträge/Done"));
        Assert.Equal([@"Done\"], MailFolderPathSyntax.SplitGraphPath(@"Done\"));
    }

    [Fact]
    public void SplitGraphPath_EscapedBackslash_IsOneLiteralBackslash()
    {
        // The rule the trigger's original '/'-only escape did NOT have: "\\" is one backslash,
        // so "a\\/b" is the name "a\" with the child "b" — not the single name "a\/b". This is
        // the ONLY input class where the full rule and the old minimal escape diverge.
        Assert.Equal([@"a\", "b"], MailFolderPathSyntax.SplitGraphPath(@"a\\/b"));
        Assert.Equal([@"a\b"], MailFolderPathSyntax.SplitGraphPath(@"a\\b"));
        Assert.Equal([@"\"], MailFolderPathSyntax.SplitGraphPath(@"\\"));
    }

    [Fact]
    public void SplitGraphPath_EscapedSlashAtSegmentEdge_IsKeptAfterTrim()
    {
        // The escape produces a literal slash; only whitespace is trimmed, never the slash.
        Assert.Equal(["/Inbox", "Done/"], MailFolderPathSyntax.SplitGraphPath(@"\/Inbox/Done\/"));
    }

    [Fact]
    public void SplitGraphPath_EmptyOrSeparatorsOnly_YieldsNothing()
    {
        Assert.Empty(MailFolderPathSyntax.SplitGraphPath(""));
        Assert.Empty(MailFolderPathSyntax.SplitGraphPath("///"));
        Assert.Empty(MailFolderPathSyntax.SplitGraphPath("  /  "));
    }

    [Fact]
    public void SplitGraphPath_IsCaseAndApostrophePreserving()
    {
        // The '' doubling for the Graph $filter happens in the resolver, not here — the segment
        // must reach it untouched.
        Assert.Equal(["O'Brien / Co", "ToDo"],
            MailFolderPathSyntax.SplitGraphPath(@"O'Brien \/ Co/ToDo"));
    }

    [Fact]
    public void EscapeGraphSegment_MakesAHintNameAValidPathSegment()
    {
        // What the trigger prints in its "available folders at this level" hint: a name copied
        // from there resolves back to the same folder.
        const string name = @"02_Steuern / Finanzen\Archiv";

        var segment = MailFolderPathSyntax.EscapeGraphSegment(name);

        Assert.Equal(@"02_Steuern \/ Finanzen\\Archiv", segment);
        Assert.Equal([name], MailFolderPathSyntax.SplitGraphPath(segment));
    }
}
