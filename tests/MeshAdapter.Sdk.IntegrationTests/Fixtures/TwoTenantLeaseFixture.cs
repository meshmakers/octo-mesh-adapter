using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MeshAdapter.Sdk.IntegrationTests.Fixtures;

/// <summary>
///     Two real tenants in one real MongoDB, each holding the same CK type with a <b>different</b>
///     marker value — the substrate of the AB#4924 increment 6 isolation suite.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why two databases and not two in-memory fakes.</b> The defect this suite exists to
///         catch is a process that carries something of tenant A into tenant B's execution: a warm CK
///         model, a token, a registration, a cached repository. Every one of those is a real object
///         built from a real connection, and a fake repository would answer correctly by construction
///         — the test would pass with the isolation mechanism removed, which is the one outcome that
///         must not be possible here.
///     </para>
///     <para>
///         Tenant A additionally holds a <see cref="PoisonCanary" />: a value that must never appear
///         in any other tenant's execution output. Cheap, and it fails loudly on a leak nobody
///         predicted.
///     </para>
/// </remarks>
public class TwoTenantLeaseFixture : SystemFixture
{
    /// <summary>The first borrowing tenant.</summary>
    public const string TenantA = "leasetenanta";

    /// <summary>The second borrowing tenant.</summary>
    public const string TenantB = "leasetenantb";

    /// <summary>A third one, so the randomised interleavings have more than two states to visit.</summary>
    public const string TenantC = "leasetenantc";

    /// <summary>CK type the marker entities are instances of.</summary>
    public static readonly RtCkId<CkTypeId> SensorReadingCkTypeId =
        new("MeshAdapterIntegrationTest/SensorReading");

    /// <summary>
    ///     🔴 A value that exists in tenant A's database and in no other. It must never be observed by
    ///     an execution of another tenant, whatever else the test asserts.
    /// </summary>
    public const string PoisonCanary = "POISON-CANARY-NEVER-CROSSES-A-TENANT-BOUNDARY";

    /// <summary>
    ///     🔴 AB#4924 — <b>the tenant whose datasource password is not the installation's.</b>
    /// </summary>
    /// <remarks>
    ///     Its database user's password is rotated to <see cref="RotatedDatabasePassword" /> after the
    ///     tenant is created and seeded. A pool member configured with the installation-wide password —
    ///     which is what every other tenant here opens with, and what a member needs in order to read
    ///     the tenant registry at all — therefore <b>cannot</b> open this tenant's database. Only the
    ///     credential the lease carries can.
    ///     <para>
    ///         That is what turns "the lease has a database user on it" into "the lease is what opened
    ///         the database". Without the rotation the member would succeed either way and the test
    ///         would pass with the whole mechanism removed. It is also a preview of AB#5255, where
    ///         every database's password is its own.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT a member of <see cref="Tenants" />: the isolation suite leases those
    ///         with the installation credential, and adding an unopenable one to that list would fail
    ///         those tests for a reason they are not about.
    ///     </para>
    /// </remarks>
    public const string RotatedTenant = "leasetenantrot";

    /// <summary>The password <see cref="RotatedTenant" />'s datasource user actually has.</summary>
    public const string RotatedDatabasePassword = "rotated-3fPq9Zx2Kd7Wm1Ln6Tr8Yb0Vc5Hs4Ja";

    /// <summary>Every tenant this fixture creates and leases with the installation credential.</summary>
    public static IReadOnlyList<string> Tenants { get; } = [TenantA, TenantB, TenantC];

    /// <summary>The database name this fixture gives a tenant — the same one the tenant record holds.</summary>
    public static string DatabaseNameOf(string tenantId) => $"db{tenantId}";

    /// <summary>
    ///     The datasource user of a tenant's database, formatted the way the engine formats it when it
    ///     creates the user. This is the CONTROLLER's job in production; the fixture stands in for it.
    /// </summary>
    public string DatabaseUserOf(string tenantId) =>
        string.Format(SystemConfiguration.DatabaseUser, DatabaseNameOf(tenantId));

    /// <summary>The installation-wide datasource password — what every tenant but the rotated one has.</summary>
    public string InstallationDatabasePassword => SystemConfiguration.DatabaseUserPassword!;

    private OctoSystemConfiguration SystemConfiguration =>
        GetService<IOptions<OctoSystemConfiguration>>().Value;

    /// <summary>The marker only tenant <paramref name="tenantId" />'s database holds.</summary>
    public static string MarkerOf(string tenantId) => $"MARKER-OF-{tenantId.ToUpperInvariant()}";

    public TwoTenantLeaseFixture()
    {
        // Registered before the provider is built; the base constructor already added the runtime
        // engine and System v2.
        Services.AddCkModelMeshAdapterIntegrationTestV1();
    }

