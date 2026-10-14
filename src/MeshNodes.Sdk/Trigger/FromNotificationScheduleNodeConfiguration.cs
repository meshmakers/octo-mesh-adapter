using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromNotificationSchedule. Starts one execution per schedule tick of a
/// notification preference; the communication controller schedules one recurring tick per preference
/// of a deployed subscription. The execution starts with <c>{ "tenantId": ..., "preferenceRtId": ... }</c>.
/// </summary>
[NodeName("FromNotificationSchedule", 1)]
public record FromNotificationScheduleNodeConfiguration : TriggerNodeConfiguration;
