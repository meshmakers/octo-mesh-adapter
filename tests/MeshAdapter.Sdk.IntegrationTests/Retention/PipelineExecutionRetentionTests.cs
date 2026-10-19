using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshAdapter.Sdk.IntegrationTests.Retention;

/// <summary>
///     AB#7010 — pipeline executions must not retain managed memory. Runs a pipeline of the energyiq
///     shape (ForEach over states, GetRtEntitiesByType with a field filter, nested ForEach,
///     CreateUpdateInfo with <c>=now()</c>, Flatten, ApplyChanges@2) many times through the real
///     orchestrator against a real MongoDB and compares the live heap after a full blocking GC.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class PipelineExecutionRetentionTests(TwoTenantLeaseFixture fixture, Xunit.ITestOutputHelper output)
    : IClassFixture<TwoTenantLeaseFixture>
{
    private const string Tenant = TwoTenantLeaseFixture.TenantA;

    private const string CorePipeline = """
        triggers:
          - type: RetentionHarnessTrigger@1
        transformations:
          - type: ForEach@1
            iterationPath: $.states
            targetPath: $.updateResults
            mergePath: $.updates
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/SensorReading
                targetPath: $.updates._controls
                identity: System
                fieldFilters:
                  - attributePath: SerialNumber
                    operator: Equals
                    comparisonValuePath: $.key.serial
              - type: ForEach@1
                path: $
                fullDocumentPath: $.full
                iterationPath: $.updates._controls.Items
                targetPath: $.updates._controlResults
                mergePath: $.updates
                transformations:
                  - type: CreateUpdateInfo@1
                    targetPath: $.updates._controlUpdate
                    updateKind: UPDATE
                    ckTypeIdPath: $.key.CkTypeId.SemanticVersionedFullName
                    rtIdPath: $.key.RtId
                    attributeUpdates:
                      - attributeName: Temperature
                        attributeValueType: Double
                        valuePath: $.full.key.value
                      - attributeName: SourceDate
                        attributeValueType: DateTime
                        value: =now()
          - type: Flatten@1
            path: $.updateResults[*]._controlResults[*]._controlUpdate
            targetPath: $._updateItems
            targetValueKind: Simple
            targetValueWriteMode: Overwrite
          - type: If@1
            path: $._updateItems[0].RtId
            operator: NotEquals
            value: null
            valueType: String
            transformations:
              - type: ApplyChanges@2
                entityUpdatesPath: $._updateItems
        """;

    [Fact]
    public async Task CorePipelineDoesNotRetainMemory()
    {
        await RunAsync(CorePipeline, warmup: int.Parse(Environment.GetEnvironmentVariable("RETENTION_WARMUP") ?? "300"), measured: int.Parse(Environment.GetEnvironmentVariable("RETENTION_N") ?? "3000"));
    }

    private async Task RunAsync(string pipelineYaml, int warmup, int measured)
    {
        fixture.EnsureInitialized();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Warning);
        });
        var sys = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        services.Configure<OctoSystemConfiguration>(c =>
        {
            c.SystemDatabaseName = sys.SystemDatabaseName;
            c.DatabaseHost = sys.DatabaseHost;
            c.AdminUser = sys.AdminUser;
            c.AdminUserPassword = sys.AdminUserPassword;
            c.DatabaseUserPassword = sys.DatabaseUserPassword;
            c.UseDirectConnection = sys.UseDirectConnection;
        });
        services.AddCkModelSystemV2();
        services.AddCkModelMeshAdapterIntegrationTestV1();
        services.AddOctoMeshAdapter().RegisterTriggerNode<RetentionHarnessTriggerNode>();
        services.AddSingleton<IPipelineRegistryService, PipelineRegistryService>();
        services.AddSingleton(FakeItEasy.A.Fake<Meshmakers.Octo.Sdk.ServiceClient.IServiceClientAccessToken>());
        services.AddSingleton(FakeItEasy.A.Fake<Meshmakers.Octo.Common.DistributionEventHub.Services.IDistributionEventHubService>());

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPipelineRegistryService>();
        var orchestrator = provider.GetRequiredService<IEtlDataOrchestrator>();
        var contextCreator = provider.GetRequiredService<IContextCreatorService>();
        var serializer = provider.GetRequiredService<IPipelineConfigurationSerializer>();

        var pipelineRtId = new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId());
        var dto = new PipelineConfigurationDto(OctoObjectId.GenerateNewId(), pipelineRtId,
            isDebuggingEnabled: false, nodeConfiguration: pipelineYaml, configurations: []);
        var errors = new List<DeploymentUpdateErrorMessageDto>();
        (await registry.RegisterPipelinesAsync(Tenant, [dto], errors)).Should()
            .BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
        registry.TryGetPipelineRegistration(Tenant, pipelineRtId, out var registration).Should().BeTrue();

        var input = new JsonObject
        {
            ["states"] = new JsonArray(
                new JsonObject { ["serial"] = TwoTenantLeaseFixture.MarkerOf(Tenant), ["value"] = 21.5 },
                new JsonObject { ["serial"] = TwoTenantLeaseFixture.MarkerOf(Tenant), ["value"] = 22.5 })
        };

        async Task ExecuteAsync()
        {
            var id = Guid.NewGuid();
            var options = new ExecutePipelineOptions(DateTime.UtcNow);
            var ctx = await contextCreator.CreateEtlContext<IMeshEtlContext>(registration!, options, id);
            await orchestrator.ExecutePipelineAsync(registration!.NodeDefinitionRoot, ctx, null,
                JsonNode.Parse(input.ToJsonString()));
        }

        for (var i = 0; i < warmup; i++) await ExecuteAsync();
        var before = LiveBytes();
        GcDump("start");
        var gen2Before = GC.CollectionCount(2);
        for (var i = 0; i < measured; i++) await ExecuteAsync();
        var after = LiveBytes();
        GcDump("end");

        var perThousand = (after - before) / 1024.0 / (measured / 1000.0);
        output.WriteLine($"PID {Environment.ProcessId}; live before {before / 1024} KiB, after {after / 1024} KiB, " +
                         $"growth {perThousand:F0} KiB per 1000 executions (gen2 GCs {GC.CollectionCount(2) - gen2Before})");
        Console.Error.WriteLine($"[retention] growth {perThousand:F0} KiB per 1000 executions");

        perThousand.Should().BeLessThan(256, "an execution must not retain memory (AB#7010)");
    }

    [NodeName("RetentionHarnessTrigger", 1)]
    internal record RetentionHarnessTriggerNodeConfiguration : TriggerNodeConfiguration;

    [NodeConfiguration(typeof(RetentionHarnessTriggerNodeConfiguration))]
    internal sealed class RetentionHarnessTriggerNode : ITriggerPipelineNode
    {
        public Task StartAsync(ITriggerContext context) => Task.CompletedTask;

        public Task StopAsync(ITriggerContext context) => Task.CompletedTask;
    }

    private static void GcDump(string label)
    {
        var dir = Environment.GetEnvironmentVariable("RETENTION_GCDUMP_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("dotnet-gcdump",
            $"collect -p {Environment.ProcessId} -o \"{Path.Combine(dir, label + ".gcdump")}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static long LiveBytes()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(true);
    }
}
