using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Load;

/// <summary>
///     AB#7463 (Feature F3.7, decision note AB#7120): <c>ApplyChanges@2</c> refuses to insert a new entity of a type
///     whose CK method declares <c>replacesGenericCreate</c> (<c>MeshAdapterIntegrationTest/ReplacedAccount</c>,
///     method <c>Provision-1</c>) except in that method's own pipeline run — real orchestrator, real nodes
///     (<c>CreateUpdateInfo@1</c>, <c>ApplyChanges@2</c>, <c>GetRtEntitiesByType@1</c>), real CK cache, MongoDB.
/// </summary>
/// <remarks>
///     <para>
///         <b>What is real and what is modelled.</b> The marker (<see cref="IMeshEtlContext.MethodInvocation" />) is
///         what the controller puts into the sealed lease envelope and the adapter copies into the run's
///         <see cref="MeshEtlContext" /> (AB#7461 / AB#7459, covered in their own repositories). Here the context is
///         built by hand with or without that marker: "the method's own run" is a context with a marker for this
///         pipeline, "manual <c>ExecutePipeline</c>" (REST, CLI, Studio, cron) is a context without one, and the
///         "chained pipeline" is the context of the <i>downstream</i> pipeline of a <c>ToPipelineDataEvent</c> chain,
///         which never receives the marker (it is not part of the event and not of the downstream lease), plus the
///         case that a marker of the upstream pipeline would reach the downstream run: it names another pipeline and
///         is refused as well. There is no controller, lease or event hub in this harness, so the end-to-end hand-over
///         from the method invocation to the node is not exercised here.
///     </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class ReplacedGenericCreateIntegrationTests(PipelineExecutionFixture fixture)
    : IClassFixture<PipelineExecutionFixture>
{
    private const string ReplacedType = "MeshAdapterIntegrationTest/ReplacedAccount";
    private const string PlainType = "MeshAdapterIntegrationTest/TestControl";

    private static readonly OctoObjectId MethodPipelineId = new("000000000000000000000071");
    private static readonly OctoObjectId OtherPipelineId = new("000000000000000000000072");

    private const string InsertAccountPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: MeshAdapterIntegrationTest/ReplacedAccount
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: AccountName
                attributeValueType: String
                valuePath: $.name
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        """;

    private const string UpdateAccountPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._updates
            targetValueKind: Array
            updateKind: UPDATE
            ckTypeId: MeshAdapterIntegrationTest/ReplacedAccount
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: AccountName
                attributeValueType: String
                valuePath: $.name
          - type: ApplyChanges@2
            entityUpdatesPath: $._updates
        """;

    private const string InsertControlPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: MeshAdapterIntegrationTest/TestControl
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: ControlName
                attributeValueType: String
                valuePath: $.name
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        """;

    private const string ReadAccountPipeline = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/ReplacedAccount
            targetPath: $.accounts
            fieldFilters:
              - attributePath: AccountName
                operator: Equals
                comparisonValuePath: $.name
        """;

    private const string ReadControlPipeline = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/TestControl
            targetPath: $.accounts
            fieldFilters:
              - attributePath: ControlName
                operator: Equals
                comparisonValuePath: $.name
        """;

    private static JsonObject Input(string name, string? rtId = null) => new()
    {
        ["rtId"] = rtId ?? OctoObjectId.GenerateNewId().ToString(),
        ["name"] = name
    };

    /// <summary>
    ///     The marker for the replacing method's own run of <paramref name="pipelineId" />. The tenant CK cache is
    ///     loaded first: a test that asks for the marker before its first pipeline run (test order is not fixed)
    ///     would otherwise see an empty cache.
    /// </summary>
    private async Task<ExecutePipelineMethodInvocation> MarkerFor(OctoObjectId pipelineId)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        var cache = fixture.GetService<ICkCacheService>();
        await repository.LoadCacheForTenantAsync(cache);
        GenericCreateReplacement.TryGetReplacingMethod(cache, repository.TenantId,
            new RtCkId<CkTypeId>(ReplacedType), out var method).Should().BeTrue("the test model flags ReplacedAccount");
        return new ExecutePipelineMethodInvocation
        {
            MethodId = method!.QualifiedMethodId,
            InvocationId = Guid.NewGuid(),
            CkTypeId = ReplacedType,
            PipelineRtId = pipelineId.ToString()
        };
    }

    [Fact]
    public async Task MethodOwnRun_InsertsTheEntity()
    {
        fixture.EnsureInitialized();
        var name = $"own-{Guid.NewGuid():N}";

        await RunAsync(InsertAccountPipeline, Input(name), MethodPipelineId, await MarkerFor(MethodPipelineId));

        (await CountAsync(ReadAccountPipeline, name)).Should().Be(1);
    }

    [Fact]
    public async Task ManualExecution_WithoutMarker_IsRefused_AndNothingIsWritten()
    {
        fixture.EnsureInitialized();
        var name = $"manual-{Guid.NewGuid():N}";

        var act = () => RunAsync(InsertAccountPipeline, Input(name), MethodPipelineId, marker: null);

        var ex = await act.Should().ThrowAsync<Exception>();
        AssertRefusedByGuard(ex.Which);
        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task ManualDryRun_WithoutMarker_IsRefused_BeforeTheDryRunBranch()
    {
        fixture.EnsureInitialized();
        var name = $"dry-{Guid.NewGuid():N}";

        var act = () => RunAsync(InsertAccountPipeline, Input(name), MethodPipelineId, marker: null, dryRun: true);

        var ex = await act.Should().ThrowAsync<Exception>();
        AssertRefusedByGuard(ex.Which);
        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task MethodOwnRun_AsDryRun_IsNotRefused_AndWritesNothing()
    {
        fixture.EnsureInitialized();
        var name = $"owndry-{Guid.NewGuid():N}";

        await RunAsync(InsertAccountPipeline, Input(name), MethodPipelineId, await MarkerFor(MethodPipelineId),
            dryRun: true);

        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task ChainedPipeline_WithoutMarker_IsRefused()
    {
        fixture.EnsureInitialized();
        var name = $"chained-{Guid.NewGuid():N}";

        // The method's pipeline hands its data to a downstream pipeline with ToPipelineDataEvent; that run is an
        // ordinary run of ANOTHER pipeline and carries no marker.
        var act = () => RunAsync(InsertAccountPipeline, Input(name), OtherPipelineId, marker: null);

        var ex = await act.Should().ThrowAsync<Exception>();
        AssertRefusedByGuard(ex.Which);
        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task ChainedPipeline_EvenWithTheMarkerOfTheMethodsPipeline_IsRefused()
    {
        fixture.EnsureInitialized();
        var name = $"leak-{Guid.NewGuid():N}";

        // Should the marker ever reach the downstream run, it names the upstream pipeline and does not match.
        var leaked = await MarkerFor(MethodPipelineId);
        var act = () => RunAsync(InsertAccountPipeline, Input(name), OtherPipelineId, leaked);

        var ex = await act.Should().ThrowAsync<Exception>();
        AssertRefusedByGuard(ex.Which);
        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task UpdateOfAnExistingEntity_WithoutMarker_StillPasses()
    {
        fixture.EnsureInitialized();
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var name = $"upd-{Guid.NewGuid():N}";
        await RunAsync(InsertAccountPipeline, Input(name, rtId), MethodPipelineId, await MarkerFor(MethodPipelineId));

        var renamed = $"{name}-renamed";
        await RunAsync(UpdateAccountPipeline, Input(renamed, rtId), OtherPipelineId, marker: null);

        (await CountAsync(ReadAccountPipeline, renamed)).Should().Be(1);
        (await CountAsync(ReadAccountPipeline, name)).Should().Be(0);
    }

    [Fact]
    public async Task InsertOfATypeWithoutReplacingMethod_WithoutMarker_StillPasses()
    {
        fixture.EnsureInitialized();
        var name = $"plain-{Guid.NewGuid():N}";

        await RunAsync(InsertControlPipeline, Input(name), OtherPipelineId, marker: null);

        (await CountAsync(ReadControlPipeline, name)).Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     The run failed because of the guard and for no other reason: the exception chain holds a
    ///     <see cref="GenericCreateReplacedByMethodException" /> with the stable code and one refusal for the type.
    /// </summary>
    private static void AssertRefusedByGuard(Exception failure)
    {
        GenericCreateReplacedByMethodException? refusal = null;
        for (var current = failure; current != null && refusal == null; current = current.InnerException)
        {
            refusal = current as GenericCreateReplacedByMethodException;
        }

        refusal.Should().NotBeNull($"the failure was: {failure}");
        refusal!.Code.Should().Be("GENERIC_CREATE_REPLACED_BY_METHOD");
        refusal.Refusals.Should().ContainSingle().Which.CkTypeId.Should().Be(new RtCkId<CkTypeId>(ReplacedType).FullName);
    }

    private async Task<int> CountAsync(string readPipeline, string name)
    {
        var result = await RunAsync(readPipeline, new JsonObject { ["name"] = name }, OtherPipelineId, marker: null);
        return ((JsonArray)result["accounts"]!["Items"]!).Count;
    }

    private async Task<JsonObject> RunAsync(string yaml, JsonObject input, OctoObjectId pipelineId,
        ExecutePipelineMethodInvocation? marker, bool dryRun = false)
    {
        var repository = fixture.GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, pipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", pipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>(), methodInvocation: marker);

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(yaml);
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: input,
            executionMode: dryRun ? new DefaultPipelineExecutionMode { IsDryRun = true } : null);
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }
}
