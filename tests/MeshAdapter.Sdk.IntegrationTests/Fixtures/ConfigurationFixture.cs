using MeshAdapter.Sdk.IntegrationTests.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that provides configuration management for integration tests.
/// </summary>
public abstract class ConfigurationFixture : ServiceCollectionFixture
{
    private readonly IntegrationTestConfiguration _configuration;

    /// <summary>
    /// The system database name used for tests.
    /// </summary>
    public string SystemDatabaseName => "meshadapter_integration_tests";

    protected ConfigurationFixture()
    {
        _configuration = new IntegrationTestConfiguration();

        Services.Configure<IntegrationTestOptions>(options =>
            _configuration.GetSection("integrationTest").Bind(options));
    }

    protected T GetOptions<T>(string sectionName)
    {
        var option = Activator.CreateInstance<T>();
        _configuration.GetSection(sectionName).Bind(option);
        return option;
    }
}
