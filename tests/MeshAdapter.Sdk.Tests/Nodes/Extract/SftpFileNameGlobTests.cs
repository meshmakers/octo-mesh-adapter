using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

public class SftpFileNameGlobTests
{
    [Theory]
    [InlineData("AR00006946.TXT", "AR*TXT", true)]
    [InlineData("ar00006946.txt", "AR*TXT", true)]
    [InlineData("BE_20240205035403463.txt", "BE*txt", true)]
    [InlineData("AS00006946.TXT", "AR*TXT", false)]
    [InlineData("XAR00006946.TXT", "AR*TXT", false)]
    [InlineData("AR00006946.TXT.bak", "AR*TXT", false)]
    [InlineData("AR1.TXT", "AR?.TXT", true)]
    [InlineData("AR12.TXT", "AR?.TXT", false)]
    // A dot in the pattern is a literal dot, not a regex wildcard.
    [InlineData("report.2026.txt", "report.*.txt", true)]
    [InlineData("reportX2026.txt", "report.*.txt", false)]
    public void Matches_FollowsGlobSemantics(string fileName, string pattern, bool expected)
    {
        Assert.Equal(expected, SftpFileNameGlob.Matches(fileName, pattern));
    }

    [Fact]
    public void Matches_TrailingNewlineInName_DoesNotSlipThroughTheAnchor()
    {
        // '$' also matches before a final newline, and a POSIX file name may contain one.
        // Such a file would be picked up as if it were the name without it.
        Assert.False(SftpFileNameGlob.Matches("report.txt\n", "*.txt"));
    }

    [Fact]
    public async Task Matches_ManyWildcardsAgainstANearMiss_DoesNotHang()
    {
        // Each '*' becomes an independent '.*'; a backtracking engine then needs O(n^k) on a
        // near miss. The file name comes from the remote server, so a hostile peer picks it.
        var pattern = "*a*a*a*a*a*a*a*a*b";
        var fileName = new string('a', 60) + "c";

        var match = Task.Run(() => SftpFileNameGlob.Matches(fileName, pattern));
        var finished = await Task.WhenAny(match, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(match, finished);
        Assert.False(await match);
    }
}
