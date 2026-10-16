using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Prepares one notification mail for one preference: loads the preference, its subscription, the
/// subscription's query and template, runs the query and compares the result with the rows the
/// preference has already seen.
/// <para>
/// The branch ends here when the preference or its subscription is gone or undeployed, when another
/// run of the same preference is still going, when nothing is new, and on the first run after the
/// start of an Onwards preference, which records the current result without mailing it. In every
/// other case the node writes the mail to <see cref="TargetPathNodeConfiguration.TargetPath" />:
/// </para>
/// <code>
/// {
///   "preferenceRtId": "...",
///   "stateRtId": "...",           // the delivery state CommitNotificationState@1 updates
///   "subjectId": "...",           // the recipient, for ResolveNotificationChannel@1
///   "subject": "...",
///   "body": "...",
///   "renderingType": "Html",      // the template's, for SendEMail@2 BodyFormatPath
///   "newCount": 3,
///   "seenRtIds": [ "..." ],       // what CommitNotificationState@1 records as sent
///   "skippedRtIds": [ "..." ],    // rows an Onwards start recorded without sending
///   "queryRtId": "...",           // the query the rows came from; another query is a new start
///   "sent": false                 // set by CommitNotificationState@1 once the mail went out
/// }
/// </code>
/// </summary>
[NodeName("PrepareNotification", 1)]
public record PrepareNotificationNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>Initializes the node with the default sink <c>$.notification</c>.</summary>
    public PrepareNotificationNodeConfiguration()
    {
        TargetPath = "$.notification";
    }

    /// <summary>
    /// JSONPath of the preference's rtId; <c>FromNotificationSchedule@1</c> writes it to
    /// <c>$.preferenceRtId</c>.
    /// </summary>
    [PropertyGroup("General", 0, "jsonpath")]
    public string PreferenceRtIdPath { get; set; } = "$.preferenceRtId";
}
