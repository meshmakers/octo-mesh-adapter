using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
///  Configuration to get a notification template from the database by name
/// </summary>
[NodeName("GetNotificationTemplate", 1)]
public record GetNotificationTemplateNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the notification template used for the email
    /// </summary>
    [PropertyGroup("General", 1)]
    public string? NotificationTemplateName { get; set; }

    /// <summary>
    /// Gets or sets the json path to the notification template name
    /// </summary>
    [PropertyGroup("General", 2, "jsonpath")]
    public string? NotificationTemplateNamePath { get; set; }

    /// <summary>
    /// The path where the subject of the notification template should be written
    /// </summary>
    /// <remarks>
    /// Properties 'TargetValueWriteMode' and 'TargetValueKind' are used for this property too.
    /// </remarks>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public required string SubjectTargetPath { get; set; }
}