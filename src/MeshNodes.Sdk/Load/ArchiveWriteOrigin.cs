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
    /// Entered or corrected by a person (or imported as such). The row records its writer as resolved by the
    /// platform: the verified user caller, else the client id of the pipeline's service identity, else
    /// <c>pipeline:&lt;rtId&gt;</c>.
    /// </summary>
    Manual = 1
}
