using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
///     AB#6933: <c>GetRtEntitiesByType@1</c> with <c>ckInterfaceId</c> through the real orchestrator, the real node, the
///     real CK cache and the real engine method <c>GetRtEntitiesByInterfaceAsync</c> (AB#7019) on MongoDB. Test model:
///     interface <c>MeshAdapterIntegrationTest/Shelved</c>, implemented by <c>ShelfBox</c> and <c>ShelfCrate</c>
///     (and its derived <c>ShelfHeavyCrate</c>) - three types in two collection roots; <c>ShelfPlain</c> has the same
///     members but does not implement it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetRtEntitiesByInterfaceIntegrationTests(ShelvedFixture fixture, ITestOutputHelper output)
    : IClassFixture<ShelvedFixture>
{
    private const string Model = "MeshAdapterIntegrationTest";
    private static readonly OctoObjectId PipelineId = new("000000000000000000000081");

    private const string BySortedCode = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckInterfaceId: MeshAdapterIntegrationTest/Shelved
            take: 50
            sortOrders:
              - attributeName: ShelfCode
                sortOrder: Ascending
            targetPath: $.shelf
        """;

    // ---- the result -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReturnsTheEntitiesOfAllImplementers_DerivedTypeIncluded_InOneGlobalOrder_AndNeverANonImplementer()
    {
        await SeedAsync();

        var result = await RunAsync(BySortedCode);

        // a-02 Crate, b-01 Box, b-06 HeavyCrate, c-03 Crate, d-04 Box, e-05 HeavyCrate; ShelfPlain "a-00" is not an implementer.
        Codes(result).Should().Equal("a-02", "b-01", "b-06", "c-03", "d-04", "e-05");
        TypesOf(result).Should().Equal("ShelfCrate", "ShelfBox", "ShelfHeavyCrate", "ShelfCrate", "ShelfBox", "ShelfHeavyCrate");
        TotalCount(result).Should().Be(6);
    }

    [Fact]
    public async Task TheVersionedInterfaceId_AsWrittenInTheCkModel_SelectsTheSameEntities()
    {
        await SeedAsync();

        // The CK element id is Shelved-1; the runtime id of a tenant is version-less. Both spellings are accepted.
        var yaml = BySortedCode.Replace("MeshAdapterIntegrationTest/Shelved", "MeshAdapterIntegrationTest/Shelved-1");
        yaml.Should().Contain("MeshAdapterIntegrationTest/Shelved-1");
        var result = await RunAsync(yaml);

        Codes(result).Should().Equal("a-02", "b-01", "b-06", "c-03", "d-04", "e-05");
    }

    [Fact]
    public async Task FieldFilter_SortOrder_SkipAndTake_BehaveLikeTheTypeVariant()
    {
        await SeedAsync();

        // weight >= 3 : a-02 (7), b-01 (5), d-04 (3), e-05 (9), b-06 (4) ; sorted by weight descending: e-05 (9), a-02 (7), b-01 (5), b-06 (4), d-04 (3)
        var result = await RunAsync("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                skip: 1
                take: 2
                fieldFilters:
                  - attributePath: ShelfWeight
                    operator: GreaterEqualThan
                    comparisonValue: 3
                sortOrders:
                  - attributeName: ShelfWeight
                    sortOrder: Descending
                targetPath: $.shelf
            """);

        Codes(result).Should().Equal("a-02", "b-01");
        TotalCount(result).Should().Be(5);
    }

    [Fact]
    public async Task TheTypeVariant_IsUnchanged_AndStillReturnsOnlyItsOwnType()
    {
        await SeedAsync();

        var result = await RunAsync("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/ShelfBox
                sortOrders:
                  - attributeName: ShelfCode
                    sortOrder: Ascending
                targetPath: $.shelf
            """);

        Codes(result).Should().Equal("b-01", "d-04");
    }

    [Fact]
    public async Task AnInterfaceWithoutEntities_ReturnsAnEmptyResultWithCountZero()
    {
        await fixture.ResetAsync();

        var result = await RunAsync(BySortedCode);

        Codes(result).Should().BeEmpty();
        TotalCount(result).Should().Be(0);
    }

    // ---- permissions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARestrictedCaller_NeverSeesProtectedTypes_AndTheCountDoesNotLeakThem()
    {
        await SeedAsync();
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            // ShelfCrate (and its derived ShelfHeavyCrate) may only be read by CrateAdmin.
            new RtDataPolicyRule("crates",
                new HashSet<string> { new RtCkId<CkTypeId>($"{Model}/ShelfCrate").SemanticVersionedFullName },
                [RtDataAction.Read], OwnedOnly: false, AuditOnly: false, new HashSet<string> { "CrateAdmin" })
        ]);
        try
        {
            var reader = new VerifiedPrincipal("u-reader", "system", null, null, ["Reader"]);
            var admin = new VerifiedPrincipal("u-admin", "system", null, null, ["Reader", "CrateAdmin"]);

            var asReader = await RunAsync(BySortedCode, reader);
            var asAdmin = await RunAsync(BySortedCode, admin);
            var asSystem = await RunAsync(BySortedCode, caller: null);

            // Only ShelfBox is visible to the reader; ShelfCrate and ShelfHeavyCrate never appear and are not counted.
            Codes(asReader).Should().Equal("b-01", "d-04");
            TotalCount(asReader).Should().Be(2);
            Codes(asAdmin).Should().Equal("a-02", "b-01", "b-06", "c-03", "d-04", "e-05");
            Codes(asSystem).Should().Equal("a-02", "b-01", "b-06", "c-03", "d-04", "e-05");

            // The type variant shows the reader exactly what the interface variant shows for that type.
            var crateAsReader = await RunAsync("""
                transformations:
                  - type: GetRtEntitiesByType@1
                    ckTypeId: MeshAdapterIntegrationTest/ShelfCrate
                    targetPath: $.shelf
                """, reader);
            Codes(crateAsReader).Should().BeEmpty();
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    // ---- failures: stable code and parameters ---------------------------------------------------------------------

    [Fact]
    public async Task BothTypeAndInterface_FailsTheNodeValidation()
    {
        await SeedAsync();

        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/ShelfBox
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                take: 5
                targetPath: $.shelf
            """);

        failure.Code.Should().Be(RtEntitySelectionCodes.NodeConfigurationInvalid);
        failure.Reason.Should().Be(RtEntitySelectionReasons.CkTypeAndCkInterfaceBothSet);
    }

    [Fact]
    public async Task NeitherTypeNorInterface_FailsTheNodeValidation()
    {
        await SeedAsync();

        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                targetPath: $.shelf
            """);

        failure.Code.Should().Be(RtEntitySelectionCodes.NodeConfigurationInvalid);
        failure.Reason.Should().Be(RtEntitySelectionReasons.CkTypeOrCkInterfaceRequired);
    }

    [Fact]
    public async Task UnknownInterface_ReachesThePipelineRunWithTheEngineNumberReasonAndParameters()
    {
        await SeedAsync();

        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/DoesNotExist
                take: 5
                targetPath: $.shelf
            """);

        AssertEngineRefusal(failure, "UnknownInterface");
        failure.Parameters["ckInterfaceId"].Should().Be($"{Model}/DoesNotExist");
    }

    [Fact]
    public async Task NavigationInASortPath_ReachesThePipelineRun()
    {
        await SeedAsync();

        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                take: 5
                sortOrders:
                  - attributeName: ShelfCode->Other
                    sortOrder: Ascending
                targetPath: $.shelf
            """);

        AssertEngineRefusal(failure, "NavigationNotSupported");
        failure.Parameters["attributePath"].Should().Be("ShelfCode->Other");
    }

    [Fact]
    public async Task FilterOnANonMember_ReachesThePipelineRun()
    {
        await SeedAsync();

        // ShelfPlain has this member, the interface does not: the interface query refuses it instead of ignoring it.
        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                take: 5
                fieldFilters:
                  - attributePath: NotAMember
                    operator: Equals
                    comparisonValue: x
                targetPath: $.shelf
            """);

        AssertEngineRefusal(failure, "UnknownMember");
        failure.Parameters["attributePath"].Should().Be("NotAMember");
        failure.Parameters["usage"].Should().Be("filter");
    }

    [Fact]
    public async Task SkipPlusTakeAboveTheCap_ReachesThePipelineRun()
    {
        await SeedAsync();

        var failure = await FailureOf("""
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                skip: 900
                take: 200
                targetPath: $.shelf
            """);

        AssertEngineRefusal(failure, "PageTooLarge");
        failure.Parameters["maxSkipPlusTake"].Should().Be("1000");
    }

    // ---- performance: documented check, printed, not asserted ------------------------------------------------------

    /// <summary>
    ///     The interface variant against the type variant with the same row count, through the real node. Prints the
    ///     ratio (median of the runs); only sanity is asserted, so it cannot flake in CI. The budget is that of AB#7019
    ///     (one collection at most 1.25x of the type query; indexed multi-collection first page p95 at most 100 ms at
    ///     1M entities / 30 roots, asserted by the engine's performance test, not here).
    /// </summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task InterfaceVariantAgainstTypeVariant_SameRowCount_PrintsTheRatio()
    {
        await fixture.ResetAsync();
        var perType = int.TryParse(Environment.GetEnvironmentVariable("OCTO_TEST_SHELF_ENTITIES_PER_TYPE"), out var n) ? n : 3000;
        await InsertManyAsync("ShelfBox", perType, 1_000_000);
        await InsertManyAsync("ShelfCrate", perType, 2_000_000);
        await InsertManyAsync("ShelfHeavyCrate", perType, 3_000_000);

        // Same row count: the first 50 of ONE type (type variant) against the first 50 of the interface, which here all come
        // from the three types; the sort field is indexed in both collections.
        const string typeVariant = """
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/ShelfBox
                skip: 0
                take: 50
                sortOrders:
                  - attributeName: ShelfCode
                    sortOrder: Ascending
                targetPath: $.shelf
            """;
        const string interfaceVariant = """
            transformations:
              - type: GetRtEntitiesByType@1
                ckInterfaceId: MeshAdapterIntegrationTest/Shelved
                skip: 0
                take: 50
                sortOrders:
                  - attributeName: ShelfCode
                    sortOrder: Ascending
                targetPath: $.shelf
            """;

        var typeMs = await MedianMsAsync(typeVariant);
        var interfaceMs = await MedianMsAsync(interfaceVariant);

        var typeResult = await RunAsync(typeVariant);
        var interfaceResult = await RunAsync(interfaceVariant);
        Codes(typeResult).Should().HaveCount(50);
        Codes(interfaceResult).Should().HaveCount(50);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"GetRtEntitiesByType first page of 50, {perType} entities in each of 3 types (2 collection roots), indexed ShelfCode, median of 15 runs: " +
            $"type variant {typeMs:F1} ms, interface variant {interfaceMs:F1} ms, ratio {interfaceMs / typeMs:F2}"));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private async Task<double> MedianMsAsync(string yaml)
    {
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(yaml);
        }

        var times = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            await RunAsync(yaml);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[times.Count / 2];
    }

    private static void AssertEngineRefusal(RtEntitySelectionException failure, string reason)
    {
        failure.Code.Should().Be(RtEntitySelectionCodes.InterfaceQuery);
        failure.MessageNumber.Should().Be(7019);
        failure.Reason.Should().Be(reason);
        failure.Parameters["reason"].Should().Be(reason);
    }

    private static string[] Codes(JsonObject result) =>
        Items(result).Select(i => (string)AttributeOf(i, "ShelfCode")!).ToArray();

    private static string[] TypesOf(JsonObject result) =>
        Items(result).Select(i => (i["CkTypeId"] is JsonObject o ? (string)o["FullName"]! : (string)i["CkTypeId"]!)
            .Split('/')[^1].Split('-')[0]).ToArray();

    private static long TotalCount(JsonObject result) => (long)result["shelf"]!["TotalCount"]!;

    private static IEnumerable<JsonNode> Items(JsonObject result) => ((JsonArray)result["shelf"]!["Items"]!).Select(i => i!);

    private static JsonNode? AttributeOf(JsonNode item, string name)
    {
        var attributes = (JsonObject)item["Attributes"]!;
        return attributes.First(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Weights and codes: a-02 Crate 7, b-01 Box 5, b-06 Heavy 4, c-03 Crate 1, d-04 Box 3, e-05 Heavy 9; plain a-00.</summary>
    private async Task SeedAsync()
    {
        await fixture.ResetAsync();
        await InsertAsync("ShelfBox", 1, "b-01", 5);
        await InsertAsync("ShelfBox", 2, "d-04", 3);
        await InsertAsync("ShelfCrate", 3, "a-02", 7);
        await InsertAsync("ShelfCrate", 4, "c-03", 1);
        await InsertAsync("ShelfHeavyCrate", 5, "e-05", 9);
        await InsertAsync("ShelfHeavyCrate", 6, "b-06", 4);
        await InsertAsync("ShelfPlain", 7, "a-00", 1);
    }

    private async Task InsertAsync(string type, int number, string code, int weight)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await InsertCoreAsync(repository, session, type, number, code, weight);
        await session.CommitTransactionAsync();
    }

    private async Task InsertManyAsync(string type, int count, int firstNumber)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());
        for (var offset = 0; offset < count; offset += 500)
        {
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            for (var i = offset; i < Math.Min(offset + 500, count); i++)
            {
                // Interleaved codes, so that the first page needs rows of every type.
                var number = firstNumber + i;
                await InsertCoreAsync(repository, session, type, number, $"k-{i * 3 + firstNumber / 1_000_000:D8}", i);
            }

            await session.CommitTransactionAsync();
        }
    }

    private static async Task InsertCoreAsync(ITenantRepository repository, IOctoSession session, string type, int number,
        string code, int weight)
    {
        var entity = await repository.CreateTransientRtEntityByRtCkIdAsync(new RtCkId<CkTypeId>($"{Model}/{type}"));
        entity.RtId = OctoObjectId.Parse(number.ToString("x24", CultureInfo.InvariantCulture));
        entity.SetAttributeValue("ShelfCode", AttributeValueTypesDto.String, code);
        entity.SetAttributeValue("ShelfWeight", AttributeValueTypesDto.Integer, weight);
        await repository.InsertOneRtEntityAsync(session, new RtCkId<CkTypeId>($"{Model}/{type}"), entity);
    }

    private async Task<RtEntitySelectionException> FailureOf(string yaml, VerifiedPrincipal? caller = null)
    {
        var act = () => RunAsync(yaml, caller);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        for (var current = thrown; current != null; current = current.InnerException)
        {
            if (current is RtEntitySelectionException selection)
            {
                return selection;
            }
        }

        throw new Xunit.Sdk.XunitException($"No RtEntitySelectionException in the failure chain: {thrown}");
    }

    private async Task<JsonObject> RunAsync(string yaml, VerifiedPrincipal? caller = null)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, PipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", PipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), verifiedPrincipal: caller);

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(yaml);
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: new JsonObject());
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }
}
