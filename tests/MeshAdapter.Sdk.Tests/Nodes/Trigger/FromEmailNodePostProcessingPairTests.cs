using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
/// MarkAsRead is bookkeeping only for a NotSeen search: with onlyUnread off the same batch would
/// come back every poll, so that pair is refused at trigger start (5.10.2026).
/// </summary>
public class FromEmailNodePostProcessingPairTests
{
    [Fact]
    public void MarkAsRead_WithoutOnlyUnread_IsRefusedWithTheRepair()
    {
        var ex = Assert.Throws<MeshAdapterPipelineExecutionException>(
            () => FromEmailNode.EnsurePostProcessingModeWorksWithSearch(MailPostProcessingMode.MarkAsRead, false));

        Assert.Contains("MarkAsRead needs onlyUnread = true", ex.Message);
        Assert.Contains("EmailImportOnlyUnread", ex.Message);
    }

    [Theory]
    [InlineData(MailPostProcessingMode.MarkAsRead, true)]
    [InlineData(MailPostProcessingMode.MoveToFolders, false)]
    [InlineData(MailPostProcessingMode.MoveToFolders, true)]
    [InlineData(MailPostProcessingMode.Delete, false)]
    [InlineData(MailPostProcessingMode.Delete, true)]
    public void EveryOtherPair_IsAccepted(MailPostProcessingMode mode, bool onlyUnread)
    {
        FromEmailNode.EnsurePostProcessingModeWorksWithSearch(mode, onlyUnread);
    }
}
