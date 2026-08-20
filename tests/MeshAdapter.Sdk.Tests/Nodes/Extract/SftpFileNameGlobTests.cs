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
}
