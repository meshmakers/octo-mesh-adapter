using FluentAssertions;
using FluentAssertions.Execution;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog.Extensions.Logging;
using Xunit;

namespace MeshAdapter.Sdk.IntegrationTests.Leasing;

/// <summary>
///     AB#4924 §9.9 / D4 — <b>a lease must carry the work, and the member must run it.</b>
/// </summary>
/// <remarks>
///     <para>
///         Unlike <see cref="LeasedTenantIsolationTests" />, which supplies its own work item in order
///         to prove the isolation mechanism, this suite uses the <b>real</b>
///         <c>LeasedPipelineWorkItem</c> registered by <c>AddOctoMeshAdapterPoolMember()</c>. It grants
///         a real lease carrying a real pipeline configuration and a real input, over a real MongoDB
///         with two real tenant databases, and asserts on what came back on the release.
///     </para>
///     <para>
///         🔴 The pipeline reads the borrowing tenant's own marker entity and declares the whole data
///         root as its result, so one assertion covers three things at once: the named pipeline ran,
///         the given input reached it, and it ran against the leased tenant's data rather than
///         somebody else's.
///     </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class LeasedPipelineWorkItemTests(TwoTenantLeaseFixture fixture) : IClassFixture<TwoTenantLeaseFixture>
{
    private const string LenderTenantId = "leaselender";
    private const string PoolRtId = "665f0000000000000000ee21";
    private const string Issuer = "https://identity.example.com";
    private const string BorrowerSecret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm";

    [Fact]
    public async Task TheMemberRunsThePipelineTheLeaseNamesWithTheInputTheLeaseCarries()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        var output = await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA,
            input: "{\"invoiceNumber\":\"BORROWER-PRIVATE-4711\"}");

        using var _ = new AssertionScope();
        member.HubClient.Releases[^1].Success.Should()
            .BeTrue($"the lease must have completed: {member.HubClient.Releases[^1].StatusMessage}");
        // The input reached the pipeline: it is the data root the orchestrator started from.
        output.Should().Contain("BORROWER-PRIVATE-4711");
        // The pipeline really ran — its transformation added the leased tenant's own data.
        output.Should().Contain(TwoTenantLeaseFixture.MarkerOf(TwoTenantLeaseFixture.TenantA));
        // And only that tenant's.
        output.Should().NotContain(TwoTenantLeaseFixture.MarkerOf(TwoTenantLeaseFixture.TenantB));
    }

    /// <summary>
    ///     🔴 The work item runs against the execution the controller already queued. It reports no
    ///     execution start, so no second entity can appear — the assertion available on this side is
    ///     that the member never opens an adapter-hub report at all, and that what it hands back is
    ///     keyed by the lease rather than by an id of its own making.
    /// </summary>
    [Fact]
    public async Task TheRunIsReportedOnlyThroughTheReleaseOfItsOwnLease()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        var leaseId = await member.LeaseAndRunReturningLeaseIdAsync(TwoTenantLeaseFixture.TenantB);

        using var _ = new AssertionScope();
        member.HubClient.Releases.Should().HaveCount(1);
        member.HubClient.Releases[0].LeaseId.Should().Be(leaseId);
        member.HubClient.Releases[0].Success.Should().BeTrue();
        // The output came home on the release — the only route it has, since a pool member holds a
        // tenant-free management channel and cannot report an execution end.
        member.HubClient.Releases[0].OutputData.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    ///     A lease that names no pipeline is still a valid lease — increment 6's hand-driven grant.
    ///     The member says "nothing to run" and succeeds, rather than failing a lease nobody gave work
    ///     to.
    /// </summary>
    [Fact]
    public async Task ALeaseWithoutWorkIsTakenAndHandedStraightBack()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        await member.LeaseAsync(TwoTenantLeaseFixture.TenantA, pipeline: null, executionId: string.Empty,
            input: null);

        using var _ = new AssertionScope();
        member.HubClient.Releases[^1].Success.Should().BeTrue();
        member.HubClient.Releases[^1].StatusMessage.Should().Contain("no work item");
        member.HubClient.Releases[^1].OutputData.Should().BeNull();
    }

    /// <summary>
    ///     🔴 A lease naming a pipeline but carrying no configuration for it must <b>fail</b>, not
    ///     quietly succeed with nothing done. A member that returned success there would have the
    ///     controller complete the borrower's execution as <c>Completed</c> while the pipeline never
    ///     ran.
    /// </summary>
    [Fact]
    public async Task ALeaseNamingAPipelineItDoesNotCarryFailsRatherThanDoingNothing()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        await member.LeaseAsync(TwoTenantLeaseFixture.TenantA, pipeline: null,
            executionId: Guid.NewGuid().ToString(), input: null, pipelineRtIdOverride: "665f0000000000000000ee99");

        using var _ = new AssertionScope();
        member.HubClient.Releases[^1].Success.Should().BeFalse();
        member.HubClient.Releases[^1].StatusMessage.Should().Contain("665f0000000000000000ee99");
    }

    /// <summary>
    ///     🔴 The execution id has to be the one the controller queued. An unparsable one is a refusal,
    ///     because the alternative — invent a Guid and run anyway — is exactly how a second execution
    ///     entity would come into existence.
    /// </summary>
    [Fact]
    public async Task AnExecutionIdThatIsNotAGuidFailsTheLeaseInsteadOfInventingOne()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA, executionId: "not-a-guid");

        using var _ = new AssertionScope();
        member.HubClient.Releases[^1].Success.Should().BeFalse();
        member.HubClient.Releases[^1].StatusMessage.Should().Contain("not a GUID");
    }

    /// <summary>
    ///     A pool member composed the real way, with the real work item.
    /// </summary>
    private sealed class PoolMember : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private readonly TwoTenantLeaseFixture _fixture;

        private PoolMember(TwoTenantLeaseFixture fixture, ServiceProvider services,
            LeasedTenantIsolationTests.RecordingHubClient hubClient,
            LeasedTenantIsolationTests.FakeIdentityService identity)
        {
            _fixture = fixture;
            _services = services;
            HubClient = hubClient;
            Identity = identity;
        }

        public LeasedTenantIsolationTests.RecordingHubClient HubClient { get; }
        private LeasedTenantIsolationTests.FakeIdentityService Identity { get; }

        public static async Task<PoolMember> CreateAsync(TwoTenantLeaseFixture fixture)
        {
            var identity = new LeasedTenantIsolationTests.FakeIdentityService();
            var services = new ServiceCollection();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddNLog();
            });

            var systemConfiguration = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
            services.Configure<OctoSystemConfiguration>(c =>
            {
                c.SystemDatabaseName = systemConfiguration.SystemDatabaseName;
                c.DatabaseHost = systemConfiguration.DatabaseHost;
                c.AdminUser = systemConfiguration.AdminUser;
                c.AdminUserPassword = systemConfiguration.AdminUserPassword;
                c.DatabaseUserPassword = systemConfiguration.DatabaseUserPassword;
                c.UseDirectConnection = systemConfiguration.UseDirectConnection;
            });

            services.AddCkModelSystemV2();
            services.AddCkModelMeshAdapterIntegrationTestV1();
            services.AddRuntimeEngine().AddMongoDbRuntimeRepository();

            var hubClient = new LeasedTenantIsolationTests.RecordingHubClient();
            services.AddSingleton<IAdapterPoolHubClient>(hubClient);
            services.AddHttpClient(string.Empty)
                .ConfigurePrimaryHttpMessageHandler(() => identity);

            services.Configure<AdapterOptions>(o => o.IssuerUri = Issuer);
            services.Configure<AdapterPoolMemberOptions>(o =>
            {
                o.PoolTenantId = LenderTenantId;
                o.PoolRtId = PoolRtId;
                o.MemberId = "octo-pool-workitem-0";
            });

            // 🔴 No IAdapterLeaseWorkItem override: the real LeasedPipelineWorkItem registered by this
            // call is the thing under test.
            services.AddOctoMeshAdapterPoolMember();

            services.AddDataPipeline()
                .RegisterEtlContext<IMeshEtlContext>()
                .RegisterNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>()
                .RegisterNode<GetRtEntitiesByTypeNode>()
                // SetPipelineExecutionResult@1 is built in — AddDataPipeline() already registers it,
                // and registering it again throws on the duplicate node name.
                .RegisterTriggerNode<LeasedTenantIsolationTests.LeaseProbeTriggerNode>();

            // 🔴 AFTER AddDataPipeline(), which registers the SDK's DefaultContextCreatorService with a
            // plain AddSingleton — last registration wins. A real mesh-adapter host has the same
            // ordering inside AddOctoMeshAdapter(); a pool-member composition has to reproduce it, or
            // every lease fails with "Etl context type mismatch. Expected IMeshEtlContext".
            services.AddSingleton<IContextCreatorService, MeshContextCreatorService>();

            var provider = services.BuildServiceProvider();
            await Task.CompletedTask;
            return new PoolMember(fixture, provider, hubClient, identity);
        }

        /// <summary>
        ///     The pipeline the lease carries: read the leased tenant's marker entities, then declare
        ///     the whole data root — input included — as the execution result.
        /// </summary>
        private async Task<PipelineConfigurationDto> BuildPipelineAsync()
        {
            var definition = new NodeDefinitionRoot
            {
                Triggers = [new LeasedTenantIsolationTests.LeaseProbeTriggerNodeConfiguration()],
                Transformations =
                [
                    new GetRtEntitiesByTypeNodeConfiguration
                    {
                        CkTypeId = TwoTenantLeaseFixture.SensorReadingCkTypeId,
                        TargetPath = "$.readings",
                        Identity = NodeExecutionIdentity.System
                    },
                    new SetPipelineExecutionResultNodeConfiguration { Path = "$" }
                ]
            };

            var nodeConfiguration = await _services.GetRequiredService<IPipelineConfigurationSerializer>()
                .SerializeAsync(definition);

            return new PipelineConfigurationDto(OctoObjectId.GenerateNewId(),
                new RtEntityId("System.Communication/Pipeline", new OctoObjectId("665f0000000000000000ee31")),
                isDebuggingEnabled: false,
                nodeConfiguration: nodeConfiguration,
                configurations: []);
        }

        public async Task<string> LeaseAndRunAsync(string tenantId, string? input = null,
            string? executionId = null)
        {
            var pipeline = await BuildPipelineAsync();
            await LeaseAsync(tenantId, pipeline, executionId ?? Guid.NewGuid().ToString(), input);
            return HubClient.Releases[^1].OutputData ?? string.Empty;
        }

        public async Task<string> LeaseAndRunReturningLeaseIdAsync(string tenantId)
        {
            var pipeline = await BuildPipelineAsync();
            return await LeaseAsync(tenantId, pipeline, Guid.NewGuid().ToString(), input: null);
        }

        public async Task<string> LeaseAsync(string tenantId, PipelineConfigurationDto? pipeline,
            string executionId, string? input, string? pipelineRtIdOverride = null)
        {
            var client = _services.GetRequiredService<AdapterPoolClient>();
            Identity.NextTenantId = tenantId;
            var leaseId = Guid.NewGuid().ToString("N");

            await client.LeaseAsync(new LeaseDto
            {
                LeaseId = leaseId,
                TenantId = tenantId,
                PoolTenantId = LenderTenantId,
                PoolRtId = PoolRtId,
                AdapterRtId = "665f0000000000000000ee22",
                AdapterCkTypeId = "System.Communication/Adapter",
                ExecutionId = executionId,
                PipelineRtId = pipelineRtIdOverride
                               ?? pipeline?.PipelineRtEntityId.RtId.ToString()
                               ?? string.Empty,
                PipelineInput = input,
                Pipeline = pipeline,
                ClientId = $"octo-pipeline-sa-{tenantId}",
                ClientSecret = BorrowerSecret,
                // 🔴 AB#4924 — the lease carries the borrower's DATABASE credential too, and a member
                // refuses a lease without one. Filling it here is not test scaffolding: it is what the
                // controller does, and a lease built without it is not a lease any controller grants.
                DatabaseName = TwoTenantLeaseFixture.DatabaseNameOf(tenantId),
                DatabaseUser = _fixture.DatabaseUserOf(tenantId),
                DatabasePassword = _fixture.InstallationDatabasePassword,
                GrantedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
            });

            return leaseId;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
        }
    }
}
