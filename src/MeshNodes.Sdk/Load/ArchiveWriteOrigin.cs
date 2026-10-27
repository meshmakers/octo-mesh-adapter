namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Origin an archive load node declares for the rows it writes (AB#5356, AB#7596): whether the values were
/// entered manually or delivered automatically. Written in pipeline YAML by member name
/// (<c>origin: Manual</c>); any other value fails the pipeline registration.
/// </summary>
public enum ArchiveWriteOrigin
{
    /// <summary>Delivered by a system: an adapter, a pipeline, an import. The default.</summary>
    Automated = 0,

    /// <summary>
    /// Entered or corrected by a person. The row records the execution's verified caller as its writer, so
    /// the pipeline must be triggered by an authenticated caller; without one the node refuses the write.
    /// </summary>
    Manual = 1
}
