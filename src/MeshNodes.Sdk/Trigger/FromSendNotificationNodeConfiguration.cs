using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromSendNotification. This node is triggered when a OctoMesh service
/// needs to send a notification.
/// </summary>
[NodeName("FromSendNotification", 1)]
[NodeRequiresRunningProcess]
public record FromSendNotificationNodeConfiguration : TriggerNodeConfiguration;