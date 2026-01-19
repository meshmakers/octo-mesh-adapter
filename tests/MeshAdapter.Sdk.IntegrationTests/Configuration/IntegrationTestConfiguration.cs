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
            .Build();
    }

    public IConfigurationSection GetSection(string key) => _configurationRoot.GetSection(key);
}
