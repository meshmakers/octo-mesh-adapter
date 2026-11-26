using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.Tests.Configuration;

/// <summary>
///     AB#9824: the mesh adapter binds the runtime read guard from its configuration (section <c>RuntimeReadGuard</c>,
///     helm <c>runtimeReadGuard.mode</c> / <c>maxTake</c> as <c>OCTO_RUNTIMEREADGUARD__MODE</c> /
///     <c>__MAXTAKE</c>), defaults to Warn / 1000, and a host without <see cref="IConfiguration" /> still resolves the
///     options with the defaults instead of failing every tenant context.
/// </summary>
public class RuntimeReadGuardRegistrationTests
{
    private static ServiceProvider Build(IConfiguration? configuration)
    {
        var services = new ServiceCollection();
        if (configuration != null)
        {
            services.AddSingleton(configuration);
        }

        services.AddLogging();
        services.AddOctoMeshAdapter();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Configuration_BindsModeAndMaxTake()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RuntimeReadGuard:Mode"] = "Off",
                ["RuntimeReadGuard:MaxTake"] = "250"
            })
            .Build();
        using var provider = Build(configuration);

        var options = provider.GetRequiredService<IOptionsMonitor<RuntimeReadGuardOptions>>().CurrentValue;

        Assert.Equal("Off", options.Mode);
        Assert.Equal(250, options.MaxTake);
    }

    [Fact]
    public void WithoutConfiguration_TheDefaultsApply_AndNothingThrows()
    {
        using var provider = Build(new ConfigurationBuilder().Build());
        using var bare = Build(null);

        foreach (var p in new[] { provider, bare })
        {
            var options = p.GetRequiredService<IOptionsMonitor<RuntimeReadGuardOptions>>().CurrentValue;
            Assert.Equal("Warn", options.Mode);
            Assert.Equal(RuntimeReadGuardOptions.DefaultMaxTake, options.MaxTake);
        }
    }
}
