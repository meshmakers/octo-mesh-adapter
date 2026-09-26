using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// Tests for the folder path syntax of <see cref="FromMicrosoftGraphEmailNode"/> (AB#5385):
/// the separator is the unescaped '/', and <c>\/</c> stands for a slash INSIDE a folder name,
/// so a folder such as "02_Steuern / Finanzen" becomes addressable at all.
/// </summary>
public class FromMicrosoftGraphEmailNodeFolderPathTests
{
    [Fact]
    public void SplitFolderPath_EscapedSlash_IsPartOfTheSegment()
    {
        var segments = FromMicrosoftGraphEmailNode.SplitFolderPath(@"Inbox/02_Steuern \/ Finanzen/1_Tecob GmbH");

        Assert.Equal(["Inbox", "02_Steuern / Finanzen", "1_Tecob GmbH"], segments);
    }

    [Fact]
    public void SplitFolderPath_PlainPath_SplitsOnEverySlash()
    {
        // The pre-AB#5385 behaviour for every existing path: split, trim, drop empties.
        Assert.Equal(["Archive", "Invoices", "ToDo"],
            FromMicrosoftGraphEmailNode.SplitFolderPath("Archive/Invoices/ToDo"));
        Assert.Equal(["Archive", "Invoices"],
            FromMicrosoftGraphEmailNode.SplitFolderPath("/Archive//Invoices/"));
        Assert.Equal(["Archive", "Invoices"],
            FromMicrosoftGraphEmailNode.SplitFolderPath("  Archive / Invoices  "));
    }

    [Fact]
    public void SplitFolderPath_EscapedSlashesOnly_IsOneSegment()
    {
        Assert.Equal(["a/b/c"], FromMicrosoftGraphEmailNode.SplitFolderPath(@"a\/b\/c"));
    }

    [Fact]
    public void SplitFolderPath_BackslashNotFollowedBySlash_IsLiteral()
    {
        // Only the backslash directly in front of a slash escapes; any other backslash is
        // part of the folder name, including one at the very end of the path.
        Assert.Equal([@"Rechnungen\Verträge", "Done"],
            FromMicrosoftGraphEmailNode.SplitFolderPath(@"Rechnungen\Verträge/Done"));
        Assert.Equal([@"Done\"], FromMicrosoftGraphEmailNode.SplitFolderPath(@"Done\"));
    }

    [Fact]
    public void SplitFolderPath_EscapedSlashAtSegmentEdge_IsKeptAfterTrim()
    {
        // The escape produces a literal slash; only whitespace is trimmed, never the slash.
        Assert.Equal(["/Inbox", "Done/"], FromMicrosoftGraphEmailNode.SplitFolderPath(@"\/Inbox/Done\/"));
    }

    [Fact]
    public void SplitFolderPath_EmptyOrSeparatorsOnly_YieldsNothing()
    {
        Assert.Empty(FromMicrosoftGraphEmailNode.SplitFolderPath(""));
        Assert.Empty(FromMicrosoftGraphEmailNode.SplitFolderPath("///"));
        Assert.Empty(FromMicrosoftGraphEmailNode.SplitFolderPath("  /  "));
    }

    [Fact]
    public void SplitFolderPath_IsCaseAndApostrophePreserving()
    {
        // The '' doubling for the Graph $filter happens in the resolver, not here — the segment
        // must reach it untouched.
        Assert.Equal(["O'Brien / Co", "ToDo"],
            FromMicrosoftGraphEmailNode.SplitFolderPath(@"O'Brien \/ Co/ToDo"));
    }
}
