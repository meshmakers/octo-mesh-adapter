namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// Defines graph directions in graph queries
/// </summary>
public enum GraphDirectionsDto
{
    /// <summary>All inbound directions (e. g. parent to child)</summary>
    Inbound = 1,

    /// <summary>All outbound directions (e. g. child to parent)</summary>
    Outbound = 2,

    /// <summary>All directions</summary>
    Any = 3,
}