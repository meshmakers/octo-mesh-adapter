using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;

/// <summary>
///     Builds one isolated notification scenario in the fixture's tenant and runs the engine nodes on it.
///     Every scenario filters its query by its own serial-number prefix, so scenarios never see each
///     other's rows.
/// </summary>
internal sealed class NotificationEngineHarness
{
    public const string SubjectTemplate = "${count} new readings";
    public const string BodyTemplate = "${SerialNumber} at ${SourceDate}";

    private readonly NotificationEngineFixture _fixture;
    private readonly string _prefix = $"S{Guid.NewGuid():N}"[..13];
    private readonly HashSet<string> _knownSubjects = new(StringComparer.Ordinal);
    private int _serial;

    public NotificationEngineHarness(NotificationEngineFixture fixture)
    {
        _fixture = fixture;
        // Ahead of the wall clock, so every entity the scenario creates is older than its first tick.
        Clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(5));
        SubjectLookup = A.Fake<ISubjectChannelLookup>();
        A.CallTo(() => SubjectLookup.FindBySubjectIdAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string subjectId, CancellationToken _) =>
                Task.FromResult(_knownSubjects.Contains(subjectId)
                    ? new SubjectChannelRecord(subjectId, $"{subjectId}@example.com", subjectId, null, null, [])
                    : null));
    }

    public TestClock Clock { get; }

    public ISubjectChannelLookup SubjectLookup { get; }

    public OctoObjectId SubscriptionRtId { get; private set; }

    public OctoObjectId TemplateRtId { get; private set; }

    public OctoObjectId QueryRtId { get; private set; }

    private ITenantRepository Repository => _fixture.TenantRepository;

    private ICkCacheService CkCache => _fixture.GetService<ICkCacheService>();

    public void KnowSubject(string subjectId) => _knownSubjects.Add(subjectId);

    public void ForgetSubject(string subjectId) => _knownSubjects.Remove(subjectId);

    public void Advance(TimeSpan by)
    {
        Clock.Advance(by);
    }

    public async Task<IReadOnlyList<OctoObjectId>> AddReadingsAsync(int count, DateTime? sourceDate = null)
    {
        var entities = new List<IEntityUpdateInfo<RtEntity>>();
        var rtIds = new List<OctoObjectId>();
        for (var i = 0; i < count; i++)
        {
            var reading = new RtEntity(NotificationEngineFixture.SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
            reading.SetAttributeRawValue("SerialNumber", $"{_prefix}-{++_serial:D4}");
            reading.SetAttributeRawValue("SourceDate", sourceDate ?? new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(reading));
            rtIds.Add(reading.RtId);
        }

        await ApplyAsync(entities, []);
        return rtIds;
    }

    public string SerialOf(int index) => $"{_prefix}-{index:D4}";

    public async Task<OctoObjectId> AddForeignReadingAsync()
    {
        var reading = new RtEntity(NotificationEngineFixture.SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
        reading.SetAttributeRawValue("SerialNumber", $"X{_prefix}");
        reading.SetAttributeRawValue("SourceDate", new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
        await ApplyAsync([EntityUpdateInfo<RtEntity>.CreateInsert(reading)], []);
        return reading.RtId;
    }

    public async Task SortQueryAsync(string attributePath, RtSortOrdersEnum sortOrder) =>
        await UpdateAsync<RtSimpleRtQuery>(await SimpleQueryTypeIdAsync(), QueryRtId, query =>
            query.Sorting = new AttributeRecordValueList<RtSortOrderItemRecord>
            {
                new() { AttributePath = attributePath, SortOrder = sortOrder }
            });

    public async Task FilterQueryAsync(string attributePath) =>
        await UpdateAsync<RtSimpleRtQuery>(await SimpleQueryTypeIdAsync(), QueryRtId, query =>
            query.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
            {
                new()
                {
                    AttributePath = "SerialNumber",
                    Operator = RtFieldFilterOperatorEnum.MatchRegEx,
                    ComparisonValue = $"^{_prefix}-"
                },
                new() { AttributePath = attributePath, Operator = RtFieldFilterOperatorEnum.Equals, ComparisonValue = "false" }
            });

    public async Task<OctoObjectId> CreateSubscriptionAsync(bool deployed = true, string bodyTemplate = BodyTemplate)
    {
        QueryRtId = await CreateQueryAsync($"{_prefix} readings");

        var template = new RtNotificationTemplate
        {
            RtId = OctoObjectId.GenerateNewId(),
            SubjectTemplate = SubjectTemplate,
            BodyTemplate = bodyTemplate,
            Type = RtNotificationTypesEnum.EMail,
            RenderingType = RtRenderingTypesEnum.Plain
        };
        TemplateRtId = template.RtId;

        var subscription = new RtNotificationSubscription
        {
            RtId = OctoObjectId.GenerateNewId(),
            Name = $"{_prefix} subscription",
            Enabled = deployed,
            DeployedAt = deployed ? DateTime.UtcNow : null
        };
        SubscriptionRtId = subscription.RtId;

        List<AssociationUpdateInfo> associations =
        [
            Link(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, subscription.RtId,
                SystemNotificationCkIds.RtCkNotificationTemplateTypeId, template.RtId,
                SystemNotificationCkIds.RtCkSubscriptionTemplateRoleId),
            Link(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, subscription.RtId,
                await SimpleQueryTypeIdAsync(), QueryRtId, SystemNotificationCkIds.RtCkSubscriptionQueryRoleId)
        ];

        await ApplyAsync(
        [
            EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationTemplateTypeId, template),
            EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId,
                subscription)
        ], associations);

        return subscription.RtId;
    }

    public async Task<OctoObjectId> CreateQueryAsync(string name)
    {
        var query = await Repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        query.Name = name;
        query.QueryCkTypeId = NotificationEngineFixture.SensorReadingCkTypeId.ToString();
        query.Columns = new AttributeStringValueList(["SerialNumber", "SourceDate"]);
        query.FieldFilter = new AttributeRecordValueList<RtFieldFilterRecord>
        {
            new()
            {
                AttributePath = "SerialNumber",
                Operator = RtFieldFilterOperatorEnum.MatchRegEx,
                ComparisonValue = $"^{_prefix}-"
            }
        };

        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        await Repository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();
        return query.RtId;
    }

    public async Task<OctoObjectId> CreatePreferenceAsync(string subjectId, bool onwards = true,
        string? timeZone = "Europe/Vienna")
    {
        KnowSubject(subjectId);
        var preference = new RtNotificationPreference
        {
            RtId = OctoObjectId.GenerateNewId(),
            SubjectId = subjectId,
            CronExpression = "0 7 * * *",
            SkipExistingRows = onwards,
            TimeZone = timeZone
        };

        await ApplyAsync(
            [EntityUpdateInfo<RtEntity>.CreateInsert(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference)],
            [
                Link(SystemNotificationCkIds.RtCkNotificationPreferenceTypeId, preference.RtId,
                    SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, SubscriptionRtId,
                    SystemNotificationCkIds.RtCkPreferenceSubscriptionRoleId)
            ]);

        return preference.RtId;
    }

    public async Task UpdateAsync<T>(RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId, Action<T> change)
        where T : RtEntity, new()
    {
        var entity = new T { RtId = rtId };
        change(entity);
        await ApplyAsync([EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(ckTypeId, rtId), entity)], []);
    }

    public async Task DeleteTemplateAsync() =>
        await ApplyAsync(
            [EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(SystemNotificationCkIds.RtCkNotificationTemplateTypeId, TemplateRtId))],
            []);

    public async Task RepointQueryAsync(OctoObjectId newQueryRtId)
    {
        var queryType = await SimpleQueryTypeIdAsync();
        await ApplyAsync([],
        [
            AssociationUpdateInfo.CreateDelete(
                new RtEntityId(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, SubscriptionRtId),
                new RtEntityId(queryType, QueryRtId), SystemNotificationCkIds.RtCkSubscriptionQueryRoleId),
            Link(SystemNotificationCkIds.RtCkNotificationSubscriptionTypeId, SubscriptionRtId, queryType,
                newQueryRtId, SystemNotificationCkIds.RtCkSubscriptionQueryRoleId)
        ]);
        QueryRtId = newQueryRtId;
    }

    public async Task<RtNotificationDeliveryState?> StateOfAsync(OctoObjectId preferenceRtId)
    {
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        var targets = await Repository
            .GetRtAssociationTargetsAsync<RtNotificationPreference, RtNotificationDeliveryState>(session,
                [preferenceRtId], SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Inbound,
                null, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return targets.Values.SelectMany(set => set.Items).SingleOrDefault();
    }

    public async Task<int> StateCountAsync(OctoObjectId preferenceRtId)
    {
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        var targets = await Repository
            .GetRtAssociationTargetsAsync<RtNotificationPreference, RtNotificationDeliveryState>(session,
                [preferenceRtId], SystemNotificationCkIds.RtCkDeliveryStatePreferenceRoleId, GraphDirections.Inbound,
                null, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return targets.Values.SelectMany(set => set.Items).Count();
    }

    /// <summary>Runs <c>PrepareNotification@1</c> for one tick; <paramref name="next" /> stands for the send.</summary>
    public async Task<TickResult> TickAsync(OctoObjectId preferenceRtId, Func<IDataContext, INodeContext, Task>? next = null,
        bool dryRun = false, string? rawPreferenceRtId = null)
    {
        var input = JsonSerializer.Serialize(new { preferenceRtId = rawPreferenceRtId ?? preferenceRtId.ToString() });
        using var document = JsonDocument.Parse(input);
        using var dataContext = new DataContextImpl(document.RootElement);

        var config = new PrepareNotificationNodeConfiguration();
        var rootContext = dryRun
            ? NodeContext.CreateRootNodeContext(_fixture.Provider!, A.Fake<IPipelineLogger>(), "PrepareNotification",
                config, A.Fake<IPipelineDebugger>(), new DefaultPipelineExecutionMode { IsDryRun = true })
            : NodeContext.CreateRootNodeContext(_fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = dryRun ? rootContext : rootContext.RegisterChildNode("PrepareNotification", 0, config, dataContext);

        PreparedNotification? prepared = null;
        var sent = false;

        async Task Next(IDataContext dc, INodeContext nc)
        {
            sent = true;
            prepared = dc.Get<PreparedNotification>(config.TargetPath);
            if (next != null)
            {
                await next(dc, nc);
            }
        }

        var node = new PrepareNotificationNode(Next, CreateEtlContext(), CkCache, SubjectLookup, Clock);
        Exception? failure = null;
        try
        {
            await node.ProcessObjectAsync(dataContext, nodeContext);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return new TickResult(sent, prepared, failure);
    }

    /// <summary>A tick whose send succeeds and is followed by <c>CommitNotificationState@1</c>.</summary>
    public Task<TickResult> TickAndCommitAsync(OctoObjectId preferenceRtId) =>
        TickAsync(preferenceRtId, (dc, _) => CommitAsync(dc));

    public async Task CommitAsync(IDataContext dataContext)
    {
        var config = new CommitNotificationStateNodeConfiguration { Path = "$.notification" };
        var rootContext = NodeContext.CreateRootNodeContext(_fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var commitContext = rootContext.RegisterChildNode("CommitNotificationState", 1, config, dataContext);
        var node = new CommitNotificationStateNode((_, _) => Task.CompletedTask, CreateEtlContext(), Clock);
        await node.ProcessObjectAsync(dataContext, commitContext);
    }

    public MeshEtlContext CreateEtlContext()
    {
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns([]);
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var pipelineId = new OctoObjectId("670000000000000000002581");

        return new MeshEtlContext(
            tenantId: Repository.TenantId,
            tenantRepository: Repository,
            dataFlowRtId: new OctoObjectId("670000000000000000002580"),
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System.Communication/Pipeline", pipelineId),
            adapterReceivedDateTime: Clock.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>());
    }

    private async Task<RtCkId<CkTypeId>> SimpleQueryTypeIdAsync() =>
        (await Repository.CreateTransientRtEntityAsync<RtSimpleRtQuery>()).CkTypeId!;

    private static AssociationUpdateInfo Link(RtCkId<CkTypeId> originType, OctoObjectId origin,
        RtCkId<CkTypeId> targetType, OctoObjectId target, RtCkId<CkAssociationRoleId> role) =>
        AssociationUpdateInfo.CreateInsert(new RtEntityId(originType, origin), new RtEntityId(targetType, target), role);

    private async Task ApplyAsync(IReadOnlyList<IEntityUpdateInfo<RtEntity>> entities,
        IReadOnlyList<AssociationUpdateInfo> associations)
    {
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await Repository.ApplyChangesAsync(session, entities, associations, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(string.Join(", ",
                operationResult.Messages.Select(message => message.MessageText)));
        }

        await session.CommitTransactionAsync();
    }
}

internal sealed record TickResult(bool Sent, PreparedNotification? Prepared, Exception? Failure);

internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public DateTime UtcNow => _now.UtcDateTime;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
