namespace Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;

/// <summary>
/// One validity period of a series: the half-open time range [<see cref="From" />, <see cref="To" />)
/// in which the series is expected to hold data (e.g. the participation period of a metering point).
/// A series may have several periods; overlapping or touching periods of one series are merged.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record ValidityPeriodDto
{
    /// <summary>Runtime id of the series' source entity.</summary>
    public required string RtId { get; init; }

    /// <summary>
    /// Start of the validity, inclusive. Give it with its UTC offset; a value without an offset is
    /// read as UTC.
    /// </summary>
    public required DateTime From { get; init; }

    /// <summary>
    /// End of the validity, exclusive; leave it unset for an open end. Same offset rules as
    /// <see cref="From" />; it must be later than <see cref="From" />.
    /// </summary>
    public DateTime? To { get; init; }
}