    protected override async Task InitializeServicesAsync()
    {
        // Builds the provider and creates the system tenant (which auto-imports the System model).
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var ckCacheService = GetService<ICkCacheService>();

        foreach (var tenantId in Tenants)
        {
            using var adminSession = await systemContext.GetAdminSessionAsync();
            if (await systemContext.IsChildTenantExistingAsync(adminSession, tenantId))
            {
                await systemContext.DropChildTenantAsync(adminSession, tenantId);
            }

            // A child of the system tenant — which is also the shape a real lending relationship
            // takes: a pool lends downwards into its own subtree (concept §3).
            await systemContext.CreateChildTenantAsync(adminSession, $"db{tenantId}", tenantId);

            var operationResult = new OperationResult();
            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
            await tenantContext.ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"),
                operationResult);

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw new InvalidOperationException(
                    $"Failed to import the test CK model into tenant '{tenantId}': " +
                    string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
            }

            await SeedMarkerAsync(systemContext, ckCacheService, tenantId);
        }

        await CreateRotatedCredentialTenantAsync(systemContext, ckCacheService);

        // 🔴 Leave the caches cold. A fixture that left every tenant's model loaded would hide
        // exactly the state the post-release assertions are about.
        foreach (var tenantId in Tenants.Append(RotatedTenant).Where(ckCacheService.IsTenantLoaded))
        {
            ckCacheService.Unload(tenantId);
        }
    }

    /// <summary>
    ///     Creates <see cref="RotatedTenant" /> like any other, seeds it, and then changes its
    ///     datasource user's password so that the installation-wide one no longer opens it.
    /// </summary>
    /// <remarks>
    ///     🔴 The order matters: seed first, rotate second. The seeding runs on the fixture's own
    ///     provider, which authenticates with the installation password — after the rotation it could
    ///     not write a thing.
    /// </remarks>
    private async Task CreateRotatedCredentialTenantAsync(ISystemContext systemContext,
        ICkCacheService ckCacheService)
    {
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            if (await systemContext.IsChildTenantExistingAsync(adminSession, RotatedTenant))
            {
                await systemContext.DropChildTenantAsync(adminSession, RotatedTenant);
            }

            await systemContext.CreateChildTenantAsync(adminSession, DatabaseNameOf(RotatedTenant),
                RotatedTenant);
        }

        var operationResult = new OperationResult();
        var tenantContext = await systemContext.FindTenantContextAsync(RotatedTenant);
        await tenantContext.ImportCkModelAsync(new CkModelId("MeshAdapterIntegrationTest"), operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw new InvalidOperationException(
                $"Failed to import the test CK model into tenant '{RotatedTenant}': " +
                string.Join(", ", operationResult.Messages.Select(m => m.MessageText)));
        }

        await SeedMarkerAsync(systemContext, ckCacheService, RotatedTenant);

        await RotateDatasourcePasswordAsync(DatabaseUserOf(RotatedTenant), RotatedDatabasePassword);
    }

    /// <summary>
    ///     Changes one database user's password, as the MongoDB root user.
    /// </summary>
    /// <remarks>
    ///     Straight to the driver rather than through the engine: the engine creates users and drops
    ///     them, it has no notion of rotating one, because in this installation there is nothing to
    ///     rotate — one password stands behind every user. That is the state AB#5255 changes, and this
    ///     is the fixture standing in for it.
    /// </remarks>
    private async Task RotateDatasourcePasswordAsync(string user, string password)
    {
        var configuration = SystemConfiguration;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(configuration.DatabaseHost),
            Username = configuration.AdminUser,
            Password = configuration.AdminUserPassword,
            AuthenticationSource = configuration.AuthenticationDatabaseName,
            DatabaseName = configuration.AuthenticationDatabaseName,
            DirectConnection = configuration.UseDirectConnection
        };

        var client = new MongoClient(MongoClientSettings.FromUrl(urlBuilder.ToMongoUrl()));
        await client.GetDatabase(configuration.AuthenticationDatabaseName)
            .RunCommandAsync<BsonDocument>(new BsonDocumentCommand<BsonDocument>(
                new BsonDocument { { "updateUser", user }, { "pwd", password } }));
    }

    private static async Task SeedMarkerAsync(ISystemContext systemContext, ICkCacheService ckCacheService,
        string tenantId)
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var entities = new List<EntityUpdateInfo<RtEntity>>();

        var marker = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
        marker.SetAttributeRawValue("SerialNumber", MarkerOf(tenantId));
        entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(marker));

        if (tenantId == TenantA)
        {
            var canary = new RtEntity(SensorReadingCkTypeId, OctoObjectId.GenerateNewId());
            canary.SetAttributeRawValue("SerialNumber", PoisonCanary);
            entities.Add(EntityUpdateInfo<RtEntity>.CreateInsert(canary));
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entities, operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(
                $"Failed to seed the marker of tenant '{tenantId}': {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }
}
