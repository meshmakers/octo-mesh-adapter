using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that provides MongoDB (via <see cref="DatabaseFixture"/>) plus a single-node CrateDB
/// Testcontainer for stream-data integration tests. It registers the CrateDB stream-data stack and
/// a dedicated test CK model (<c>MeshAdapterIntegrationTest</c>), enables stream data for the system
/// tenant, then provisions and activates a <c>RawArchive</c> and seeds it with a handful of
/// <see cref="StreamDataPoint"/>s. The resulting archive id is exposed as <see cref="ArchiveRtId"/>.
///
/// Container bring-up mirrors the proven pattern in
/// <c>octo-asset-repo-services</c>'s <c>StreamDataFixture</c>.
/// </summary>
public class StreamDataFixture : SystemFixture
{
    private IContainer? _crateDbContainer;

    public string? CrateDbConnectionString { get; private set; }

    /// <summary>System tenant id (no hyphens — safe as a CrateDB schema identifier).</summary>
    public string StreamDataTenantId => GetSystemContext().TenantId;

    /// <summary>Target CK type of the provisioned archive (from the test CK model).</summary>
    public string TestCkTypeId => "MeshAdapterIntegrationTest/SensorReading";

    /// <summary>Runtime id of the activated <c>RawArchive</c> the fixture writes to.</summary>
    public OctoObjectId ArchiveRtId { get; private set; }

    public string ArchiveRtIdString => ArchiveRtId.ToString();

    /// <summary>Known test data: 5 points at 15-minute intervals starting here.</summary>
    public DateTime TestDataStartTime { get; } = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    public int TestDataPointCount { get; } = 5;

    public StreamDataFixture()
    {
        // Register the dedicated test CK model before the provider is built (base ctor already
        // registered AddRuntimeEngine + System v2).
        Services.AddCkModelMeshAdapterIntegrationTestV1();
    }

    protected override async Task InitializeServicesAsync()
    {
        // Same .NET 10 regex-timeout workaround the MongoDB fixture uses — the generic container
        // builder also parses the image name via a timed regex.
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(10));

        _crateDbContainer = new ContainerBuilder("crate:5.10.10")
            .WithName($"cratedb-meshadapter-test-{Guid.NewGuid():N}")
            .WithPortBinding(5432, true)
            .WithPortBinding(4200, true)
            .WithEnvironment("CRATE_HEAP_SIZE", "512m")
            .WithCommand("-Cdiscovery.type=single-node")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
            .Build();

        await _crateDbContainer.StartAsync();

        var crateDbPort = _crateDbContainer.GetMappedPublicPort(5432);
        CrateDbConnectionString = $"Host=localhost;Port={crateDbPort};Username=crate;SSL Mode=Prefer";

        // Flip the instance-level kill switch BEFORE registering the stream-data stack — the tenant
        // context refuses to enable stream data if StreamData:Enabled is false at process scope.
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton(new CrateDbTestConnectionString(CrateDbConnectionString));
        Services.AddStreamDataDatabase<TestStreamDataConfiguration>();
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));

        // Builds the provider + creates the system tenant (auto-imports the System model).
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();

        // Enable stream data BEFORE importing the custom CK model — the import invalidates the CK
        // cache mid-flight otherwise (same ordering subtlety as the asset-repo fixture).
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await tenantContext.EnableStreamDataAsync();

        var operationResult = new OperationResult();
        await systemContext.ImportCkModelAsync(
            new CkModelId("MeshAdapterIntegrationTest"), operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                "Failed to import MeshAdapterIntegrationTest CK model: " +
                string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
        }

        ArchiveRtId = await CreateAndActivateArchiveAsync();
        await InsertTestDataPoints();
    }

    private async Task<OctoObjectId> CreateAndActivateArchiveAsync()
    {
        var systemContext = GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        var archive = new RtRawArchive
        {
            RtWellKnownName = "SensorReadingArchive",
            TargetCkTypeId = TestCkTypeId,
            Status = RtCkArchiveStatusEnum.Created,
            Columns = new AttributeRecordValueList<RtCkArchiveColumnRecord>
            {
                new() { Path = "SerialNumber", Indexed = true, Required = false },
                new() { Path = "Temperature", Indexed = true, Required = false },
                // Dotted record paths — exercise the physical-column-name mapping
                // (Amount.Value -> amountvalue) that GetQueryByIdNode must resolve.
                new() { Path = "Amount.Value", Indexed = false, Required = false },
                new() { Path = "Amount.Unit", Indexed = false, Required = false }
            }
        };

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

    private async Task InsertTestDataPoints()
    {
        var systemContext = GetSystemContext();
        var tenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        var repo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException(
                "StreamDataRepository not available — was EnableStreamDataAsync called?");

        var ckTypeId = new RtCkId<CkTypeId>(TestCkTypeId);
        var points = new List<StreamDataPoint>(TestDataPointCount);
        for (var i = 0; i < TestDataPointCount; i++)
        {
            points.Add(new StreamDataPoint
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = ckTypeId,
                Timestamp = TestDataStartTime.AddMinutes(i * 15),
                RtWellKnownName = $"Sensor{i:D3}",
                Attributes = new Dictionary<string, object?>
                {
                    ["SerialNumber"] = $"SN-{i:D3}",
                    ["Temperature"] = 20.0 + i,
                    ["Amount.Value"] = 100.0 + i,
                    ["Amount.Unit"] = "kWh"
                }
            });
        }

        await repo.InsertAsync(ArchiveRtId, points);
        await RefreshArchiveTableAsync();
    }

    /// <summary>
    /// CrateDB applies inserts to the read path asynchronously (~1s). Force a refresh so the
    /// immediately-following query has read-after-write consistency.
    /// </summary>
    private async Task RefreshArchiveTableAsync()
    {
        var qualifiedTable = $"\"{StreamDataTenantId}\".\"archive_{ArchiveRtIdString}\"";
        await using var conn = new NpgsqlConnection(CrateDbConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"REFRESH TABLE {qualifiedTable}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        await base.DisposeServicesAsync();

        if (_crateDbContainer != null)
        {
            await _crateDbContainer.StopAsync();
            await _crateDbContainer.DisposeAsync();
        }
    }

    internal record CrateDbTestConnectionString(string ConnectionString);

    /// <summary>
    /// Single-node CrateDB testcontainer config: 1 shard, 0 replicas (3 shards silently drops rows
    /// on a single node).
    /// </summary>
    internal class TestStreamDataConfiguration(CrateDbTestConnectionString testConnection)
        : IConfigureNamedOptions<StreamDataConfiguration>
    {
        public void Configure(StreamDataConfiguration options) =>
            Configure(Microsoft.Extensions.Options.Options.DefaultName, options);

        public void Configure(string? name, StreamDataConfiguration options)
        {
            options.ConnectionString = testConnection.ConnectionString;
            options.NumberOfShards = 1;
            options.NumberOfReplicas = 0;
        }
    }
}
