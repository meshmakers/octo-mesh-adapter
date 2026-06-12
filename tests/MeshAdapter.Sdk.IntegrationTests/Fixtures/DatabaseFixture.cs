using MeshAdapter.Sdk.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     Fixture that manages MongoDB Testcontainer for integration tests.
///
///     Container-bringup pattern matches octo-construction-kit-engine-mongodb /
///     octo-ai-services — Testcontainers' rs.initiate() handshake and mongo's keyfile-init
///     entrypoint race with port binding on CI agents under load (build 34386 hung 40+ min
///     in a sibling service due to exit-48 on 27017 inside the entrypoint restart). The
///     retry loop with a *fresh* container per attempt is the proven fix.
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
            // Workaround: .NET 10 introduced a default regex match timeout that causes
            // Testcontainers' MatchImage regex to time out when parsing Docker image names.
            // Set a generous timeout until Testcontainers fixes this upstream.
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(10));

            await Console.Error.WriteLineAsync($"[DatabaseFixture] Starting MongoDB container with image: {Options.MongoDbImage}");
            await Console.Error.FlushAsync();

            const int maxAttempts = 3;
            var perAttemptTimeout = TimeSpan.FromMinutes(2);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await Console.Error.WriteLineAsync($"[DatabaseFixture] StartAsync attempt {attempt}/{maxAttempts}");
                await Console.Error.FlushAsync();

                // No WithCleanUp(true) — Ryuk's TCP handshake blocks silently on the
                // self-hosted DinD agent; DisposeServicesAsync handles cleanup explicitly.
                _mongoDbContainer = new MongoDbBuilder(Options.MongoDbImage)
                    .WithReplicaSet()
                    .WithName($"mongodb-meshadapter-test-{Guid.NewGuid():N}")
                    .WithUsername(Options.AdminUser)
                    .WithPassword(Options.AdminUserPassword)
                    .Build();

                using var startCts = new CancellationTokenSource(perAttemptTimeout);
                try
                {
                    await _mongoDbContainer.StartAsync(startCts.Token);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $@"Testcontainer MongoDB start failed on attempt {attempt}/{maxAttempts}: {ex.GetType().Name}: {ex.Message}");

                    try
                    {
                        await _mongoDbContainer.DisposeAsync();
                    }
                    catch (Exception disposeEx)
                    {
                        Console.WriteLine($@"  Disposal of failed container also threw: {disposeEx.Message}");
                    }

                    _mongoDbContainer = null;

                    if (attempt == maxAttempts)
                    {
                        throw;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            var mappedPort = _mongoDbContainer!.GetMappedPublicPort(27017);
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
