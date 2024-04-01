using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshNodes.Nodes;

/// <summary>
/// Configuration for node logger
/// </summary>
[NodeName("Logger", 1)]
public class LoggerNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Message to log
    /// </summary>
    public string Message { get; init; } = null!;
}