using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Save in time series node configuration
/// </summary>
[NodeName("SaveInTimeSeries", 1)]
public record SaveInTimeSeriesNodeConfiguration : PathNodeConfiguration;