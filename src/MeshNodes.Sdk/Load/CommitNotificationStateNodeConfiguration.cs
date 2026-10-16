using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Records a sent notification: the rows of <c>PrepareNotification@1</c>'s result become the rows the
/// preference has seen, and the run's lease is released. Place it after <c>SendEMail@2</c>, so a failed
/// send leaves the state untouched and the next tick sends again.
/// </summary>
[NodeName("CommitNotificationState", 1)]
public record CommitNotificationStateNodeConfiguration : PathNodeConfiguration
{
    /// <summary>Initializes the node with the default source <c>$.notification</c>.</summary>
    public CommitNotificationStateNodeConfiguration()
    {
        Path = "$.notification";
    }
}
