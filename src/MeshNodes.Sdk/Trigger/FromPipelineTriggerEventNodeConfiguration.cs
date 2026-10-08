using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromPipelineTriggerEvent (cron / scheduled pipeline triggers)
/// </summary>
[NodeName("FromPipelineTriggerEvent", 1)]
public record FromPipelineTriggerEventNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// Opt-out of the AB#5709 protection. By default (<c>false</c>) the pipeline runs at most once at a
    /// time for this trigger, and ticks that piled up in the durable trigger queue while the adapter was
    /// down, asleep (OnDemand) or busy collapse into ONE run with the newest tick: after a 20 h outage an
    /// hourly pipeline runs once, not 20 times in parallel.
    /// <para>
    /// <c>true</c> restores the previous behaviour: every tick is executed, several at once when they are
    /// queued. Only for pipelines whose runs are independent per tick AND safe to overlap — most cron
    /// pipelines (imports, matching, recomputations) are neither.
    /// </para>
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool AllowConcurrentExecution { get; set; }
}
