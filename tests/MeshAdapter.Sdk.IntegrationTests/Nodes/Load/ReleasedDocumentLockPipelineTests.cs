using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using FluentAssertions;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     The engine's released-document lock behind real pipelines (real orchestrator, real nodes, MongoDB): a
///     <c>MeshAdapterIntegrationTest/ReleasedTestInvoice</c> is released at <c>ReleasedTestStatus = Released</c> by the
///     range <c>ReleasedTestInvoiceNumber</c>. An update or delete through <c>ApplyChanges@1</c> (system session) and
///     <c>ApplyChanges@2</c> (the default caller identity and <c>identity: System</c>) fails the execution with
///     <see cref="ReleasedDocumentWriteRefusedException" />, code <c>RELEASED_DOCUMENT_LOCKED</c>, and the document stays
///     as it was; the same pipelines change and delete a draft. The identity modes Caller and ServiceAccount of
///     <c>ApplyChanges@2</c> are covered with fakes in <c>ApplyChangesReleasedDocumentRefusalTests</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class ReleasedDocumentLockPipelineTests(PipelineExecutionFixture fixture)
    : IClassFixture<PipelineExecutionFixture>
{
    private const string InvoiceType = "MeshAdapterIntegrationTest/ReleasedTestInvoice";
    private const string Range = "ReleasedTestInvoiceNumber";
    private static readonly OctoObjectId PipelineId = new("000000000000000000000081");
    private static readonly SemaphoreSlim RangeLock = new(1, 1);
    private static bool _rangeStored;

    private const string InsertPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: MeshAdapterIntegrationTest/ReleasedTestInvoice
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: ReleasedTestTitle
                attributeValueType: String
                valuePath: $.title
              - attributeName: ReleasedTestStatus
                attributeValueType: String
                valuePath: $.status
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        """;

    private const string ReadPipeline = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/ReleasedTestInvoice
            targetPath: $.invoices
            fieldFilters:
              - attributePath: ReleasedTestTitle
                operator: Equals
                comparisonValuePath: $.title
        """;

    public static TheoryData<string, string> WritePaths => new()
    {
        { "ApplyChanges@1", "UPDATE" },
        { "ApplyChanges@1", "DELETE" },
        { "ApplyChanges@2", "UPDATE" },
        { "ApplyChanges@2", "DELETE" },
        { "ApplyChanges@2 System", "UPDATE" },
        { "ApplyChanges@2 System", "DELETE" }
    };

    private static string WritePipeline(string node, string updateKind)
    {
        var apply = node switch
        {
            "ApplyChanges@1" => "- type: ApplyChanges@1\n    path: $._writes",
            "ApplyChanges@2 System" => "- type: ApplyChanges@2\n    entityUpdatesPath: $._writes\n    identity: System",
            _ => "- type: ApplyChanges@2\n    entityUpdatesPath: $._writes"
        };
        var attributes = updateKind == "DELETE"
            ? ""
            : """

                attributeUpdates:
                  - attributeName: ReleasedTestTitle
                    attributeValueType: String
                    valuePath: $.newTitle
            """;
        return $"""
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $._writes
                targetValueKind: Array
                updateKind: {updateKind}
                ckTypeId: {InvoiceType}
                rtIdPath: $.rtId{attributes}
              {apply}
            """;
    }

    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task WriteToAReleasedDocument_FailsTheExecution_WithTheEngineCode(string node, string updateKind)
    {
        fixture.EnsureInitialized();
        var (rtId, title) = await InsertAsync("Released");
        var before = await ReadAsync(title);
        before!.ToJsonString().Should().Contain("RTI-", "a released document gets its number");

        var act = () => RunAsync(WritePipeline(node, updateKind),
            new JsonObject { ["rtId"] = rtId, ["newTitle"] = $"{title}-changed" });

        var failure = (await act.Should().ThrowAsync<Exception>()).Which;
        ReleasedDocumentWriteRefusedException? refused = null;
        for (var current = failure; current != null && refused == null; current = current.InnerException)
        {
            refused = current as ReleasedDocumentWriteRefusedException;
        }

        refused.Should().NotBeNull($"the failure was: {failure}");
        refused!.ErrorCode.Should().Be("RELEASED_DOCUMENT_LOCKED");
        refused.Message.Should().Contain("RELEASED_DOCUMENT_LOCKED: ").And.Contain(rtId).And.Contain(" 168 ");
        (await ReadAsync(title))!.ToJsonString().Should().Be(before.ToJsonString(), "nothing was written");
    }

    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task WriteToADraft_Succeeds(string node, string updateKind)
    {
        fixture.EnsureInitialized();
        var (rtId, title) = await InsertAsync("Draft");

        await RunAsync(WritePipeline(node, updateKind),
            new JsonObject { ["rtId"] = rtId, ["newTitle"] = $"{title}-changed" });

        (await ReadAsync(title)).Should().BeNull();
        if (updateKind == "UPDATE")
        {
            (await ReadAsync($"{title}-changed")).Should().NotBeNull();
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<(string RtId, string Title)> InsertAsync(string status)
    {
        await EnsureRangeAsync();
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var title = $"rd-{Guid.NewGuid():N}";
        await RunAsync(InsertPipeline, new JsonObject { ["rtId"] = rtId, ["title"] = title, ["status"] = status });
        return (rtId, title);
    }

    /// <summary>The range with its release configuration, stored once per test run.</summary>
    private async Task EnsureRangeAsync()
    {
        await RangeLock.WaitAsync();
        try
        {
            if (_rangeStored)
            {
                return;
            }

            var repository = fixture.GetSystemContext().GetSystemTenantRepository();
            using var session = await repository.GetSessionAsync();
            session.StartTransaction();
            var range = await repository.CreateTransientRtEntityAsync<RtAutoIncrement>();
            range.RtWellKnownName = Range;
            range.CurrentValue = 0;
            range.End = 99999;
            range.Format = "RTI-{0:D5}";
            range.Release = new RtNumberRangeReleaseRecord
            {
                StateAttribute = "ReleasedTestStatus",
                ReleasedValues = new AttributeStringValueList(["Released"])
            };
            await repository.InsertOneRtEntityAsync(session, range);
            await session.CommitTransactionAsync();
            _rangeStored = true;
        }
        finally
        {
            RangeLock.Release();
        }
    }

    private async Task<JsonNode?> ReadAsync(string title)
    {
        var result = await RunAsync(ReadPipeline, new JsonObject { ["title"] = title });
        var items = (JsonArray)result["invoices"]!["Items"]!;
        return items.Count == 0 ? null : items.Single();
    }

    private async Task<JsonObject> RunAsync(string yaml, JsonObject input)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, PipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", PipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(yaml);
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: input);
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }
}
