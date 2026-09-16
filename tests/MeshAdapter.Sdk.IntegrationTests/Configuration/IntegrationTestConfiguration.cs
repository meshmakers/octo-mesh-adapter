using Microsoft.Extensions.Configuration;

namespace MeshAdapter.Sdk.IntegrationTests.Configuration;

/// <summary>
/// Configuration provider for integration tests.
/// </summary>
public class IntegrationTestConfiguration
{
    private readonly IConfigurationRoot _configurationRoot;

    public IntegrationTestConfiguration()
    {
        _configurationRoot = new ConfigurationBuilder()
            .AddJsonFile("appsettings.test.json", optional: true)
            // 🔴 So a machine whose Docker VM cannot run the pinned image does not have to edit a
            // committed file to run the suite. Added 2026-09-16 after exactly that happened: the
            // image was the floating "mongo:8.0", which had moved to 8.0.28 — a build that refuses
            // to start on any kernel >= 6.19 (SERVER-121912), and Docker Desktop's VM kernel has
            // reached 7.0.12-linuxkit. The default is a pinned version now
            // (IntegrationTestOptions.MongoDbImage), which is the actual fix; this is the escape
            // hatch for the next time a pinned version meets a host that cannot run it.
            //
            // OCTO_INTEGRATIONTEST__MONGODBIMAGE=mongo:8.0.12
            .AddEnvironmentVariables("OCTO_")
            .Build();
    }

    public IConfigurationSection GetSection(string key) => _configurationRoot.GetSection(key);
}
