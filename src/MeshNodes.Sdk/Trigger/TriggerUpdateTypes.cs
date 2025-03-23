namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Update types for watch triggers.
/// </summary>
public enum TriggerUpdateTypes
{
    /// <summary>
    ///     Not flags defined.
    /// </summary>
    Undefined = 0,

    /// <summary>An insert operation type.</summary>
    Insert = 1,

    /// <summary>An update operation type.</summary>
    Update = 2,

    /// <summary>A replace operation type.</summary>
    Replace = 4,

    /// <summary>A delete operation type.</summary>
    Delete = 8,

    /// <summary>All operation types.</summary>
    All = Insert | Update | Replace | Delete
}