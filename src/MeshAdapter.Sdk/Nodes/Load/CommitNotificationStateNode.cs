using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Records a sent notification. See <see cref="CommitNotificationStateNodeConfiguration" />.
/// </summary>
[NodeConfiguration(typeof(CommitNotificationStateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class CommitNotificationStateNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IAdapterEventService eventService,
    ISubjectChannelLookup subjectChannelLookup,
    TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CommitNotificationStateNodeConfiguration>();

        if (!dataContext.TryGet<PreparedNotification>(c.Path, out var prepared) || prepared == null ||
            !OctoObjectId.TryParse(prepared.PreferenceRtId, out var preferenceRtId))
        {
            throw MeshAdapterPipelineExecutionException.NotificationStateMissing(nodeContext, c.Path);
        }

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.CommitNotificationState, new
            {
                preferenceRtId = prepared.PreferenceRtId,
                newCount = prepared.NewCount,
                sentRowCount = prepared.SentRows.Count
            });
            await next(dataContext, nodeContext);
            return;
        }

        if (prepared.StateRtId == null || !OctoObjectId.TryParse(prepared.StateRtId, out var stateRtId) ||
            prepared.Lease is not { } lease)
        {
            throw MeshAdapterPipelineExecutionException.NotificationStateMissing(nodeContext, c.Path);
        }

        dataContext.Set(c.Path, prepared with { Sent = true });

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var applied = await NotificationDeliveryStateStore.SaveHeldAsync(etlContext, stateRtId, preferenceRtId,
            new NotificationDeliveryStateChange(ProcessedUntil: prepared.ProcessedUntil, SentRows: prepared.SentRows,
                QueryRtId: prepared.QueryRtId, SubjectId: prepared.SubjectId,
                SkipExistingRows: prepared.SkipExistingRows, LastRunAt: now, LastSentAt: now, ClearLastError: true,
                ReleaseLease: true),
            lease);

        if (applied && await NotificationDeliveryStateStore.RecordedSendAtAsync(etlContext, preferenceRtId, now))
        {
            nodeContext.Info("Notification for preference '{0}' sent with {1} new row(s)", preferenceRtId,
                prepared.NewCount);
        }
        else
        {
            var recipient = await RecipientLabelAsync(prepared.SubjectId);
            var message =
                $"Notification for recipient {recipient} was sent with {prepared.NewCount} new row(s), but another run took over; that run keeps the delivery state and may send these rows again";
            nodeContext.Warning("{0}", message);
            await eventService.StoreWarningEventAsync(etlContext.TenantId, message,
                new RtEntityId(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preferenceRtId));
        }

        await next(dataContext, nodeContext);
    }

    private async Task<string?> RecipientLabelAsync(string? subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return subjectId;
        }

        try
        {
            var user = await subjectChannelLookup.FindBySubjectIdAsync(etlContext.TenantId, subjectId);
            return !string.IsNullOrWhiteSpace(user?.Email) ? user.Email : subjectId;
        }
        catch (Exception)
        {
            return subjectId;
        }
    }
}
