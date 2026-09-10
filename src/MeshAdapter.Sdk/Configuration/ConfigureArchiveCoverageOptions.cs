using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

/// <summary>
/// Binds the archive coverage options (AB#5157) from the adapter's own configuration, next to
/// <see cref="ConfigureStreamDataConfiguration"/>: the platform section is
/// <c>StreamData:Coverage:CacheTtlSeconds</c>, but everything an adapter is configured with lives in
/// its single <c>Adapter</c> section, so the value is read off
/// <see cref="MeshAdapterConfiguration.StreamDataCoverageCacheTtlSeconds"/>. Without this the adapter
/// would silently keep the engine default and the TTL would not be host-configurable here at all.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureArchiveCoverageOptions : IConfigureNamedOptions<ArchiveCoverageOptions>
{
    private readonly IOptions<MeshAdapterConfiguration> _options;

    public ConfigureArchiveCoverageOptions(IOptions<MeshAdapterConfiguration> options)
    {
        _options = options;
    }

    public void Configure(ArchiveCoverageOptions options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, ArchiveCoverageOptions options)
    {
        // Passed through unvalidated on purpose: a negative TTL is rejected where the cache is built,
        // with the section name in the message, rather than being silently reverted to the default here.
        options.CacheTtlSeconds = _options.Value.StreamDataCoverageCacheTtlSeconds;
    }
}
