using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// AB#5619 / AB#5620: the Signal/Teams status line keeps the <see cref="MailPollStatusLine"/>
/// contract the accounting app parses (leading UTC timestamp, <c>ERROR </c> prefix, " · ").
/// </summary>
public class TriggerStatusLineTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 4, 20, 0, DateTimeKind.Utc);

    [Fact]
    public void Success_SignalCounts()
    {
        var line = TriggerStatusLine.Success(Now, "+4366012345678", TriggerStatusLine.SignalCounts(3, 2, 1));

        Assert.Equal("2026-10-07T04:20:00Z · +4366012345678 · received 3, processed 2, rejected 1", line);
    }

    [Fact]
    public void Error_StartsWithTheMailErrorPrefix()
    {
        var line = TriggerStatusLine.Error(Now, "/teamsBot", "inbound activity rejected:\n invalid audience");

        Assert.Equal("ERROR 2026-10-07T04:20:00Z · /teamsBot · inbound activity rejected: invalid audience", line);
        Assert.StartsWith(MailPollStatusLine.ErrorPrefix, line);
    }

    [Fact]
    public void MissingSubjectAndMessage_AreMarked()
    {
        Assert.Equal("ERROR 2026-10-07T04:20:00Z · ? · (no message)", TriggerStatusLine.Error(Now, null, " "));
    }

    [Fact]
    public void OverlongLine_IsCut()
    {
        var line = TriggerStatusLine.Error(Now, "+43", new string('x', 2000));

        Assert.Equal(MailPollStatusLine.MaxLength, line.Length);
        Assert.EndsWith("…", line);
    }
}
