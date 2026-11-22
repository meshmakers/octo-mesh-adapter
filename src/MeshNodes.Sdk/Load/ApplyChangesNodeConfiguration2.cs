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
    /// A write conflict with another transaction that does not clear within the node's retries
    /// (for example a second adapter replica holding the same key) is reported the same way (AB#9466).
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
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
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
    /// 500, and in a Development environment a stack trace, for what is a routine refusal, or a CK
    /// method pipeline that books a slot and answers its declared error <c>CONCURRENT_CHANGE</c>
    /// (sample: <c>docs/examples/slot-booking-method.yaml</c>).
    ///
    /// Concurrent writers (AB#9466): within one adapter process the node writes one batch at a time.
    /// Two adapter replicas do not share that lock; the second writer then meets a MongoDB
    /// WriteConflict (112). The node aborts that transaction and retries up to six times with a short
    /// jittered backoff (about 0.4 to 0.8 seconds in total). Once the first writer has committed, the
    /// retry meets the unique index and is reported as a duplicate. When the conflict outlasts the
    /// retries, <see cref="DuplicateKeyHandling.Report" /> reports it as a conflict (flag <c>true</c>,
    /// nothing written) and <see cref="DuplicateKeyHandling.Throw" /> fails the pipeline - never a
    /// silent success.
    ///
    /// The unique index belongs to the CK type. Declare it in the type's first version: adding a
    /// unique index to an existing type that already holds data is a Major change of the CK model and
    /// fails on duplicates already stored (AB#5347).
    /// </summary>
    [PropertyGroup("Behaviour", 0)]
    public DuplicateKeyHandling OnDuplicateKey { get; init; } = DuplicateKeyHandling.Throw;

    /// <summary>
    /// Where the outcome is written when <see cref="OnDuplicateKey" /> is
    /// <see cref="DuplicateKeyHandling.Report" />: <c>true</c> when a unique index refused the write or a
    /// write conflict outlasted the retries (nothing was written), <c>false</c> when the write was
    /// stored. Required for Report; without it the node throws as with
    /// <see cref="DuplicateKeyHandling.Throw" />. When the engine reports errors in its operation result
    /// (a validation error), the node logs an error, stores nothing and writes no flag, so the value is
    /// absent or still the one of an earlier run against the same data document: read only an explicit
    /// <c>false</c> as "stored".
    /// </summary>
    [PropertyGroup("Behaviour", 1, "jsonpath")]
    public string? DuplicateKeyTargetPath { get; init; }
}
