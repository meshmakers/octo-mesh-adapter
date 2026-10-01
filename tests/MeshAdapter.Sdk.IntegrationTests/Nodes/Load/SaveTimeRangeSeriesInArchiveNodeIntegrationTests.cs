using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Npgsql;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
/// Runs <see cref="SaveTimeRangeSeriesInArchiveNode" /> end to end against the real MongoDB + CrateDB
/// of <see cref="StreamDataFixture" />: anchors are resolved and persisted in the runtime store, the
/// windowed rows land in a <c>TimeRangeArchive</c> table and are read back with plain SQL.
/// </summary>
/// <remarks>
/// The second half covers the archive's opt-in <c>ConflictPrecedence</c> (System.StreamData 1.13.0)
/// through this node, the writer it was designed for. The engine's guard is otherwise only covered
/// by SQL-shape unit tests; here two competing deliveries for the same windows really hit CrateDB, in
/// both arrival orders. Every test provisions its own archive and uses its own anchor keys, so the
/// tests are independent of each other and of the fixture's seeded archives.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class SaveTimeRangeSeriesInArchiveNodeIntegrationTests(StreamDataFixture fixture)
    : IClassFixture<StreamDataFixture>
{
    private static readonly DateTime WindowStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    /// <summary>Older document date of a competing delivery.</summary>
    private static readonly DateTime OlderDocument = new(2026, 7, 2, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Newer document date of a competing delivery.</summary>
    private static readonly DateTime NewerDocument = new(2026, 7, 3, 8, 0, 0, DateTimeKind.Utc);

    private const int Measured = 1;
    private const int Estimated = 3;

    [Fact]
    public async Task ProcessObjectAsync_WritesOneAnchorPerSeriesAndOneRowPerWindow()
    {
        // Arrange
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_EndToEnd", withPrecedence: false);
        var keyA = UniqueKey("E2E-A");
        var keyB = UniqueKey("E2E-B");
        var seriesA = Series(keyA, Enumerable.Range(0, 4).Select(slot => Value(slot, 10.0 + slot, Measured, OlderDocument)));
        var seriesB = Series(keyB, Enumerable.Range(0, 3).Select(slot => Value(slot, 50.0 + slot, Estimated, NewerDocument)));

        // Act
        await ExecuteNodeAsync(archiveRtId, seriesA, seriesB);

        // Assert — one anchor per series, and every row points at it
        var anchorA = await GetSingleAnchorAsync(keyA);
        var anchorB = await GetSingleAnchorAsync(keyB);

        var rowsA = await ReadRowsAsync(archiveRtId, anchorA.RtId);
        rowsA.Should().HaveCount(4);
        for (var slot = 0; slot < 4; slot++)
        {
            rowsA[slot].WindowStart.Should().Be(WindowStart.Add(Period * slot));
            rowsA[slot].WindowEnd.Should().Be(WindowStart.Add(Period * (slot + 1)));
            rowsA[slot].Temperature.Should().Be(10.0 + slot);
            rowsA[slot].Quality.Should().Be(Measured);
            rowsA[slot].SourceDate.Should().Be(OlderDocument);
            // Series-scoped column: constant for the whole series
            rowsA[slot].SerialNumber.Should().Be($"SN-{keyA}");
        }

        var rowsB = await ReadRowsAsync(archiveRtId, anchorB.RtId);
        rowsB.Should().HaveCount(3);
        rowsB.Select(r => r.Temperature).Should().Equal(50.0, 51.0, 52.0);

        (await CountRowsAsync(archiveRtId)).Should().Be(7, "no row may reference anything but the two anchors");
    }

    [Fact]
    public async Task ProcessObjectAsync_RunTwice_IsIdempotent()
    {
        // Arrange
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_Idempotent", withPrecedence: false);
        var key = UniqueKey("IDEMPOTENT");
        var series = Series(key, Enumerable.Range(0, 5).Select(slot => Value(slot, 20.0 + slot, Measured, OlderDocument)));

        await ExecuteNodeAsync(archiveRtId, series);
        var anchorAfterFirstRun = await GetSingleAnchorAsync(key);
        var rowsAfterFirstRun = await ReadRowsAsync(archiveRtId, anchorAfterFirstRun.RtId);

        // Act — the very same delivery again
        await ExecuteNodeAsync(archiveRtId, series);

        // Assert — the anchor is reused, not duplicated, and the rows upsert onto themselves
        var anchorAfterSecondRun = await GetSingleAnchorAsync(key);
        anchorAfterSecondRun.RtId.Should().Be(anchorAfterFirstRun.RtId);

        var rowsAfterSecondRun = await ReadRowsAsync(archiveRtId, anchorAfterSecondRun.RtId);
        rowsAfterSecondRun.Should().HaveCount(5);
        rowsAfterSecondRun.Should().Equal(rowsAfterFirstRun);
        (await CountRowsAsync(archiveRtId)).Should().Be(5);
    }

    [Fact]
    public async Task ConflictPrecedence_BetterQualityIsNotDisplacedByLaterWorseQuality_InEitherOrder()
    {
        // Arrange — the measured value carries the OLDER document date; the estimate arriving with a
        // newer date must still lose, because quality is the first (dominant) key.
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_QualityWins", withPrecedence: true);
        Func<int, JsonObject> measured = slot => Value(slot, 10.0 + slot, Measured, OlderDocument);
        Func<int, JsonObject> estimate = slot => Value(slot, 90.0 + slot, Estimated, NewerDocument);

        // Act
        var (measuredFirst, estimateFirst) =
            await WriteInBothOrdersAsync(archiveRtId, "QUALITY", measured, estimate);

        // Assert — the measured value survives, whichever delivery was written first
        foreach (var rows in new[] { measuredFirst, estimateFirst })
        {
            rows.Should().HaveCount(3);
            rows.Select(r => r.Temperature).Should().Equal(10.0, 11.0, 12.0);
            rows.Should().OnlyContain(r => r.Quality == Measured && r.SourceDate == OlderDocument);
        }

        measuredFirst.Select(Payload).Should().Equal(estimateFirst.Select(Payload));
    }

    [Fact]
    public async Task ConflictPrecedence_EqualQuality_NewerDocumentWins_InEitherOrder()
    {
        // Arrange — same quality, so the second key (document date, higher wins) decides
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_NewerWins", withPrecedence: true);
        Func<int, JsonObject> older = slot => Value(slot, 10.0 + slot, Measured, OlderDocument);
        Func<int, JsonObject> newer = slot => Value(slot, 20.0 + slot, Measured, NewerDocument);

        // Act
        var (olderFirst, newerFirst) = await WriteInBothOrdersAsync(archiveRtId, "RECENCY", older, newer);

        // Assert — the newer correction survives, whichever delivery was written first
        foreach (var rows in new[] { olderFirst, newerFirst })
        {
            rows.Should().HaveCount(3);
            rows.Select(r => r.Temperature).Should().Equal(20.0, 21.0, 22.0);
            rows.Should().OnlyContain(r => r.Quality == Measured && r.SourceDate == NewerDocument);
        }

        olderFirst.Select(Payload).Should().Equal(newerFirst.Select(Payload));
    }

    [Fact]
    public async Task WithoutConflictPrecedence_LastWriteWins()
    {
        // Arrange — regression guard: an archive that does not opt in keeps the unconditional upsert,
        // so a later estimate with an older document date still overwrites a measured value.
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_LastWriteWins", withPrecedence: false);
        var key = UniqueKey("LWW");

        // Act
        await ExecuteNodeAsync(archiveRtId,
            Series(key, Enumerable.Range(0, 3).Select(slot => Value(slot, 10.0 + slot, Measured, NewerDocument))));
        await ExecuteNodeAsync(archiveRtId,
            Series(key, Enumerable.Range(0, 3).Select(slot => Value(slot, 90.0 + slot, Estimated, OlderDocument))));

        // Assert
        var anchor = await GetSingleAnchorAsync(key);
        var rows = await ReadRowsAsync(archiveRtId, anchor.RtId);
        rows.Should().HaveCount(3);
        rows.Select(r => r.Temperature).Should().Equal(90.0, 91.0, 92.0);
        rows.Should().OnlyContain(r => r.Quality == Estimated && r.SourceDate == OlderDocument);
    }

    [Fact]
    public async Task SameWindowTwiceInOneBatch_WithoutConflictPrecedence_TheLaterRowWins()
    {
        // Arrange — archive writes go out as one multi-row statement, so two values for the same
        // window land in the SAME statement. CrateDB applies the rows in order; the result has to be
        // what two separate writes would have left: the later one.
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_DuplicateInBatch", withPrecedence: false);
        var key = UniqueKey("DUP-LWW");

        // Act
        await ExecuteNodeAsync(archiveRtId, Series(key, new[]
        {
            Value(0, 10.0, Measured, NewerDocument),
            Value(0, 90.0, Estimated, OlderDocument),
            Value(1, 11.0, Measured, NewerDocument)
        }));

        // Assert
        var anchor = await GetSingleAnchorAsync(key);
        var rows = await ReadRowsAsync(archiveRtId, anchor.RtId);
        rows.Should().HaveCount(2);
        rows.Select(r => r.Temperature).Should().Equal(90.0, 11.0);
        rows[0].Quality.Should().Be(Estimated);
        rows[0].SourceDate.Should().Be(OlderDocument);
    }

    [Fact]
    public async Task SameWindowTwiceInOneBatch_ConflictPrecedenceDecides_InEitherOrder()
    {
        // Arrange — the guard compares each row with the stored one. Inside one statement the
        // "stored" row for the second occurrence is the first occurrence, so the precedence has to
        // hold within a batch exactly as it does across batches.
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_DuplicateInBatchGuarded", withPrecedence: true);
        var keyWorseFirst = UniqueKey("DUP-GUARD-12");
        var keyBetterFirst = UniqueKey("DUP-GUARD-21");
        var keySameQuality = UniqueKey("DUP-GUARD-DATE");

        // Act
        await ExecuteNodeAsync(archiveRtId,
            Series(keyWorseFirst, new[]
            {
                Value(0, 90.0, Estimated, NewerDocument),
                Value(0, 10.0, Measured, OlderDocument)
            }),
            Series(keyBetterFirst, new[]
            {
                Value(0, 10.0, Measured, OlderDocument),
                Value(0, 90.0, Estimated, NewerDocument)
            }),
            Series(keySameQuality, new[]
            {
                Value(0, 20.0, Measured, NewerDocument),
                Value(0, 10.0, Measured, OlderDocument)
            }));

        // Assert — quality decides first, whichever occurrence comes first in the batch
        foreach (var key in new[] { keyWorseFirst, keyBetterFirst })
        {
            var anchor = await GetSingleAnchorAsync(key);
            var rows = await ReadRowsAsync(archiveRtId, anchor.RtId);
            rows.Should().ContainSingle();
            rows[0].Temperature.Should().Be(10.0);
            rows[0].Quality.Should().Be(Measured);
            rows[0].SourceDate.Should().Be(OlderDocument);
        }

        // ... and the newer document on equal quality, although it comes first in the batch
        var sameQualityAnchor = await GetSingleAnchorAsync(keySameQuality);
        var sameQualityRows = await ReadRowsAsync(archiveRtId, sameQualityAnchor.RtId);
        sameQualityRows.Should().ContainSingle();
        sameQualityRows[0].Temperature.Should().Be(20.0);
        sameQualityRows[0].SourceDate.Should().Be(NewerDocument);
    }

    [Fact]
    public async Task UnknownArchive_ThrowsBeforeAnyAnchorIsWritten()
    {
        // Arrange — the anchors are committed in their own transaction, so everything that can make
        // the archive write fail has to be refused before them.
        fixture.EnsureInitialized();
        var key = UniqueKey("NO-ARCHIVE");

        // Act
        var act = () => ExecuteNodeAsync(OctoObjectId.GenerateNewId(),
            Series(key, new[] { Value(0, 10.0, Measured, OlderDocument) }));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not exist*");
        (await CountAnchorsAsync(key)).Should().Be(0);
    }

    [Fact]
    public async Task ASeriesWithoutAUsableWindow_GetsNoAnchor_AndAMalformedValueDoesNotShapeOne()
    {
        // Arrange — a value whose window ends before it starts yields no archive row, so it must not
        // create an anchor either, nor be the value an anchor reflects.
        fixture.EnsureInitialized();
        var archiveRtId = await CreateAndActivateSeriesArchiveAsync("SeriesArchive_MalformedWindows", withPrecedence: false);
        var keyOnlyMalformed = UniqueKey("MALFORMED-ONLY");
        var keyMixed = UniqueKey("MALFORMED-MIXED");

        // Act
        await ExecuteNodeAsync(archiveRtId,
            Series(keyOnlyMalformed, new[] { Reversed(Value(0, 99.0, Measured, OlderDocument)) }),
            Series(keyMixed, new[]
            {
                Value(0, 10.0, Measured, OlderDocument),
                Reversed(Value(1, 99.0, Measured, NewerDocument))
            }));

        // Assert
        (await CountAnchorsAsync(keyOnlyMalformed)).Should().Be(0);

        var anchor = await GetSingleAnchorAsync(keyMixed);
        var rows = await ReadRowsAsync(archiveRtId, anchor.RtId);
        rows.Should().ContainSingle();
        rows[0].Temperature.Should().Be(10.0);
        (await CountRowsAsync(archiveRtId)).Should().Be(1);
    }

    /// <summary>
    /// Writes two competing deliveries for the same three windows in both arrival orders, each order on
    /// its own anchor, and returns the stored rows of both anchors.
    /// </summary>
    private async Task<(IReadOnlyList<ArchiveRow> FirstThenSecond, IReadOnlyList<ArchiveRow> SecondThenFirst)>
        WriteInBothOrdersAsync(OctoObjectId archiveRtId, string keyPrefix,
            Func<int, JsonObject> first, Func<int, JsonObject> second)
    {
        var slots = Enumerable.Range(0, 3).ToList();
        var keyFirstThenSecond = UniqueKey($"{keyPrefix}-12");
        var keySecondThenFirst = UniqueKey($"{keyPrefix}-21");

        await ExecuteNodeAsync(archiveRtId, Series(keyFirstThenSecond, slots.Select(first)));
        await ExecuteNodeAsync(archiveRtId, Series(keyFirstThenSecond, slots.Select(second)));

        await ExecuteNodeAsync(archiveRtId, Series(keySecondThenFirst, slots.Select(second)));
        await ExecuteNodeAsync(archiveRtId, Series(keySecondThenFirst, slots.Select(first)));

        var anchorFirstThenSecond = await GetSingleAnchorAsync(keyFirstThenSecond);
        var anchorSecondThenFirst = await GetSingleAnchorAsync(keySecondThenFirst);

        return (await ReadRowsAsync(archiveRtId, anchorFirstThenSecond.RtId),
            await ReadRowsAsync(archiveRtId, anchorSecondThenFirst.RtId));
    }

    /// <summary>A row without its anchor-specific parts, for comparing two anchors' results.</summary>
    private static object Payload(ArchiveRow row) =>
        (row.WindowStart, row.WindowEnd, row.Temperature, row.Quality, row.SourceDate);

    private static string UniqueKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static JsonObject Series(string key, IEnumerable<JsonObject> values) => new()
    {
        ["key"] = key,
        ["serial"] = $"SN-{key}",
        ["values"] = new JsonArray(values.Cast<JsonNode>().ToArray())
    };

    private static JsonObject Value(int slot, double temperature, int quality, DateTime sourceDate) => new()
    {
        ["from"] = WindowStart.Add(Period * slot).ToString("O"),
        ["to"] = WindowStart.Add(Period * (slot + 1)).ToString("O"),
        ["temperature"] = temperature,
        ["quality"] = quality,
        ["sourceDate"] = sourceDate.ToString("O")
    };

    private static SaveTimeRangeSeriesInArchiveNodeConfiguration CreateConfiguration(
        OctoObjectId archiveRtId, string ckTypeId) => new()
    {
        Path = "$.series",
        ArchiveRtId = archiveRtId.ToString(),
        CkTypeId = ckTypeId,
        ValuesProperty = "values",
        WellKnownNameFormat = "{key}",
        FromProperty = "from",
        ToProperty = "to",
        Columns =
        [
            // SerialNumber is required on SensorReading, so the anchor insert needs it
            new TimeRangeSeriesColumn
                { Name = "SerialNumber", ValueProperty = "serial", Scope = TimeRangeSeriesColumnScope.Series },
            new TimeRangeSeriesColumn { Name = "Temperature", ValueProperty = "temperature" },
            new TimeRangeSeriesColumn { Name = "Quality", ValueProperty = "quality" },
            new TimeRangeSeriesColumn { Name = "SourceDate", ValueProperty = "sourceDate" }
        ]
    };

    /// <summary>
    /// Provisions and activates a <c>TimeRangeArchive</c> over the test CK type with one column per
    /// mapped attribute. With <paramref name="withPrecedence" /> it declares
    /// <c>[{Quality, LowerWins}, {SourceDate, HigherWins}]</c> — the energy-metering rule.
    /// </summary>
    private async Task<OctoObjectId> CreateAndActivateSeriesArchiveAsync(string name, bool withPrecedence)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        var archive = new RtTimeRangeArchive
        {
            RtWellKnownName = $"{name}_{Guid.NewGuid():N}",
            TargetCkTypeId = fixture.TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Period = Period,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "SerialNumber", Indexed = true, Required = false },
                new() { Path = "Temperature", Indexed = true, Required = false },
                new() { Path = "Quality", Indexed = true, Required = false },
                new() { Path = "SourceDate", Indexed = true, Required = false }
            }
        };

        if (withPrecedence)
        {
            archive.ConflictPrecedence = new AttributeRecordValueList<RtCkArchiveConflictKeyRecord>
            {
                new() { Column = "Quality", Order = RtCkConflictKeyOrderEnum.LowerWins },
                new() { Column = "SourceDate", Order = RtCkConflictKeyOrderEnum.HigherWins }
            };
        }

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, archive);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var lifecycle = tenantContext.GetArchiveLifecycleService()
            ?? throw new InvalidOperationException("ArchiveLifecycleService not registered.");
        await lifecycle.ActivateAsync(archive.RtId);
        return archive.RtId;
    }

    private async Task ExecuteNodeAsync(OctoObjectId archiveRtId, params JsonObject[] series)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        var config = CreateConfiguration(archiveRtId, fixture.TestCkTypeId);
        // Cloned: a JsonNode can only have one parent, and a test may hand the same series in twice
        var input = new JsonObject { ["series"] = new JsonArray(series.Select(s => s.DeepClone()).ToArray()) };
        using var document = JsonDocument.Parse(input.ToJsonString());
        using var dataContext = new DataContextImpl(document.RootElement);

        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("SaveTimeRangeSeriesInArchive", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        var node = new SaveTimeRangeSeriesInArchiveNode(Next, CreateMeshEtlContext(tenantRepository),
            systemContext, ckCacheService);

        await node.ProcessObjectAsync(dataContext, nodeContext);
        await fixture.RefreshArchiveAsync(archiveRtId);
    }

    /// <summary>Swaps the window boundaries of a value, which makes its window unusable.</summary>
    private static JsonObject Reversed(JsonObject value)
    {
        (value["from"], value["to"]) = (value["to"]!.DeepClone(), value["from"]!.DeepClone());
        return value;
    }

    private async Task<int> CountAnchorsAsync(string wellKnownName)
    {
        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(
            session,
            new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            RtEntityQueryOptions.Create().FieldIn(nameof(RtEntity.RtWellKnownName), new List<string> { wellKnownName }),
            0,
            10);
        await session.CommitTransactionAsync();
        return result.Items.Count();
    }

    /// <summary>Asserts exactly one anchor exists for the series key and returns it.</summary>
    private async Task<RtEntity> GetSingleAnchorAsync(string wellKnownName)
    {
        var tenantRepository = fixture.GetSystemContext().GetSystemTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync(
            session,
            new RtCkId<CkTypeId>(fixture.TestCkTypeId),
            RtEntityQueryOptions.Create().FieldIn(nameof(RtEntity.RtWellKnownName), new List<string> { wellKnownName }),
            0,
            10);
        await session.CommitTransactionAsync();

        result.Items.Should().ContainSingle($"the series '{wellKnownName}' must have exactly one anchor");
        return result.Items.Single();
    }

    private async Task<IReadOnlyList<ArchiveRow>> ReadRowsAsync(OctoObjectId archiveRtId, OctoObjectId anchorRtId)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT window_start, window_end, rtid, serialnumber, temperature, quality, sourcedate " +
            $"FROM {QualifiedTable(archiveRtId)} WHERE rtid = @rtid ORDER BY window_start", conn);
        cmd.Parameters.AddWithValue("rtid", anchorRtId.ToString());

        var rows = new List<ArchiveRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ArchiveRow(
                ToUtc(reader.GetValue(0)),
                ToUtc(reader.GetValue(1)),
                reader.GetValue(2).ToString(),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                reader.IsDBNull(6) ? null : ToUtc(reader.GetValue(6))));
        }

        rows.Should().OnlyContain(r => r.RtId == anchorRtId.ToString());
        return rows;
    }

    private async Task<long> CountRowsAsync(OctoObjectId archiveRtId)
    {
        await using var conn = new NpgsqlConnection(fixture.CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {QualifiedTable(archiveRtId)}", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private string QualifiedTable(OctoObjectId archiveRtId) =>
        $"\"{fixture.StreamDataTenantId}\".\"archive_{archiveRtId}\"";

    private static DateTime ToUtc(object value) => value switch
    {
        DateTime dt => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => throw new InvalidOperationException($"Unexpected timestamp type {value.GetType()}")
    };

    private static MeshEtlContext CreateMeshEtlContext(ITenantRepository tenantRepository)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>());
    }

    private sealed record ArchiveRow(
        DateTime WindowStart,
        DateTime WindowEnd,
        string? RtId,
        string? SerialNumber,
        double? Temperature,
        int? Quality,
        DateTime? SourceDate);
}
