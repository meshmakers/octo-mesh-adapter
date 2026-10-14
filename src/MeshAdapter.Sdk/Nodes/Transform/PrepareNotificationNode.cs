using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

internal sealed record PreparedNotification(
    [property: JsonPropertyName("preferenceRtId")] string PreferenceRtId,
    [property: JsonPropertyName("stateRtId")] string? StateRtId,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("renderingType")] string RenderingType,
    [property: JsonPropertyName("newCount")] int NewCount,
    [property: JsonPropertyName("seenRtIds")] IReadOnlyList<string> SeenRtIds,
    [property: JsonPropertyName("skippedRtIds")] IReadOnlyList<string> SkippedRtIds,
    [property: JsonPropertyName("queryRtId")] string QueryRtId,
    [property: JsonPropertyName("skipExistingRows")] bool SkipExistingRows,
    [property: JsonPropertyName("sent")] bool Sent = false);

[NodeConfiguration(typeof(PrepareNotificationNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class PrepareNotificationNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ICkCacheService ckCacheService,
    ISubjectChannelLookup subjectChannelLookup,
    TimeProvider? timeProvider = null) : IPipelineNode
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<PrepareNotificationNodeConfiguration>();

        var rawPreferenceRtId = dataContext.Get<string>(c.PreferenceRtIdPath);
        if (rawPreferenceRtId == null || !OctoObjectId.TryParse(rawPreferenceRtId, out var preferenceRtId))
        {
            throw MeshAdapterPipelineExecutionException.NotificationPreferenceRtIdInvalid(nodeContext,
                c.PreferenceRtIdPath, rawPreferenceRtId);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var dryRun = nodeContext.PipelineExecutionMode?.IsDryRun == true;
        var repository = etlContext.TenantRepository;

        RtNotificationPreference? preference;
        RtNotificationSubscription? subscription = null;
        RtNotificationTemplate? template = null;
        RtPersistentQuery? query = null;
        RtNotificationDeliveryState? state = null;
        IReadOnlyList<RtNotificationPreference> recipients = [];

        // AB#5028 — SYSTEM by decision: preference, subscription, template, query definition and
        // delivery state are notification configuration and engine bookkeeping, not the recipient's
        // data. Under a read filter a hidden subscription would read as removed and stop the mails
        // without a trace. The query itself runs scoped, below.
        using (var session = await etlContext.GetSystemSessionAsync())
        {
            session.StartTransaction();
            preference = await repository.GetRtEntityByRtIdAsync<RtNotificationPreference>(session, preferenceRtId);
            if (preference != null)
            {
                subscription = await NotificationDeliveryStateStore
                    .FirstTargetAsync<RtNotificationPreference, RtNotificationSubscription>(repository, session,
                        preferenceRtId, SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId, GraphDirections.Outbound);
            }

            if (subscription is { Enabled: true })
            {
                template = await NotificationDeliveryStateStore
                    .FirstTargetAsync<RtNotificationSubscription, RtNotificationTemplate>(repository, session,
                        subscription.RtId, SystemNotificationCkIds.RtCkSubscriptionTemplateRoleId, GraphDirections.Outbound);
                query = await NotificationDeliveryStateStore
                    .FirstTargetAsync<RtNotificationSubscription, RtPersistentQuery>(repository, session,
                        subscription.RtId, SystemNotificationCkIds.RtCkSubscriptionQueryRoleId, GraphDirections.Outbound);
                state = await NotificationDeliveryStateStore.LoadAsync(repository, session, preferenceRtId);
                recipients = await NotificationDeliveryStateStore
                    .TargetsAsync<RtNotificationSubscription, RtNotificationPreference>(repository, session,
                        subscription.RtId, SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId, GraphDirections.Inbound);
            }

            await session.CommitTransactionAsync();
        }

        if (preference == null || subscription is not { Enabled: true })
        {
            nodeContext.Debug("Preference '{0}' is removed or its subscription is not deployed; nothing to send",
                preferenceRtId);
            return;
        }

        if (NotificationDigest.IsLeaseHeld(state?.RunningSince, now))
        {
            nodeContext.Debug("Preference '{0}' is still being processed since {1:O}; this tick is skipped",
                preferenceRtId, state!.RunningSince!.Value);
            return;
        }

        DateTime?[] configurationChanges =
        [
            preference.RtChangedDateTime, subscription.RtChangedDateTime, template?.RtChangedDateTime,
            query?.RtChangedDateTime
        ];
        if (NotificationDigest.IsBackingOff(state?.LastError, state?.RtChangedDateTime, now,
                configurationChanges.Max()))
        {
            nodeContext.Debug("Preference '{0}' failed at {1:O}; it is retried {2} after the failure",
                preferenceRtId, state!.RtChangedDateTime!.Value, NotificationDigest.RetryDelay);
            return;
        }

        var stateRtId = state?.RtId;

        try
        {
            var subscriptionName = subscription.Name ?? subscription.RtId.ToString();
            if (template == null)
            {
                throw MeshAdapterPipelineExecutionException.NotificationSubscriptionIncomplete(nodeContext,
                    subscriptionName, "notification template");
            }

            if (query == null)
            {
                throw MeshAdapterPipelineExecutionException.NotificationSubscriptionIncomplete(nodeContext,
                    subscriptionName, "query");
            }

            if (query is not RtSimpleRtQuery simpleQuery)
            {
                throw MeshAdapterPipelineExecutionException.NotificationQueryNotSupported(nodeContext, subscriptionName,
                    query.GetType().Name);
            }

            var matching = await NotificationQueryCache.GetOrRunAsync(
                $"{etlContext.TenantId}|{subscription.RtId}|{simpleQuery.RtId}|{simpleQuery.RtChangedDateTime:O}", now,
                async () =>
                {
                    // AB#5028 — scoped: the query reads tenant business data under the pipeline's identity,
                    // which is the same for every preference, so the preferences of one firing share it.
                    using var scopedSession = await etlContext.GetScopedSessionAsync();
                    scopedSession.StartTransaction();
                    var rtIds = await SimpleRtQueryExecution.FindRtIdsAsync(etlContext, ckCacheService,
                        scopedSession, simpleQuery, NotificationDigest.MaxTrackedRows + 1);
                    await scopedSession.CommitTransactionAsync();
                    return rtIds;
                });

            if (matching.Count > NotificationDigest.MaxTrackedRows)
            {
                throw MeshAdapterPipelineExecutionException.NotificationQueryTooLarge(nodeContext, subscriptionName,
                    NotificationDigest.MaxTrackedRows);
            }

            var currentRtIds = NotificationDigest.MailOrder(matching.Select(rtId => rtId.ToString()).ToList(),
                simpleQuery.Sorting?.Any() == true);

            var queryRtId = query.RtId.ToString();
            var skipExistingRows = preference.SkipExistingRows != false;
            var otherRecipient = NotificationDigest.RecipientChanged(state?.SubjectId, preference.SubjectId);
            var seen = otherRecipient ? null : state?.SeenRtIds?.ToList();
            var skipped = otherRecipient ? null : state?.SkippedRtIds?.ToList();
            var lastRunAt = otherRecipient ||
                            NotificationDigest.StartOptionChanged(state?.SkipExistingRows, skipExistingRows)
                ? null
                : NotificationDigest.LastRunOfQuery(state?.LastRunAt, state?.QueryRtId, queryRtId);
            var decision = NotificationDigest.Decide(currentRtIds, seen, skipped, skipExistingRows, lastRunAt,
                NotificationDigest.StartOf(preference.RtCreationDateTime, subscription.DeployedAt));

            if (decision.Action != NotificationDigestAction.Send)
            {
                var unchanged = NotificationDigest.StateUnchanged(decision, seen, skipped) &&
                                state?.QueryRtId == queryRtId && state.SubjectId == preference.SubjectId &&
                                state.SkipExistingRows == skipExistingRows;
                if (!dryRun && NotificationDigest.NeedsIdleWrite(decision.Action, unchanged, state?.LastError,
                        state?.LastRunAt, now))
                {
                    await NotificationDeliveryStateStore.SaveAsync(etlContext, stateRtId, preferenceRtId,
                        new NotificationDeliveryStateChange(SeenRtIds: unchanged ? null : decision.SeenRtIds,
                            SkippedRtIds: unchanged ? null : decision.SkippedRtIds, QueryRtId: queryRtId,
                            SubjectId: preference.SubjectId, SkipExistingRows: skipExistingRows, LastRunAt: now,
                            ClearLastError: true, ClearLastSentAt: otherRecipient));
                }

                if (decision.Action == NotificationDigestAction.RecordSilently)
                {
                    nodeContext.Info("First run of preference '{0}': {1} existing row(s) recorded, nothing sent",
                        preferenceRtId, currentRtIds.Count);
                }
                else
                {
                    nodeContext.Debug("Nothing new for preference '{0}' ({1} row(s))", preferenceRtId,
                        currentRtIds.Count);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(preference.SubjectId) ||
                await subjectChannelLookup.FindBySubjectIdAsync(etlContext.TenantId, preference.SubjectId) == null)
            {
                throw MeshAdapterPipelineExecutionException.NotificationRecipientMissing(nodeContext, subscriptionName);
            }

            if (NotificationDigest.HasOlderTwin(AsRecipient(preference), recipients.Select(AsRecipient)))
            {
                throw MeshAdapterPipelineExecutionException.NotificationRecipientDuplicate(nodeContext, subscriptionName);
            }

            if (!dryRun)
            {
                var leased = await NotificationDeliveryStateStore.TryAcquireLeaseAsync(etlContext, stateRtId,
                    preferenceRtId, now, NotificationDigest.LeaseExpiry);
                if (leased == null)
                {
                    nodeContext.Debug("Preference '{0}' was taken by another run; this tick is skipped", preferenceRtId);
                    return;
                }

                stateRtId = leased;
            }

            QueryResult result;
            // AB#5028 — scoped, as above: only the rows the mail lists are read in full.
            using (var scopedSession = await etlContext.GetScopedSessionAsync())
            {
                scopedSession.StartTransaction();
                result = await SimpleRtQueryExecution.RunForRtIdsAsync(etlContext, ckCacheService, scopedSession,
                    simpleQuery, decision.NewRtIds.Take(NotificationDigest.MaxRowsPerMail)
                        .Select(rtId => new OctoObjectId(rtId)).ToList());
                await scopedSession.CommitTransactionAsync();
            }

            var newRows = result.Rows.Select(row => (IReadOnlyList<object?>)row.Values).ToList();

            var renderingType = template.RenderingType.ToString();
            var bodyKind = EMailSenderNode2.ResolveBodyFormat(renderingType, BodyFormats.Markdown) ==
                           BodyFormats.PlainText
                ? NotificationBodyKind.Plain
                : NotificationBodyKind.Markup;

            RenderedNotification rendered;
            try
            {
                rendered = NotificationDigest.Render(template.SubjectTemplate, template.BodyTemplate, bodyKind,
                    result.Columns.Select(column => column.Header).ToList(), newRows,
                    ResolveTimeZone(nodeContext, preference.TimeZone), decision.NewRtIds.Count, simpleQuery.Name);
            }
            catch (NotificationRenderException ex)
            {
                throw MeshAdapterPipelineExecutionException.NotificationRenderFailed(nodeContext, subscriptionName, ex);
            }

            dataContext.Set(c.TargetPath, new PreparedNotification(preferenceRtId.ToString(), stateRtId?.ToString(),
                    preference.SubjectId, rendered.Subject, rendered.Body, renderingType, decision.NewRtIds.Count,
                    decision.SeenRtIds, decision.SkippedRtIds, queryRtId, skipExistingRows),
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
            await next(dataContext, nodeContext);
        }
        catch (Exception ex) when (!dryRun)
        {
            try
            {
                var sent = dataContext.TryGet<PreparedNotification>(c.TargetPath, out var sentNotification) &&
                           sentNotification is { Sent: true };
                await NotificationDeliveryStateStore.SaveAsync(etlContext, stateRtId, preferenceRtId, sent
                    ? new NotificationDeliveryStateChange(SeenRtIds: sentNotification!.SeenRtIds,
                        SkippedRtIds: sentNotification.SkippedRtIds, QueryRtId: sentNotification.QueryRtId,
                        SubjectId: sentNotification.SubjectId, SkipExistingRows: sentNotification.SkipExistingRows,
                        LastRunAt: now, LastSentAt: now,
                        LastError: ex.Message)
                    : new NotificationDeliveryStateChange(LastError: ex.Message));
            }
            catch (Exception bookkeepingException)
            {
                nodeContext.Warning("The failure of preference '{0}' could not be recorded: {1}", preferenceRtId,
                    bookkeepingException.Message);
            }

            throw;
        }
    }

    private static NotificationRecipient AsRecipient(RtNotificationPreference preference) =>
        new(preference.RtId.ToString(), preference.SubjectId, preference.RtCreationDateTime);

    private static TimeZoneInfo ResolveTimeZone(INodeContext nodeContext, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return TimeZoneInfo.Utc;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            return zone;
        }

        nodeContext.Warning("Time zone '{0}' is unknown; dates are shown in UTC", timeZone);
        return TimeZoneInfo.Utc;
    }
}
