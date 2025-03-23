using Meshmakers.Octo.Services.StreamData.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
{
    private readonly IOptions<MeshAdapterConfiguration> _options;

    public ConfigureStreamDataConfiguration(IOptions<MeshAdapterConfiguration> options)
    {
        _options = options;
    }
    public void Configure(StreamDataConfiguration options)
    {
        Configure(Options.DefaultName, options);
    }

    public void Configure(string? name, StreamDataConfiguration options)
    {
        var o = _options.Value;

        options.ConnectionStringFromConfiguration(o.StreamDataHost, o.StreamDataUser, o.StreamDataPassword);
    }
}