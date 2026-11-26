using System.Diagnostics.Metrics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     AB#9824: real pipelines (real orchestrator, real mesh adapter nodes, MongoDB) with the runtime read guard of the
///     mesh adapter host (<c>AddRuntimeReadGuard</c>). Adds to <see cref="IdentityCredentialPipelineFixture" /> (a role
///     with one assigned user for the association read) a capture of the guard's structured warnings, a switch for the
///     guard mode and some <c>TestControl</c> entities.
/// </summary>
public class RuntimeReadGuardPipelineFixture : IdentityCredentialPipelineFixture
{
    public const string ControlType = "MeshAdapterIntegrationTest/TestControl";

    public const string ContactType = "MeshAdapterIntegrationTest/SyncedContact";

    public const string ReadingType = "MeshAdapterIntegrationTest/SensorReading";

    /// <summary>A self-reported metering point number that a <see cref="ControlType" /> entity carries as ControlName.</summary>
    public const string MeteringPointNumber = "AT0099980000000000000000000000001";

    public const int ControlCount = 4;

    /// <summary>The guard options every read sees; tests switch <see cref="RuntimeReadGuardOptions.Mode" />.</summary>
    public RuntimeReadGuardOptions GuardOptions { get; } = new();

    /// <summary>Warnings and other entries of the guard's logger, with their structured state.</summary>
    public List<GuardLogEntry> GuardLog { get; } = [];

    protected override async Task InitializeServicesAsync()
    {
        // A closed registration wins over the options monitor AddOctoMeshAdapter binds from configuration.
        Services.AddSingleton<IOptionsMonitor<RuntimeReadGuardOptions>>(new FixedOptionsMonitor(GuardOptions));
        Services.AddLogging(b => b.AddProvider(new GuardLoggerProvider(GuardLog)));

        await base.InitializeServicesAsync();

        var repository = GetSystemContext().GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(GetService<ICkCacheService>());
        var type = new RtCkId<CkTypeId>(ControlType);
        var inserts = new List<EntityUpdateInfo<RtEntity>>();
        for (var i = 0; i < ControlCount; i++)
        {
            var control = await repository.CreateTransientRtEntityByRtCkIdAsync(type);
            control.SetAttributeValue("ControlName", AttributeValueTypesDto.String,
                i == 0 ? MeteringPointNumber : $"control-{i}");
            inserts.Add(EntityUpdateInfo<RtEntity>.CreateInsert(type, control));
        }

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        await repository.ApplyChangesAsync(session, inserts, [], result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            throw new InvalidOperationException("Seeding the controls failed: " +
                                                string.Join(", ", result.Messages.Select(m => m.MessageText)));
        }

        await session.CommitTransactionAsync();
    }

    /// <summary>Stores a <c>System/SimpleRtQuery</c> on <see cref="ControlType" /> (no page size of its own).</summary>
    public async Task<string> CreateControlQueryAsync()
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        var queryType = new RtCkId<CkTypeId>("System/SimpleRtQuery");
        var query = await repository.CreateTransientRtEntityByRtCkIdAsync(queryType);
        query.SetAttributeValue("Name", AttributeValueTypesDto.String, "guard-query-" + Guid.NewGuid().ToString("N")[..8]);
        query.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, ControlType);
        query.SetAttributeValue("Columns", AttributeValueTypesDto.StringArray, new List<string> { "ControlName" });
        return await InsertAsync(queryType, query);
    }

    /// <summary>Stores a <c>System/AggregationRtQuery</c> (Count of ControlName) on <see cref="ControlType" />.</summary>
    public async Task<string> CreateControlAggregationQueryAsync()
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        var queryType = new RtCkId<CkTypeId>("System/AggregationRtQuery");
        var query = await repository.CreateTransientRtEntityByRtCkIdAsync(queryType);
        query.SetAttributeValue("Name", AttributeValueTypesDto.String, "guard-agg-" + Guid.NewGuid().ToString("N")[..8]);
        query.SetAttributeValue("QueryCkTypeId", AttributeValueTypesDto.String, ControlType);
        query.SetAttributeValue("Columns", AttributeValueTypesDto.RecordArray, new List<RtRecord>
        {
            new(new RtCkId<CkRecordId>("System/AggregationQueryColumn"), new Dictionary<string, object?>
            {
                ["AttributePath"] = "ControlName",
                ["AggregationType"] = 0 // Count
            })
        });
        return await InsertAsync(queryType, query);
    }

    private async Task<string> InsertAsync(RtCkId<CkTypeId> type, RtEntity entity)
    {
        var repository = GetSystemContext().GetSystemTenantRepository();
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        await repository.InsertOneRtEntityAsync(session, type, entity);
        await session.CommitTransactionAsync();
        return entity.RtId.ToString();
    }

    /// <summary>The meter factory of this host: only its meters are this fixture's guard.</summary>
    public IMeterFactory MeterFactory => GetService<IMeterFactory>();

    public sealed record GuardLogEntry(LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> State);

    private sealed class FixedOptionsMonitor(RuntimeReadGuardOptions options) : IOptionsMonitor<RuntimeReadGuardOptions>
    {
        public RuntimeReadGuardOptions CurrentValue => options;

        public RuntimeReadGuardOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RuntimeReadGuardOptions, string?> listener) => null;
    }

    private sealed class GuardLoggerProvider(List<GuardLogEntry> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.EndsWith(".RuntimeReadGuard", StringComparison.Ordinal) ? new GuardLogger(sink) : NullLogger;

        public void Dispose()
        {
        }

        private static readonly ILogger NullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        private sealed class GuardLogger(List<GuardLogEntry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var dict = new Dictionary<string, object?>();
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        dict[pair.Key] = pair.Value;
                    }
                }

                lock (sink)
                {
                    sink.Add(new GuardLogEntry(logLevel, eventId, dict));
                }
            }
        }
    }
}
