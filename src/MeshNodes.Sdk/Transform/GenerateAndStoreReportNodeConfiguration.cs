using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for node GenerateReport that generates a report and stores it to the file system
/// </summary>
[NodeName("GenerateAndStoreReport", 1)]
public record GenerateAndStoreReportNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// The uri of the folder in the file system where the report should be stored, e.g., /demo under the root folder of Reports.
    /// </summary>
    public string? FileSystemFolderUri { get; set; } = "/";

    /// <summary>
    /// The uri of the report definition, e.g. /demo/report.trdp under the root folder of Report definitions.
    /// </summary>
    public string? ReportDefinitionUri { get; set; }

    /// <summary>
    /// The prefix of the report file name, e.g. 'report-'. To the prefix the date and time are added in format yyyyMMdd-HHmmssFFF
    /// </summary>
    public string? ReportFileNamePrefix { get; set; }

    /// <summary>
    /// The (optional) runtime id of an entity that is set related to the report comes with <see cref="RelatedRtId"/> or <see cref="RelatedRtIdPath"/>.
    /// </summary>
    public string? RelatedRtIdPath { get; set; }

    /// <summary>
    /// The (optional) runtime id of an entity that is set related to the report comes with <see cref="RelatedCkTypeId"/> or <see cref="RelatedCkTypeIdPath"/>.
    /// </summary>
    public OctoObjectId? RelatedRtId { get; set; }


    /// <summary>
    /// Optional CkTypeId of related CkTypeId, comes with <see cref="RelatedRtId"/> or <see cref="RelatedRtIdPath"/>.
    /// </summary>
    public RtCkId<CkTypeId>? RelatedCkTypeId { get; set; }

    /// <summary>
    /// Optional path to the CkTypeId of the related CkTypeId, comes with <see cref="RelatedRtId"/> or <see cref="RelatedRtIdPath"/>.
    /// </summary>
    public string? RelatedCkTypeIdPath { get; set; }

    /// <summary>
    /// Path parameters to be replaced in the URL
    /// </summary>
    public List<HttpPathParameter> ReportParameters { get; set; } = new();
}