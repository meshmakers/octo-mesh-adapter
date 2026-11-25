using System.Diagnostics;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     AB#9770 acceptance criteria against a real tenant tree in a real MongoDB (<see cref="TenantSubtreeFixture" />):
///     group G maintains contacts once; a sync pipeline of G (real YAML, real orchestrator, the registered nodes, and for
///     the timing criterion the real <c>FromWatchRtEntity@1</c> on G's change stream) writes them one-way into its
///     companies A and B with ApplyChanges@2 <c>targetTenants</c>, through the real guard on the real registry. Only the
///     identity service is scripted: it proves the service account's right in a company only where a grant was given.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class MasterDataSyncIntegrationTests(TenantSubtreePipelineFixture fixture)
    : IClassFixture<TenantSubtreePipelineFixture>
{
    private const string Generation = "gen-1";
    private const string Model = "MeshAdapterIntegrationTest";
    private const string Group = TenantSubtreeFixture.Group;
    private const string CompanyA = TenantSubtreeFixture.TenantA;
    private const string CompanyB = TenantSubtreeFixture.TenantB;
    private static readonly RtCkId<CkTypeId> ContactType = new($"{Model}/SyncedContact");
    private static readonly TimeSpan SyncDeadline = TimeSpan.FromSeconds(60);

    /// <summary>Target tenants in which the scripted identity service proves the service account's right.</summary>
    private readonly HashSet<string> _grants = [CompanyA, CompanyB];

    private ICrossTenantAccessGuard CreateGuard()
    {
        var provider = A.Fake<ICrossTenantIdentityProvider>();
        A.CallTo(() => provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                _grants.Contains(r.TargetTenantId.ToLowerInvariant()) &&
                r.SubjectKind == CrossTenantSubjectKind.ServiceAccount
                    ? new CrossTenantProof(r.TargetTenantId, Generation,
                        RtSecurityContext.ForUser($"xt_{r.SourceTenantId}_{r.SourceIdentity.SubjectId}",
                            ["TenantDataWriter"]))
                    : null));
        var generations = A.Fake<ITenantGenerationReader>();
        A.CallTo(() => generations.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>(Generation));

        var systemContext = fixture.GetSystemContext();
        return new CrossTenantAccessGuard(systemContext, fixture.GetService<ICkCacheService>(),
            new TenantRegistryReader(systemContext, fixture.GetService<IOptions<OctoSystemConfiguration>>()),
            provider, NullLogger<CrossTenantAccessGuard>.Instance, generations);
    }

    private async Task<IMeshEtlContext> CreateContextAsync(string sourceTenantId)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(sourceTenantId);
        var id = new OctoObjectId("000000000000000000000097");
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var resolver = A.Fake<IPipelineIdentityResolver>();
        A.CallTo(() => resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.ForUser("sa-client", ["AdminOfSource"])));
        // The sync pipelines name identity ServiceAccount (a watch trigger has no caller); the caller is there for
        // the local pipelines of a company, which write their own tenant as the caller.
        var caller = new VerifiedPrincipal($"user-of-{sourceTenantId}", sourceTenantId, null, null, ["AdminOfSource"]);
        return new MeshEtlContext(sourceTenantId, repository, id, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", id), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), caller, "caller-token", resolver, crossTenantAccessGuard: CreateGuard());
    }

    private async Task RunAsync(string sourceTenantId, string yaml, object input)
    {
        fixture.EnsureInitialized();
        var etlContext = await CreateContextAsync(sourceTenantId);
        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        await fixture.GetService<IEtlDataOrchestrator>().ExecutePipelineAsync(definition, etlContext, value: input);
    }

    private static List<Exception> Chain(Exception exception)
    {
        var chain = new List<Exception>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            chain.Add(e);
        }

        return chain;
    }

    private async Task<T> FailsWithAsync<T>(string sourceTenantId, string yaml, object input) where T : Exception
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(sourceTenantId, yaml, input));
        return Assert.Single(Chain(exception).OfType<T>());
    }

    // ---- data -----------------------------------------------------------------------------------------------

    private async Task<OctoObjectId> WriteAsync(string tenantId, EntityUpdateInfo<RtEntity> update)
    {
        fixture.EnsureInitialized();
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session, [update], result);
        Assert.False(result.HasErrors || result.HasFatalErrors, result.GetMessages());
        await session.CommitTransactionAsync();
        return update.RtId ?? update.RtEntity!.RtId;
    }

    private static RtEntity Contact(OctoObjectId rtId, string name, string? email = null)
    {
        var entity = new RtEntity(ContactType, rtId);
        entity.SetAttributeRawValue("ContactName", name);
        if (email != null)
        {
            entity.SetAttributeRawValue("ContactEmail", email);
        }

        return entity;
    }

    private Task<OctoObjectId> CreateContactAsync(string tenantId, string name, OctoObjectId? rtId = null) =>
        WriteAsync(tenantId, EntityUpdateInfo<RtEntity>.CreateInsert(Contact(rtId ?? OctoObjectId.GenerateNewId(), name)));

    private Task UpdateContactAsync(string tenantId, OctoObjectId rtId, string name) =>
        WriteAsync(tenantId, EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(ContactType, rtId),
            Contact(rtId, name)));

    private async Task EraseContactAsync(string tenantId, OctoObjectId rtId)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.DeleteOneRtEntityByRtIdAsync(session, ContactType, rtId, DeleteOptions.Erase);
        await session.CommitTransactionAsync();
    }

    private Task DeleteContactAsync(string tenantId, OctoObjectId rtId) =>
        WriteAsync(tenantId, EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ContactType, rtId)));

    private async Task<RtEntity?> ContactInAsync(string tenantId, OctoObjectId rtId)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByIdAsync(session, ContactType, [rtId],
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Items.SingleOrDefault();
    }

    private async Task<int> CountByNameAsync(string tenantId, string name)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByTypeAsync(session, ContactType, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Items.Count(e => Equals(Name(e), name));
    }

    private static string? Name(RtEntity? entity) =>
        entity != null && entity.Attributes.TryGetValue("ContactName", out var value) ? value?.ToString() : null;

    private static RtSyncOriginRecord OriginOf(RtEntity entity) =>
        new(Assert.IsType<RtRecord>(entity.Attributes["RtSyncOrigin"]));

    private static string NewName() => $"Contact-{Guid.NewGuid():N}";

    /// <summary>Polls until <paramref name="condition" /> holds in A and in B; returns how long that took.</summary>
    private async Task<TimeSpan> WaitInAandBAsync(Func<RtEntity?, bool> condition, OctoObjectId rtId,
        Stopwatch since)
    {
        while (since.Elapsed < SyncDeadline + TimeSpan.FromSeconds(5))
        {
            if (condition(await ContactInAsync(CompanyA, rtId)) && condition(await ContactInAsync(CompanyB, rtId)))
            {
                return since.Elapsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return TimeSpan.MaxValue;
    }

    // ---- pipelines ------------------------------------------------------------------------------------------

    /// <summary>G's sync pipeline: the watched contact ($.Document of the change event) upserted into the children.</summary>
    private static string SyncPipeline(string targets = "targetTenants:\n      relation: Child",
        string identity = "ServiceAccount", string documentPath = "$.Document") => $"""
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._sync
            targetValueKind: Array
            updateKind: UPDATE
            ckTypeId: {Model}/SyncedContact
            rtIdPath: {documentPath}.RtId
            attributeUpdates:
              - attributeName: ContactName
                attributeValueType: String
                valuePath: {documentPath}.Attributes.ContactName
          - type: ApplyChanges@2
            identity: {identity}
            entityUpdatesPath: $._sync
            {targets}
        """;

    /// <summary>G's sync of a deletion: the deleted contact's RtId ($.DocumentRtId of a delete event).</summary>
    private static string SyncDeletePipeline() => $"""
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._sync
            targetValueKind: Array
            updateKind: DELETE
            ckTypeId: {Model}/SyncedContact
            rtIdPath: $.DocumentRtId
          - type: ApplyChanges@2
            identity: ServiceAccount
            entityUpdatesPath: $._sync
            targetTenants:
              relation: Child
        """;

    /// <summary>
    ///     G's whole sync pipeline behind <c>FromWatchRtEntity@1</c> with <c>includeDeletes</c>. A deletion in G arrives in
    ///     two shapes: the default delete of ApplyChanges and GraphQL archives the entity (an update event whose
    ///     <c>$.Document.RtState</c> is 1, Archived), an erase removes it (a delete event, <c>$.UpdateType</c> 8, no
    ///     document). Both delete the copies; every other event upserts them. Enums are numbers in the data context.
    /// </summary>
    private static string WatchSyncPipeline() => $"""
        transformations:
          - type: If@1
            path: $.UpdateType
            operator: Equals
            valueType: Integer
            value: 8
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._erased
                targetValueKind: Array
                updateKind: DELETE
                ckTypeId: {Model}/SyncedContact
                rtIdPath: $.DocumentRtId
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $._erased
                targetTenants:
                  relation: Child
          - type: If@1
            path: $.Document.RtState
            operator: Equals
            valueType: Integer
            value: 1
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._archived
                targetValueKind: Array
                updateKind: DELETE
                ckTypeId: {Model}/SyncedContact
                rtIdPath: $.DocumentRtId
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $._archived
                targetTenants:
                  relation: Child
          - type: If@1
            path: $.UpdateType
            operator: NotEquals
            valueType: Integer
            value: 8
            transformations:
              - type: If@1
                path: $.Document.RtState
                operator: NotEquals
                valueType: Integer
                value: 1
                transformations:
                  - type: CreateUpdateInfo@1
                    targetPath: $._sync
                    targetValueKind: Array
                    updateKind: UPDATE
                    ckTypeId: {Model}/SyncedContact
                    rtIdPath: $.DocumentRtId
                    attributeUpdates:
                      - attributeName: ContactName
                        attributeValueType: String
                        valuePath: $.Document.Attributes.ContactName
                  - type: ApplyChanges@2
                    identity: ServiceAccount
                    entityUpdatesPath: $._sync
                    targetTenants:
                      relation: Child
        """;

    /// <summary>A pipeline of company A that changes or deletes a contact in its own tenant.</summary>
    private static string LocalChangePipeline(string updateKind, string node = "ApplyChanges@2",
        string attribute = "ContactName") => $"""
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._changes
            targetValueKind: Array
            updateKind: {updateKind}
            ckTypeId: {Model}/SyncedContact
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: {attribute}
                attributeValueType: String
                valuePath: $.value
          - type: {node}
            {(node == "ApplyChanges@2" ? "entityUpdatesPath" : "path")}: $._changes
        """;

    private static JsonObject Event(OctoObjectId rtId, string name) => new()
    {
        ["UpdateType"] = 2,
        ["DocumentRtId"] = rtId.ToString(),
        ["Document"] = new JsonObject
        {
            ["RtId"] = rtId.ToString(), ["Attributes"] = new JsonObject { ["ContactName"] = name }
        }
    };

    /// <summary>The shape of a delete event without pre-image: no document, the id from the document key.</summary>
    private static JsonObject DeleteEvent(OctoObjectId rtId) => new()
    {
        ["UpdateType"] = 8, ["DocumentRtId"] = rtId.ToString(), ["Document"] = null
    };

    private static JsonObject LocalInput(OctoObjectId rtId, string value) => new()
    {
        ["rtId"] = rtId.ToString(), ["value"] = value
    };

    // ---- AC 1: create, change and delete in G reach A and B within 60 s, through the real change stream -------

    [Fact]
    public async Task ContactCreatedChangedAndDeletedInG_ReachesAandB_Within60SecondsEachTime_ThroughTheChangeStream()
    {
        fixture.EnsureInitialized();
        var failures = new List<Exception>();
        var received = new List<string>();
        var definition = await fixture.GetService<IPipelineConfigurationSerializer>()
            .DeserializeAsync(WatchSyncPipeline());
        var triggerContext = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => triggerContext.TenantId).Returns(Group);
        A.CallTo(() => triggerContext.NodeContext).Returns(nodeContext);
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromWatchRtEntityNodeConfiguration>())
            .Returns(new FromWatchRtEntityNodeConfiguration
            {
                CkTypeId = ContactType, UpdateTypes = TriggerUpdateTypes.All, IncludeDeletes = true
            });
        A.CallTo(() => triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .ReturnsLazily(async (ExecutePipelineOptions _, object? input) =>
            {
                lock (received)
                {
                    received.Add(input is IUpdateInfo<RtEntity> u ? $"{u.UpdateType}:{u.DocumentRtId}" : "?");
                }

                try
                {
                    var etlContext = await CreateContextAsync(Group);
                    return await fixture.GetService<IEtlDataOrchestrator>()
                        .ExecutePipelineAsync(definition, etlContext, value: input);
                }
                catch (Exception e)
                {
                    lock (failures)
                    {
                        failures.Add(e);
                    }

                    throw;
                }
            });

        var trigger = new FromWatchRtEntityNode(fixture.GetSystemContext(), fixture.GetService<ICkCacheService>());
        await trigger.StartAsync(triggerContext);
        try
        {
            var name = NewName();
            var clock = Stopwatch.StartNew();
            var rtId = await CreateContactAsync(Group, name);
            var created = await WaitInAandBAsync(e => Name(e) == name, rtId, clock);
            Assert.True(created <= SyncDeadline,
                $"create not in CompanyA and CompanyB within 60 s ({created}); failures: {string.Join(" | ", failures.Select(f => f.Message))}");

            var copyInA = (await ContactInAsync(CompanyA, rtId))!;
            Assert.Equal(Group, OriginOf(copyInA).SourceTenantId);
            Assert.Equal(rtId.ToString(), OriginOf(copyInA).SourceRtId);
            Assert.True(OriginOf(copyInA).SyncedAt > DateTime.UtcNow.AddMinutes(-5));

            var changed = NewName();
            clock.Restart();
            await UpdateContactAsync(Group, rtId, changed);
            var updated = await WaitInAandBAsync(e => Name(e) == changed, rtId, clock);
            Assert.True(updated <= SyncDeadline, $"change not in CompanyA and CompanyB within 60 s ({updated})");

            // The default delete archives the entity in G (an update event): the copies are deleted (archived) too.
            clock.Restart();
            await DeleteContactAsync(Group, rtId);
            var archived = await WaitInAandBAsync(e => e == null, rtId, clock);
            Assert.True(archived <= SyncDeadline,
                $"archive not in CompanyA and CompanyB within 60 s ({archived}); events: {string.Join(", ", received)}; " +
                $"failures: {string.Join(" | ", failures.Select(f => f.Message))}");

            // An erase removes the entity (a delete event without document): the copies go as well.
            var erasedName = NewName();
            var erasedId = await CreateContactAsync(Group, erasedName);
            clock.Restart();
            Assert.True(await WaitInAandBAsync(e => Name(e) == erasedName, erasedId, clock) <= SyncDeadline);
            await EraseContactAsync(Group, erasedId);
            clock.Restart();
            var deleted = await WaitInAandBAsync(e => e == null, erasedId, clock);
            Assert.True(deleted <= SyncDeadline, $"delete not in CompanyA and CompanyB within 60 s ({deleted}); events: {string.Join(", ", received)}; " +
                $"failures: {string.Join(" | ", failures.Select(f => f.Message))}");
            Assert.Empty(failures);
        }
        finally
        {
            await trigger.StopAsync(triggerContext);
        }
    }

    // ---- AC 1 (delete): a deletion in G removes the synced copies, and only them --------------------------------

    [Fact]
    public async Task ContactDeletedInG_RemovesTheCopiesInAandB_AndRepeatingIsANoOp()
    {
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);
        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        Assert.NotNull(await ContactInAsync(CompanyA, rtId));
        Assert.NotNull(await ContactInAsync(CompanyB, rtId));
        var localName = NewName();
        var local = await CreateContactAsync(CompanyA, localName);

        await DeleteContactAsync(Group, rtId);
        await RunAsync(Group, SyncDeletePipeline(), DeleteEvent(rtId));
        await RunAsync(Group, SyncDeletePipeline(), DeleteEvent(rtId));

        Assert.Null(await ContactInAsync(CompanyA, rtId));
        Assert.Null(await ContactInAsync(CompanyB, rtId));
        Assert.Equal(localName, Name(await ContactInAsync(CompanyA, local)));
    }

    // ---- AC 2: the sync running twice leaves exactly one copy -----------------------------------------------------

    [Fact]
    public async Task SyncRunningTwiceForTheSameChange_LeavesExactlyOneCopyInAandB()
    {
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);

        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        await RunAsync(Group, SyncPipeline(), Event(rtId, name));

        Assert.Equal(1, await CountByNameAsync(CompanyA, name));
        Assert.Equal(1, await CountByNameAsync(CompanyB, name));
        Assert.Equal(rtId, (await ContactInAsync(CompanyA, rtId))!.RtId);
    }

    // ---- AC 3: the copy carries its owner; local changes in A are refused --------------------------------------

    [Theory]
    [InlineData("UPDATE", "ApplyChanges@2")]
    [InlineData("DELETE", "ApplyChanges@2")]
    [InlineData("UPDATE", "ApplyChanges@1")]
    [InlineData("DELETE", "ApplyChanges@1")]
    public async Task SyncedContactInA_CarriesItsOwner_AndALocalChangeInA_IsRefused(string updateKind, string node)
    {
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);
        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        var copy = (await ContactInAsync(CompanyA, rtId))!;
        Assert.Equal(Group, OriginOf(copy).SourceTenantId);
        Assert.Equal(rtId.ToString(), OriginOf(copy).SourceRtId);

        var refused = await FailsWithAsync<SyncOriginWriteRefusedException>(CompanyA,
            LocalChangePipeline(updateKind, node), LocalInput(rtId, "edited in CompanyA"));

        Assert.Equal(SyncOriginCodes.SyncOwnedEntity, refused.ErrorCode);
        Assert.Equal(name, Name(await ContactInAsync(CompanyA, rtId)));
    }

    [Fact]
    public async Task TheSyncChangesItsOwnCopies_ButAPlainTargetTenantWriteOfGroupIsRefused()
    {
        // Both directions of the rule: the sync path of the source tenant updates and deletes its copies (other
        // tests), every other write - also one of the source tenant with a single targetTenant - is refused.
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);
        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        var changed = NewName();
        await RunAsync(Group, SyncPipeline(), Event(rtId, changed));
        Assert.Equal(changed, Name(await ContactInAsync(CompanyA, rtId)));

        var plainWrite = $"""
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._changes
                targetValueKind: Array
                updateKind: UPDATE
                ckTypeId: {Model}/SyncedContact
                rtIdPath: $.rtId
                attributeUpdates:
                  - attributeName: ContactName
                    attributeValueType: String
                    valuePath: $.value
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $._changes
                targetTenant:
                  relation: Child
                  tenantId: {CompanyA}
            """;
        var refused = await FailsWithAsync<SyncOriginWriteRefusedException>(Group, plainWrite,
            LocalInput(rtId, "not through the sync"));

        Assert.Equal(SyncOriginCodes.SyncOwnedEntity, refused.ErrorCode);
        Assert.Equal(changed, Name(await ContactInAsync(CompanyA, rtId)));
    }

    // ---- AC 4: B refuses (grant removed): execution failed, B named, A updated -------------------------------

    [Fact]
    public async Task GrantRemovedInB_FailsTheExecution_NamesB_AndAIsStillUpdated()
    {
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);
        _grants.Remove(CompanyB);

        var failed = await FailsWithAsync<CrossTenantWriteFailedException>(Group, SyncPipeline(), Event(rtId, name));

        Assert.Equal(CrossTenantWriteCodes.WriteFailed, failed.ErrorCode);
        var failure = Assert.Single(failed.Failures);
        Assert.Equal(CompanyB, failure.TenantId);
        Assert.Equal(CrossTenantAccessCodes.Forbidden, failure.ErrorCode);
        Assert.Contains($"'{CompanyB}'", failed.Message);
        Assert.Equal(new[] { CompanyA }, failed.WrittenTenantIds);
        Assert.Equal(name, Name(await ContactInAsync(CompanyA, rtId)));
        Assert.Null(await ContactInAsync(CompanyB, rtId));
    }

    // ---- AC 5: a contact created locally in A is never touched -----------------------------------------------

    [Fact]
    public async Task ContactCreatedLocallyInA_IsNeverTouchedByTheSync()
    {
        var localName = NewName();
        var local = await CreateContactAsync(CompanyA, localName);
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);

        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        await DeleteContactAsync(Group, rtId);
        await RunAsync(Group, SyncDeletePipeline(), DeleteEvent(rtId));

        var untouched = (await ContactInAsync(CompanyA, local))!;
        Assert.Equal(localName, Name(untouched));
        Assert.False(untouched.Attributes.ContainsKey("RtSyncOrigin"));
    }

    [Fact]
    public async Task LocalEntityInAWithTheSourceRtId_IsNotOverwrittenNorDeleted_AndATargetFails()
    {
        // A forged collision: A holds an entity under the RtId of G's contact, without an origin.
        var rtId = OctoObjectId.GenerateNewId();
        var localName = NewName();
        await CreateContactAsync(CompanyA, localName, rtId);
        var name = NewName();
        await CreateContactAsync(Group, name, rtId);

        var failed = await FailsWithAsync<CrossTenantWriteFailedException>(Group, SyncPipeline(), Event(rtId, name));
        Assert.Equal(CompanyA, Assert.Single(failed.Failures).TenantId);
        Assert.Equal(SyncOriginCodes.SyncOwnedEntity, failed.Failures[0].ErrorCode);
        Assert.Equal(name, Name(await ContactInAsync(CompanyB, rtId)));

        await FailsWithAsync<CrossTenantWriteFailedException>(Group, SyncDeletePipeline(),
            DeleteEvent(rtId));
        Assert.Equal(localName, Name(await ContactInAsync(CompanyA, rtId)));
    }

    // ---- abuse: forged origins, other targets, System ---------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APipelineOfA_SettingRtSyncOriginItself_IsRefused(bool insert)
    {
        // A forges the group's origin on a new entity, or pins one of its own entities with it. The entity updates come
        // from the data context as the pipeline built them (with the origin record), so the write node sees exactly that.
        var local = await CreateContactAsync(CompanyA, NewName());
        var rtId = insert ? OctoObjectId.GenerateNewId() : local;
        var forged = Contact(rtId, "forged");
        forged.SetAttributeRawValue("RtSyncOrigin", new RtSyncOriginRecord
        {
            SourceTenantId = Group, SourceRtId = rtId.ToString(), SyncedAt = DateTime.UtcNow
        });
        var update = insert
            ? EntityUpdateInfo<RtEntity>.CreateInsert(forged)
            : EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(ContactType, rtId), forged);
        const string yaml = """
            transformations:
              - type: ApplyChanges@2
                entityUpdatesPath: $.updates
            """;

        var refused = await FailsWithAsync<SyncOriginWriteRefusedException>(CompanyA, yaml,
            new { updates = new[] { update } });

        Assert.Equal(SyncOriginCodes.SyncOwnedEntity, refused.ErrorCode);
        Assert.False((await ContactInAsync(CompanyA, local))!.Attributes.ContainsKey("RtSyncOrigin"));
        Assert.Null(insert ? await ContactInAsync(CompanyA, rtId) : null);
    }

    [Fact]
    public async Task TheSyncOfA_IntoItsChild_StampsAAsTheSource()
    {
        // A syncs G's copy on into its own child: the copy there names A, whatever origin the source carried.
        _grants.Add(TenantSubtreeFixture.GrandChild);
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);
        await RunAsync(Group, SyncPipeline(), Event(rtId, name));
        var copyInA = (await ContactInAsync(CompanyA, rtId))!;

        await RunAsync(CompanyA, SyncPipeline(), new JsonObject
        {
            ["Document"] = new JsonObject
            {
                ["RtId"] = rtId.ToString(), ["Attributes"] = new JsonObject { ["ContactName"] = Name(copyInA) }
            }
        });

        var inGrandChild = (await ContactInAsync(TenantSubtreeFixture.GrandChild, rtId))!;
        Assert.Equal(CompanyA, OriginOf(inGrandChild).SourceTenantId);
    }

    [Fact]
    public async Task ASelectedTargetList_WritesOnlyTheListedChildren_AndRefusesAGrandChild()
    {
        _grants.Add(TenantSubtreeFixture.GrandChild);
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);

        await RunAsync(Group, SyncPipeline($"targetTenants:\n      relation: Child\n      tenantIds: [{CompanyA}]"),
            Event(rtId, name));
        Assert.NotNull(await ContactInAsync(CompanyA, rtId));
        Assert.Null(await ContactInAsync(CompanyB, rtId));

        var failed = await FailsWithAsync<CrossTenantWriteFailedException>(Group,
            SyncPipeline($"targetTenants:\n      relation: Child\n      tenantIds: [{TenantSubtreeFixture.GrandChild}, {TenantSubtreeFixture.Detached}]"),
            Event(rtId, name));
        Assert.Equal(new[] { TenantSubtreeFixture.GrandChild, TenantSubtreeFixture.Detached },
            failed.Failures.Select(f => f.TenantId));
        Assert.All(failed.Failures, f => Assert.Equal(CrossTenantAccessCodes.Forbidden, f.ErrorCode));
        Assert.Null(await ContactInAsync(TenantSubtreeFixture.GrandChild, rtId));
        // The detached tenant is no longer in the registry, so the system context cannot even open it here.
    }

    [Fact]
    public async Task SystemIdentity_IsRefusedForEveryTarget_AndNothingIsWritten()
    {
        var name = NewName();
        var rtId = await CreateContactAsync(Group, name);

        var failed = await FailsWithAsync<CrossTenantWriteFailedException>(Group, SyncPipeline(identity: "System"),
            Event(rtId, name));

        Assert.Equal(new[] { CompanyA, CompanyB }, failed.Failures.Select(f => f.TenantId).Order());
        Assert.Empty(failed.WrittenTenantIds);
        Assert.Null(await ContactInAsync(CompanyA, rtId));
        Assert.Null(await ContactInAsync(CompanyB, rtId));
    }
}
