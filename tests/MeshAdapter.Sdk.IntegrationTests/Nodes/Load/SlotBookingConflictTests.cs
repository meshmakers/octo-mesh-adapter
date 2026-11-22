using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     AB#9466 (spike AB#9465): a booking pipeline refuses a double booking with a unique index and
///     <c>ApplyChanges@2</c> <c>onDuplicateKey: Report</c>, also when the competing writer is a second adapter
///     replica. Real orchestrator, real nodes and the real engine on MongoDB (Testcontainers); type
///     <c>MeshAdapterIntegrationTest/SlotBooking</c> carries a <c>UniqueNotDeleted</c> index on <c>SlotKey</c>.
///     <para>
///         A second replica does not share the node's process-wide semaphore. It is simulated by an engine session
///         whose transaction holds an uncommitted insert of the same slot while the pipeline runs: the node then meets a
///         WriteConflict (112) wrapped by the repository. Before AB#9466 the node recognised 112 only unwrapped, so
///         the pipeline failed (the caller saw METHOD_HANDLER_FAILED instead of the declared CONCURRENT_CHANGE).
///     </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class SlotBookingConflictTests(PipelineExecutionFixture fixture, ITestOutputHelper output)
    : IClassFixture<PipelineExecutionFixture>
{
    private const string BookingType = "MeshAdapterIntegrationTest/SlotBooking";

    /// <summary>The sample's type, replaced by the integration test model's type when the sample runs here.</summary>
    private const string SampleBookingType = "Acme.Scheduling/SlotBooking";

    private const string ConcurrentChange = "CONCURRENT_CHANGE";

    private const string MovePipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._updates
            targetValueKind: Array
            updateKind: UPDATE
            ckTypeId: MeshAdapterIntegrationTest/SlotBooking
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: SlotKey
                attributeValueType: String
                valuePath: $.slotKey
          - type: ApplyChanges@2
            entityUpdatesPath: $._updates
            onDuplicateKey: Report
            duplicateKeyTargetPath: $.slotTaken
        """;

    private const string ReadPipeline = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/SlotBooking
            targetPath: $.bookings
            fieldFilters:
              - attributePath: SlotKey
                operator: Equals
                comparisonValuePath: $.slotKey
        """;

    /// <summary>
    ///     N = 12 parallel calls of the sample booking method book the same slot: exactly one is booked, the other 11
    ///     answer the declared method error CONCURRENT_CHANGE, and one entity is stored.
    /// </summary>
    [Fact]
    public async Task ParallelBookingsOfOneSlot_OneIsBooked_ElevenAnswerConcurrentChange()
    {
        fixture.EnsureInitialized();
        var slot = NewSlot("res-1");
        const int n = 12;

        var results = await Task.WhenAll(Enumerable.Range(0, n)
            .Select(i => Task.Run(() => RunSampleAsync(slot, $"caller-{i}"))));

        output.WriteLine($"results: {string.Join(" ", results.Select(r => r["result"]?.ToJsonString()))}");
        results.Count(IsBooked).Should().Be(1);
        results.Count(IsConcurrentChange).Should().Be(n - 1);
        (await CountAsync(slot.Key)).Should().Be(1);
    }

    /// <summary>
    ///     Ordi's case (AB#9457): 10 different appointments are moved in parallel into the same free slot (UPDATE of
    ///     SlotKey). Exactly one move succeeds, 9 report the conflict.
    /// </summary>
    [Fact]
    public async Task ParallelMovesOfDifferentAppointmentsIntoOneSlot_OneWins_NineReportTheConflict()
    {
        fixture.EnsureInitialized();
        var run = Guid.NewGuid().ToString("N");
        const int n = 10;
        var rtIds = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var booked = await RunSampleAsync(new Slot($"res-4-{run}", $"from-{i}"), $"caller-{i}");
            IsBooked(booked).Should().BeTrue();
            rtIds.Add(await SingleRtIdAsync($"res-4-{run}|from-{i}"));
        }

        var target = $"res-4-{run}|2026-12-01T12:00:00Z";
        var results = await Task.WhenAll(rtIds.Select(id => Task.Run(() => RunAsync(MovePipeline,
            new JsonObject { ["rtId"] = id, ["slotKey"] = target }))));

        var taken = results.Select(r => r["slotTaken"]?.GetValue<bool>()).ToList();
        output.WriteLine($"move slotTaken values: {string.Join(",", taken)}");
        taken.Count(t => t == false).Should().Be(1);
        taken.Count(t => t == true).Should().Be(n - 1);
        (await CountAsync(target)).Should().Be(1);
    }

    /// <summary>
    ///     Two replicas: another replica's transaction holds the slot uncommitted and commits while the node is
    ///     retrying the WriteConflict. The retry meets the unique index; the method answers CONCURRENT_CHANGE and the
    ///     pipeline does not fail. Red without AB#9466 (the wrapped 112 failed the pipeline).
    /// </summary>
    [Fact]
    public async Task SecondReplica_CommitsDuringTheRetries_TheMethodAnswersConcurrentChange()
    {
        fixture.EnsureInitialized();
        var (repository, cache) = await RepositoryAsync();
        var slot = NewSlot("res-3");

        using var replicaA = await repository.GetSessionAsync(RtSecurityContext.System);
        replicaA.StartTransaction();
        await repository.ApplyChangesAsync(replicaA, [Insert(cache, repository.TenantId, slot.Key, "A")],
            new OperationResult());

        var booking = Task.Run(() => RunSampleAsync(slot, "B"));
        await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
        await replicaA.CommitTransactionAsync();

        var result = await booking;

        output.WriteLine($"result: {result["result"]?.ToJsonString()}");
        IsConcurrentChange(result).Should().BeTrue();
        (await CountAsync(slot.Key)).Should().Be(1, "never a double booking");
    }

    /// <summary>
    ///     Two replicas: the other replica keeps the slot uncommitted for longer than the node's whole retry window.
    ///     The node gives up and, with Report configured, reports a conflict (flag true) - never a silent "booked",
    ///     although nothing was written. Red without AB#9466.
    /// </summary>
    [Fact]
    public async Task SecondReplica_HoldsTheSlotPastAllRetries_TheMethodAnswersConcurrentChange_NeverBooked()
    {
        fixture.EnsureInitialized();
        var (repository, cache) = await RepositoryAsync();
        var slot = NewSlot("res-5");

        using var replicaA = await repository.GetSessionAsync(RtSecurityContext.System);
        replicaA.StartTransaction();
        await repository.ApplyChangesAsync(replicaA, [Insert(cache, repository.TenantId, slot.Key, "A")],
            new OperationResult());

        JsonObject result;
        try
        {
            result = await RunSampleAsync(slot, "B");
        }
        finally
        {
            await replicaA.CommitTransactionAsync();
        }

        output.WriteLine($"result: {result["result"]?.ToJsonString()}");
        result["slotTaken"]!.GetValue<bool>().Should().BeTrue();
        IsConcurrentChange(result).Should().BeTrue();
        (await CountAsync(slot.Key)).Should().Be(1, "only replica A's booking is stored");
    }

    /// <summary>
    ///     Two replicas: the other replica rolls its booking back while the node retries. The retry then stores the
    ///     booking - the node retries a conflict, it does not just turn every conflict into a refusal.
    /// </summary>
    [Fact]
    public async Task SecondReplica_RollsBackDuringTheRetries_TheRetryBooksTheSlot()
    {
        fixture.EnsureInitialized();
        var (repository, cache) = await RepositoryAsync();
        var slot = NewSlot("res-6");

        using var replicaA = await repository.GetSessionAsync(RtSecurityContext.System);
        replicaA.StartTransaction();
        await repository.ApplyChangesAsync(replicaA, [Insert(cache, repository.TenantId, slot.Key, "A")],
            new OperationResult());

        var booking = Task.Run(() => RunSampleAsync(slot, "B"));
        await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
        await replicaA.AbortTransactionAsync();

        var result = await booking;

        output.WriteLine($"result: {result["result"]?.ToJsonString()}");
        IsBooked(result).Should().BeTrue();
        (await CountAsync(slot.Key)).Should().Be(1);
    }

    /// <summary>
    ///     Without onDuplicateKey: Report the node fails as before when the conflict outlasts the retries: nothing was
    ///     written, so the pipeline must not carry on as if it had been.
    /// </summary>
    [Fact]
    public async Task WithoutReport_SecondReplicaHoldsTheSlotPastAllRetries_ThePipelineFails()
    {
        fixture.EnsureInitialized();
        var (repository, cache) = await RepositoryAsync();
        var slot = NewSlot("res-7");
        var throwingPipeline = SampleTransformations()
            .Replace("    onDuplicateKey: Report\n", string.Empty)
            .Replace("    duplicateKeyTargetPath: $.slotTaken\n", string.Empty);
        throwingPipeline.Should().NotContain("onDuplicateKey");

        using var replicaA = await repository.GetSessionAsync(RtSecurityContext.System);
        replicaA.StartTransaction();
        await repository.ApplyChangesAsync(replicaA, [Insert(cache, repository.TenantId, slot.Key, "A")],
            new OperationResult());

        Exception? failure = null;
        try
        {
            await RunAsync(throwingPipeline, SampleInput(slot, "B"));
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            await replicaA.CommitTransactionAsync();
        }

        output.WriteLine($"failure: {failure?.GetType().Name}");
        failure.Should().NotBeNull();
        (await CountAsync(slot.Key)).Should().Be(1);
    }

    private sealed record Slot(string ResourceRtId, string SlotStartUtc)
    {
        public string Key => $"{ResourceRtId}|{SlotStartUtc}";
    }

    private static Slot NewSlot(string resource) => new($"{resource}-{Guid.NewGuid():N}", "2026-12-01T09:00:00Z");

    private static bool IsBooked(JsonObject result) =>
        result["result"]?["value"]?.GetValue<bool>() == true && result["result"]?["error"] == null;

    private static bool IsConcurrentChange(JsonObject result) =>
        result["result"]?["error"]?["code"]?.GetValue<string>() == ConcurrentChange &&
        result["result"]?["value"] == null;

    /// <summary>
    ///     The transformations of the documented sample (docs/examples/slot-booking-method.yaml) against the integration
    ///     test model. The trigger is left out: the test hands the method input to the orchestrator directly.
    /// </summary>
    private static string SampleTransformations()
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "slot-booking-method.yaml"))
            .ReplaceLineEndings("\n");
        var start = sample.IndexOf("transformations:", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "the sample has a transformations section");
        sample.Should().Contain(SampleBookingType);
        return sample[start..].Replace(SampleBookingType, BookingType);
    }

    private static JsonObject SampleInput(Slot slot, string bookedBy) => new()
    {
        ["parameters"] = new JsonObject
        {
            ["resourceRtId"] = slot.ResourceRtId,
            ["slotStartUtc"] = slot.SlotStartUtc,
            ["bookedBy"] = bookedBy
        }
    };

    private Task<JsonObject> RunSampleAsync(Slot slot, string bookedBy) =>
        RunAsync(SampleTransformations(), SampleInput(slot, bookedBy));

    private async Task<(ITenantRepository Repository, ICkCacheService Cache)> RepositoryAsync()
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var cache = fixture.GetService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(cache);
        return (repository, cache);
    }

    private static IEntityUpdateInfo<RtEntity> Insert(ICkCacheService cache, string tenantId, string slotKey, string by)
    {
        var entity = new RtEntity(new RtCkId<CkTypeId>(BookingType), OctoObjectId.GenerateNewId());
        entity.SetAttributeValueByAccessPath(cache, tenantId, "SlotKey", slotKey);
        entity.SetAttributeValueByAccessPath(cache, tenantId, "BookedBy", by);
        return EntityUpdateInfo<RtEntity>.CreateInsert(entity);
    }

    private async Task<int> CountAsync(string slotKey)
    {
        var result = await RunAsync(ReadPipeline, new JsonObject { ["slotKey"] = slotKey });
        return ((JsonArray)result["bookings"]!["Items"]!).Count;
    }

    private async Task<string> SingleRtIdAsync(string slotKey)
    {
        var result = await RunAsync(ReadPipeline, new JsonObject { ["slotKey"] = slotKey });
        var items = (JsonArray)result["bookings"]!["Items"]!;
        items.Should().HaveCount(1);
        return items[0]!["RtId"]!.ToString();
    }

    private async Task<JsonObject> RunAsync(string yaml, JsonObject input)
    {
        var (repository, _) = await RepositoryAsync();
        var pipelineId = new OctoObjectId("000000000000000000000094");

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, pipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", pipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var definition = await fixture.GetService<IPipelineConfigurationSerializer>().DeserializeAsync(yaml);
        var result = await fixture.GetService<IEtlDataOrchestrator>().ExecutePipelineAsync(definition, etlContext,
            value: input);
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }
}
