using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// What the node does when the database refuses a write because it would duplicate a unique index.
/// </summary>
public enum DuplicateKeyHandling
{
    /// <summary>
    /// Let the exception escape, failing the pipeline. The default, and right for a write whose
    /// caller cannot mean anything sensible by a duplicate.
    /// </summary>
    Throw,

    /// <summary>
    /// Roll the write back, warn, and carry on with a flag at
    /// <see cref="ApplyChangesNodeConfiguration2.DuplicateKeyTargetPath" /> so the pipeline can
    /// answer for itself. For a write that races against another caller on a natural key.
    /// </summary>
    Report
}

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("ApplyChanges", 2)]
public record ApplyChangesNodeConfiguration2 : NodeConfiguration
{
    /// <summary>
    /// The path to the entity update
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string? EntityUpdatesPath { get; init; }

    /// <summary>
    /// The path to the association update
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string? AssociationUpdatesPath { get; init; }

    /// <summary>
    /// What to do when a unique index refuses the write. Defaults to <see cref="DuplicateKeyHandling.Throw" />,
    /// which is what the node has always done.
    ///
    /// A unique index is the only thing that holds against two callers writing the same natural key
    /// at once - a pipeline's own "does it exist yet" lookup reads before it writes, so both callers
    /// pass it. <see cref="DuplicateKeyHandling.Report" /> is for the pipeline that has a sensible
    /// answer for the loser of that race: an anonymous HTTP endpoint that would otherwise return a
    /// 500, and in a Development environment a stack trace, for what is a routine refusal.
    /// </summary>
    [PropertyGroup("Behaviour", 0)]
    public DuplicateKeyHandling OnDuplicateKey { get; init; } = DuplicateKeyHandling.Throw;

    /// <summary>
    /// Where <c>true</c> is written when a unique index refused the write and
    /// <see cref="OnDuplicateKey" /> is <see cref="DuplicateKeyHandling.Report" />. Nothing is
    /// written on the way through, so the pipeline must treat an absent value as "no duplicate".
    /// </summary>
    [PropertyGroup("Behaviour", 1, "jsonpath")]
    public string? DuplicateKeyTargetPath { get; init; }
}
