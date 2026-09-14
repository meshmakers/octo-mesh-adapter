using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
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

    // AB#4924 — a process is a pool member when OCTO_ADAPTERPOOL__POOLTENANTID and
    // OCTO_ADAPTERPOOL__POOLRTID are set. The order matters and is not cosmetic:
    // AddOctoMeshAdapterPoolMember() TryAdds the lease-aware IAdapterTenantScope and the lease work
    // item, so it has to win over the registrations AddOctoMeshAdapter() brings; and
    // AddOctoMeshAdapter() has to run afterwards regardless, because it registers
    // MeshContextCreatorService after AddDataPipeline() and a leased execution needs an
    // IMeshEtlContext. Getting either order wrong fails at the first lease, not at startup.
    var poolMemberOptions = new AdapterPoolMemberOptions();
    builder.Configuration.GetSection(AdapterPoolMemberOptions.SectionName).Bind(poolMemberOptions);
    if (poolMemberOptions.IsEnabled)
    {
        builder.Services.AddOctoMeshAdapterPoolMember();
    }

    // Add mesh adapter nodes and services to the container
    builder.Services.AddOctoMeshAdapter();

}, app =>
{
    app.MapObservability();
    app.UseOctoMeshAdapter();
});