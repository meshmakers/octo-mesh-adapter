using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes;

/// <summary>
/// The temporary name of <c>SftpDelete@1</c> (AB#8284): how it is built, and the strict check
/// <c>SftpList@1</c> uses to keep such a file out of a listing. A name that only resembles the
/// form is not recognized, so no delivery disappears from a listing by accident.
/// </summary>
public class SftpParkedNameTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("/out/AR00001.TXT", @"^/out/\.AR00001\.TXT\.sftpdelete-[0-9a-f]{32}$")]
    [InlineData("/AR00001.TXT", @"^/\.AR00001\.TXT\.sftpdelete-[0-9a-f]{32}$")]
    [InlineData("AR00001.TXT", @"^\.AR00001\.TXT\.sftpdelete-[0-9a-f]{32}$")]
    [InlineData("/a/b/BE_20240205035403463.txt", @"^/a/b/\.BE_20240205035403463\.txt\.sftpdelete-[0-9a-f]{32}$")]
    public void PathFor_StaysInTheSameDirectory_AndCarriesTheName(string remotePath, string expected)
    {
        Assert.Matches(expected, SftpParkedName.PathFor(remotePath));
        Assert.NotEqual(SftpParkedName.PathFor(remotePath), SftpParkedName.PathFor(remotePath));
    }

    [Theory]
    [InlineData("/out/AR00001.TXT")]
    [InlineData("/out/.hidden")]
    [InlineData("/out/name with spaces")]
    [InlineData("/out/line\nbreak.TXT")]
    [InlineData("/out/.x.sftpdelete-0123456789abcdef0123456789abcdef")]
    public void PathFor_BuildsANameThatIsRecognized(string remotePath)
    {
        // Building and recognizing stay one form: SftpList@1 skips exactly what SftpDelete@1 creates.
        var parked = SftpParkedName.PathFor(remotePath);

        Assert.True(SftpParkedName.IsParkedName(parked[(parked.LastIndexOf('/') + 1)..]));
    }

    [Theory]
    [InlineData(".AR00001.TXT.sftpdelete-" + Hex, true)]
    [InlineData(".A.sftpdelete-" + Hex, true)]
    // the original name may hold a line break, and its temporary name then does too
    [InlineData(".AR\n00001.TXT.sftpdelete-" + Hex, true)]
    // the temporary name of a temporary name
    [InlineData("..AR.TXT.sftpdelete-" + Hex + ".sftpdelete-" + Hex, true)]
    // hex digits and marker exactly as written: lower case only
    [InlineData(".AR00001.TXT.sftpdelete-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData(".AR00001.TXT.SFTPDELETE-" + Hex, false)]
    [InlineData(".AR00001.TXT.SftpDelete-" + Hex, false)]
    // exactly 32 hex digits
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcde", false)]
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcdef0", false)]
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef0123456789abcdeg", false)]
    [InlineData(".AR00001.TXT.sftpdelete-0123456789abcdef-0123456789abcde", false)]
    // the leading dot is part of the form
    [InlineData("AR00001.TXT.sftpdelete-" + Hex, false)]
    // the marker in the middle of the name: the GUID has to end it
    [InlineData(".AR.sftpdelete-" + Hex + ".TXT", false)]
    [InlineData(".AR00001.TXT.sftpdelete-" + Hex + "\n", false)]
    [InlineData(" .AR00001.TXT.sftpdelete-" + Hex, false)]
    // nothing of an original name between the dot and the marker
    [InlineData("..sftpdelete-" + Hex, false)]
    [InlineData(".sftpdelete-" + Hex, false)]
    [InlineData("", false)]
    public void IsParkedName_RecognizesExactlyTheForm(string fileName, bool parked)
    {
        Assert.Equal(parked, SftpParkedName.IsParkedName(fileName));
    }

    [Fact]
    public void ListingPattern_MatchesATemporaryName()
    {
        var parked = SftpParkedName.PathFor("/out/AR00001.TXT");

        Assert.True(SftpFileNameGlob.Matches(parked[(parked.LastIndexOf('/') + 1)..], SftpParkedName.ListingPattern));
        Assert.Contains(SftpParkedName.Marker, SftpParkedName.ListingPattern, StringComparison.Ordinal);
    }
}
