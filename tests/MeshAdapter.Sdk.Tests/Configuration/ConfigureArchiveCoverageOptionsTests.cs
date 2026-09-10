using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.Tests.Configuration;

/// <summary>
/// AB#5157: the adapter binds the archive-coverage TTL off its own <c>Adapter</c> section rather than
/// the platform's <c>StreamData:Coverage</c> one. Without this binding the host setting would not
/// reach the engine at all and the adapter would silently keep the engine default — a failure mode
/// nothing else would surface, since a wrong TTL only shows up as coverage answers going stale.
/// </summary>
public class ConfigureArchiveCoverageOptionsTests
{
    private static ArchiveCoverageOptions Configure(MeshAdapterConfiguration configuration)
    {
        var options = new ArchiveCoverageOptions();
        new ConfigureArchiveCoverageOptions(Options.Create(configuration)).Configure(options);

        return options;
    }

    [Fact]
    public void Configuration_MemoisesCoverageForAMinuteByDefault()
    {
        Assert.Equal(60, new MeshAdapterConfiguration().StreamDataCoverageCacheTtlSeconds);
    }

    [Fact]
    public void ConfiguredTtl_ReachesTheEngineOptions()
    {
        var options = Configure(new MeshAdapterConfiguration { StreamDataCoverageCacheTtlSeconds = 300 });

        Assert.Equal(300, options.CacheTtlSeconds);
    }

    [Fact]
    public void ZeroTtl_IsPassedThroughSoMemoisationCanBeTurnedOff()
    {
        var options = Configure(new MeshAdapterConfiguration { StreamDataCoverageCacheTtlSeconds = 0 });

        Assert.Equal(0, options.CacheTtlSeconds);
    }

    /// <remarks>
    /// Deliberately not corrected here: the cache rejects a negative TTL where it is built, naming the
    /// setting. Reverting to the default at this point would hide the misconfiguration instead.
    /// </remarks>
    [Fact]
    public void NegativeTtl_IsPassedThroughRatherThanQuietlyReset()
    {
        var options = Configure(new MeshAdapterConfiguration { StreamDataCoverageCacheTtlSeconds = -1 });

        Assert.Equal(-1, options.CacheTtlSeconds);
    }

    [Fact]
    public void NamedConfigure_BehavesLikeTheDefaultOne()
    {
        var options = new ArchiveCoverageOptions();
        new ConfigureArchiveCoverageOptions(
                Options.Create(new MeshAdapterConfiguration { StreamDataCoverageCacheTtlSeconds = 120 }))
            .Configure("any-name", options);

        Assert.Equal(120, options.CacheTtlSeconds);
    }
}
