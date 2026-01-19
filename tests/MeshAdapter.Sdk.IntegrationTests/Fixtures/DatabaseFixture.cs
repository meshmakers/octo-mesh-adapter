using MeshAdapter.Sdk.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that manages MongoDB Testcontainer for integration tests.
/// </summary>
public class DatabaseFixture : ConfigurationFixture
{
    protected readonly IntegrationTestOptions Options;
    private MongoDbContainer? _mongoDbContainer;
    private readonly bool _useLocalDatabase;

    public DatabaseFixture()
    {
        Options = GetOptions<IntegrationTestOptions>("integrationTest");

        // Check environment variable first, then fall back to config
        var envVar = Environment.GetEnvironmentVariable("USE_LOCAL_MONGODB");
        _useLocalDatabase = !string.IsNullOrEmpty(envVar) &&
                            (envVar.Equals("true", StringComparison.OrdinalIgnoreCase) || envVar == "1")
                            || Options.UseLocalDatabase;
    }

    protected override async Task InitializeServicesAsync()
    {
        string databaseHost;

        if (_useLocalDatabase)
        {
            // Use local MongoDB instance
            databaseHost = Options.LocalDatabaseHost;
            Console.WriteLine($"Using local MongoDB at {databaseHost}");
        }
        else
        {
            // Start MongoDB test container with replica set (required for transactions)
            _mongoDbContainer = new MongoDbBuilder()
                .WithImage(Options.MongoDbImage)
                .WithReplicaSet()
                .WithName($"mongodb-meshadapter-test-{Guid.NewGuid():N}")
                .WithUsername(Options.AdminUser)
                .WithPassword(Options.AdminUserPassword)
                .Build();

            await _mongoDbContainer.StartAsync();

            var mappedPort = _mongoDbContainer.GetMappedPublicPort(27017);
            databaseHost = $"localhost:{mappedPort}";
            Console.WriteLine($"Using Testcontainer MongoDB at {databaseHost}");
        }

        // Configure services with the connection
        Services.Configure<OctoSystemConfiguration>(config =>
        {
            config.SystemDatabaseName = SystemDatabaseName;
            config.DatabaseHost = databaseHost;
            config.AdminUser = Options.AdminUser;
            config.AdminUserPassword = Options.AdminUserPassword;
            config.DatabaseUserPassword = Options.DatabaseUserPassword;
            config.UseDirectConnection = _useLocalDatabase ? Options.UseDirectConnection : true;
        });

        await base.InitializeServicesAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        if (_mongoDbContainer != null)
        {
            await _mongoDbContainer.StopAsync();
            await _mongoDbContainer.DisposeAsync();
        }
    }
}
