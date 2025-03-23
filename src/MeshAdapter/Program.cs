using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Services.Observability;

var adapterBuilder = new WebAdapterBuilder();

await adapterBuilder.RunAsync(args, builder =>
{
    // Define the configuration for the adapter
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.Configure<MeshAdapterConfiguration>(options =>
        builder.Configuration.GetSection("Adapter").Bind(options));

    // Add services to the container.

    // Add observability to the adapter
    builder.AddObservability()
        .AddSystemContextHealthCheck();

    // Add the adapter service to startup and shutdown the adapter
    builder.Services.AddSingleton<IAdapterService, MeshAdapterService>();

    // Add mesh adapter nodes and services to the container
    builder.Services.AddOctoMeshAdapter();

}, app =>
{
    app.MapObservability();
    app.UseOctoMeshAdapter();
});