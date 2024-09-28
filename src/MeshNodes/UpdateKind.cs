namespace Meshmakers.Octo.MeshAdapter.Nodes;

/// <summary>
/// Defines the kind of update
/// </summary>
public enum UpdateKind
{
    /// <summary>
    /// Updates an existing rt entity
    /// </summary>
    Update = 0,
    
    /// <summary>
    /// Inserts a new rt entity
    /// </summary>
    Insert = 1
}