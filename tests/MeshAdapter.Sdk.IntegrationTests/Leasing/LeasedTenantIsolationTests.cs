using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using FluentAssertions.Execution;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog.Extensions.Logging;

namespace MeshAdapter.Sdk.IntegrationTests.Leasing;

/// <summary>
///     AB#4924 increment 6 — <b>the entry criteria the implementation plan §5.5 could not write in
///     increment 3</b>, now that a lease can be granted.
/// </summary>
/// <remarks>
///     <para>
///         🔴 These are the tests that stand between a lease and a cross-tenant data incident. Each one
///         drives a <b>real</b> lease through the real <see cref="AdapterPoolClient" /> and the real
///         mesh-adapter lease participants, executes a <b>real</b> pipeline through the real
///         orchestrator against a <b>real</b> MongoDB holding two real tenant databases, and asserts on
///         the <b>pipeline output</b> — not on internal state. A cache that leaks shows up exactly
///         there.
///     </para>
///     <para>
///         🔴 <b>The work item resolves its tenant from <see cref="IAdapterTenantScope" />, never from
///         the lease it was handed.</b> That is what makes these tests prove the isolation mechanism
///         rather than prove that passing the right argument produces the right answer — and it is also
///         exactly what every service around the node layer does since increment 3.
///     </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class LeasedTenantIsolationTests(TwoTenantLeaseFixture fixture) : IClassFixture<TwoTenantLeaseFixture>
{
    private const string LenderTenantId = "leaselender";
    private const string PoolRtId = "665f0000000000000000ee21";
    private const string Issuer = "https://identity.example.com";
    private const string BorrowerSecret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm";

    // ---------------------------------------------------------------------------------------
    // 1 + 2 — the two-tenant interleave on pipeline output, and the poison canary in that form.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 Lease A, run, release, lease B, run, release — and each execution's <b>pipeline output</b>
    ///     carries only its own tenant's marker. Then the reverse order, and A twice in a row.
    /// </summary>
    [Fact]
    public async Task ConsecutiveLeasesEachSeeOnlyTheirOwnTenantsData()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        var sequence = new[]
        {
            TwoTenantLeaseFixture.TenantA, TwoTenantLeaseFixture.TenantB,
            TwoTenantLeaseFixture.TenantB, TwoTenantLeaseFixture.TenantA,
            TwoTenantLeaseFixture.TenantA, TwoTenantLeaseFixture.TenantC
        };

        foreach (var tenantId in sequence)
        {
            var output = await member.LeaseAndRunAsync(tenantId);

            output.Should().Contain(TwoTenantLeaseFixture.MarkerOf(tenantId),
                $"the execution of tenant '{tenantId}' must see its own marker");

            foreach (var other in TwoTenantLeaseFixture.Tenants.Where(t => t != tenantId))
            {
                output.Should().NotContain(TwoTenantLeaseFixture.MarkerOf(other),
                    $"the execution of tenant '{tenantId}' must not see tenant '{other}'s marker");
            }
        }
    }

    /// <summary>
    ///     🔴 The poison canary. One value exists in tenant A's database and nowhere else; no other
    ///     tenant's execution may ever observe it. Cheap, and it fails loudly on a leak nobody
    ///     predicted.
    /// </summary>
    [Fact]
    public async Task ThePoisonCanaryNeverCrossesATenantBoundary()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        // Warm the member with tenant A first — a leak needs something to leak.
        var aOutput = await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA);
        aOutput.Should().Contain(TwoTenantLeaseFixture.PoisonCanary,
            "the canary must really be in tenant A's database, or this test proves nothing");

        foreach (var tenantId in new[] { TwoTenantLeaseFixture.TenantB, TwoTenantLeaseFixture.TenantC })
        {
            var output = await member.LeaseAndRunAsync(tenantId);
            output.Should().NotContain(TwoTenantLeaseFixture.PoisonCanary);
        }
    }

    /// <summary>
    ///     Randomised interleavings. A race that shows up once in fifty leases is exactly the failure
    ///     mode of a shared process, and a fixed sequence would never find it.
    /// </summary>
    [Fact]
    public async Task RandomisedInterleavingsHold()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        var random = new Random(4924);
        for (var i = 0; i < 30; i++)
        {
            var tenantId = TwoTenantLeaseFixture.Tenants[random.Next(TwoTenantLeaseFixture.Tenants.Count)];
            var output = await member.LeaseAndRunAsync(tenantId);

            output.Should().Contain(TwoTenantLeaseFixture.MarkerOf(tenantId));
            foreach (var other in TwoTenantLeaseFixture.Tenants.Where(t => t != tenantId))
            {
                output.Should().NotContain(TwoTenantLeaseFixture.MarkerOf(other));
            }

            if (tenantId != TwoTenantLeaseFixture.TenantA)
            {
                output.Should().NotContain(TwoTenantLeaseFixture.PoisonCanary);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // 3 — the post-release state assertion.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 After a release the process must hold <b>nothing</b> of the tenant it just served: the CK
    ///     model unloaded, the token holder empty, the lease scope left, no pipeline registration
    ///     behind. Concept §4's invariant, item by item.
    /// </summary>
    [Fact]
    public async Task AfterAReleaseTheProcessRetainsNothingOfTheReleasedTenant()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA);

        var ckCacheService = member.Services.GetRequiredService<ICkCacheService>();
        var accessToken = member.Services.GetRequiredService<IServiceClientAccessToken>();
        var leaseScope = member.Services.GetRequiredService<IAdapterLeaseScope>();
        var registry = member.Services.GetRequiredService<IPipelineRegistryService>();

        using var _ = new AssertionScope();
        ckCacheService.IsTenantLoaded(TwoTenantLeaseFixture.TenantA).Should().BeFalse(
            "the released tenant's CK model must not stay warm in a process about to serve another one");
        string.IsNullOrEmpty(accessToken.AccessToken).Should().BeTrue(
            "the member must present no credential at all between leases");
        leaseScope.HasLease.Should().BeFalse();
        leaseScope.HasTenant.Should().BeFalse();
        registry.GetRegisteredPipelines(TwoTenantLeaseFixture.TenantA).Should().BeEmpty(
            "a registration carries the borrower's GlobalConfiguration, credentials included");
    }

    /// <summary>
    ///     And the invariant's resting state, stated directly: outside a lease the process has no
    ///     tenant, and asking for one throws rather than answering plausibly.
    /// </summary>
    [Fact]
    public async Task BetweenLeasesReadingTheTenantThrows()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);
        await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA);

        var leaseScope = member.Services.GetRequiredService<IAdapterLeaseScope>();

        var act = () => leaseScope.TenantId;
        act.Should().Throw<InvalidOperationException>();
    }

    // ---------------------------------------------------------------------------------------
    // 4 — the log-target assertion.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 Tenant B's <b>rendered</b> execution log must contain no occurrence of tenant A's id. A
    ///     log line naming the wrong tenant is how a cross-tenant execution looks to whoever is
    ///     debugging it at 3 a.m. — plausible, and wrong.
    /// </summary>
    [Fact]
    public async Task TenantBsRenderedExecutionLogNeverNamesTenantA()
    {
        fixture.EnsureInitialized();

        var memoryTarget = new NLog.Targets.MemoryTarget("leased-execution-log-probe")
        {
            Layout = "${level}|${logger}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await using var member = await PoolMember.CreateAsync(fixture);

            // Tenant A first, so there is something to leak; then clear the probe and run B.
            await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantA);
            memoryTarget.Logs.Clear();

            var output = await member.LeaseAndRunAsync(TwoTenantLeaseFixture.TenantB);

            using var _ = new AssertionScope();
            // The run really did log — otherwise the probe proves nothing.
            memoryTarget.Logs.Should().NotBeEmpty();
            output.Should().Contain(TwoTenantLeaseFixture.MarkerOf(TwoTenantLeaseFixture.TenantB));
            memoryTarget.Logs.Should().NotContain(
                l => l.Contains(TwoTenantLeaseFixture.TenantA, StringComparison.OrdinalIgnoreCase));
            memoryTarget.Logs.Should().NotContain(
                l => l.Contains(TwoTenantLeaseFixture.PoisonCanary, StringComparison.Ordinal));
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    // ---------------------------------------------------------------------------------------
    // 5 — the identity assertion, inside a real lease.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 The token presented <b>during B's lease</b> carries <c>tenant_id=B</c> — not merely "a
    ///     token exists". Since AB#5077 a request that lost its <c>acr_values</c> returns a perfectly
    ///     valid token for the system tenant, and every "did we get a token" assertion would be green.
    /// </summary>
    [Fact]
    public async Task TheTokenPresentedDuringALeaseBelongsToTheLeasedTenant()
    {
        fixture.EnsureInitialized();
        await using var member = await PoolMember.CreateAsync(fixture);

        foreach (var tenantId in new[] { TwoTenantLeaseFixture.TenantA, TwoTenantLeaseFixture.TenantB })
        {
            await member.LeaseAndRunAsync(tenantId);

            member.TokenTenantObservedDuringLease.Should().Be(tenantId,
                "the member must act as the borrower, never as itself and never as the system tenant");
            member.Identity.LastAcrValues.Should().Be($"tenant:{tenantId}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // The pool member under test.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     One real pool-member process: the real SDK composition, the real mesh-adapter lease
    ///     participants, a fake identity service and a fake management connection.
    /// </summary>
    private sealed class PoolMember : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private PoolMember(ServiceProvider services, RecordingHubClient hubClient,
            FakeIdentityService identity, MarkerReadingWorkItem workItem)
        {
            _services = services;
            HubClient = hubClient;
            Identity = identity;
            WorkItem = workItem;
        }

        public IServiceProvider Services => _services;
        public RecordingHubClient HubClient { get; }
        public FakeIdentityService Identity { get; }
        public MarkerReadingWorkItem WorkItem { get; }

        /// <summary>The <c>tenant_id</c> of the token the member held while the last work item ran.</summary>
        public string? TokenTenantObservedDuringLease => WorkItem.TokenTenantDuringRun;

        public static Task<PoolMember> CreateAsync(TwoTenantLeaseFixture fixture)
        {
            var identity = new FakeIdentityService();
            var services = new ServiceCollection();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddNLog();
            });

            // Same MongoDB the fixture seeded.
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

            // The fake identity service the borrower credential is exchanged at, and the fake
            // management connection the release is reported on. Registered BEFORE the pool
            // composition so its TryAdd registrations do not overwrite them.
            var hubClient = new RecordingHubClient();
            services.AddSingleton<IAdapterPoolHubClient>(hubClient);
            services.AddHttpClient(string.Empty)
                .ConfigurePrimaryHttpMessageHandler(() => identity);

            services.Configure<AdapterOptions>(o => o.IssuerUri = Issuer);
            services.Configure<AdapterPoolMemberOptions>(o =>
            {
                o.PoolTenantId = LenderTenantId;
                o.PoolRtId = PoolRtId;
                o.MemberId = "octo-pool-integration-0";
            });

            // 🔴 Before AddDataPipeline: the pipeline registration uses TryAddSingleton for
            // IAdapterTenantScope, and on a pool member the winner must be the lease-aware scope.
            services.AddOctoMeshAdapterPoolMember();

            var workItem = new MarkerReadingWorkItem();
            services.AddSingleton<IAdapterLeaseWorkItem>(workItem);

            services.AddDataPipeline()
                .RegisterEtlContext<IMeshEtlContext>()
                .RegisterNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>()
                .RegisterNode<GetRtEntitiesByTypeNode>()
                .RegisterTriggerNode<LeaseProbeTriggerNode>();

            var provider = services.BuildServiceProvider();
            workItem.Bind(provider);

            return Task.FromResult(new PoolMember(provider, hubClient, identity, workItem));
        }

        /// <summary>
        ///     Grants one lease for <paramref name="tenantId" />, lets the member run it, and returns
        ///     the pipeline output.
        /// </summary>
        public async Task<string> LeaseAndRunAsync(string tenantId)
        {
            var client = _services.GetRequiredService<AdapterPoolClient>();
            Identity.NextTenantId = tenantId;

            await client.LeaseAsync(new LeaseDto
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                PoolTenantId = LenderTenantId,
                PoolRtId = PoolRtId,
                AdapterRtId = "665f0000000000000000ee22",
                AdapterCkTypeId = "System.Communication/Adapter",
                ClientId = $"octo-pipeline-sa-{tenantId}",
                ClientSecret = BorrowerSecret,
                GrantedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
            });

            var release = HubClient.Releases[^1];
            release.Success.Should().BeTrue(
                $"the lease of tenant '{tenantId}' must have completed: {release.StatusMessage}");

            return WorkItem.LastOutput ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
        }
    }

    /// <summary>
    ///     Runs a real pipeline for the tenant the <b>scope</b> says is current.
    /// </summary>
    /// <remarks>
    ///     🔴 It deliberately never reads <see cref="LeaseDto.TenantId" />. A work item that took the
    ///     tenant from its parameter would make every assertion in this file a tautology: of course
    ///     passing the right tenant yields the right data. Reading it from
    ///     <see cref="IAdapterTenantScope" /> is both what the isolation mechanism is for and what
    ///     every service around the node layer does since increment 3.
    /// </remarks>
    private sealed class MarkerReadingWorkItem : IAdapterLeaseWorkItem
    {
        private IServiceProvider? _services;

        public string? LastOutput { get; private set; }

        /// <summary>The <c>tenant_id</c> of the token the process held while the work item ran.</summary>
        public string? TokenTenantDuringRun { get; private set; }

        public void Bind(IServiceProvider services) => _services = services;

        public async Task<LeaseWorkOutcome> RunAsync(LeaseDto lease, CancellationToken cancellationToken)
        {
            var services = _services!;
            var tenantId = services.GetRequiredService<IAdapterTenantScope>().TenantId;

            TokenTenantDuringRun =
                JwtPayloadReader.TryRead(services.GetRequiredService<IServiceClientAccessToken>().AccessToken,
                    out var claims)
                    ? claims.TenantId
                    : null;

            var systemContext = services.GetRequiredService<ISystemContext>();
            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);

            IMeshEtlContext etlContext = new MeshEtlContext(tenantId, tenantRepository,
                OctoObjectId.GenerateNewId(), Guid.NewGuid(),
                new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId()),
                DateTime.UtcNow, null,
                A.Fake<IGlobalConfiguration>(),
                new Dictionary<string, object?>());

            var pipeline = new NodeDefinitionRoot
            {
                Transformations = new List<NodeConfiguration>
                {
                    new GetRtEntitiesByTypeNodeConfiguration
                    {
                        CkTypeId = TwoTenantLeaseFixture.SensorReadingCkTypeId,
                        TargetPath = "$.readings",
                        Identity = NodeExecutionIdentity.System
                    }
                }
            };

            var orchestrator = services.GetRequiredService<IEtlDataOrchestrator>();
            var result = await orchestrator.ExecutePipelineAsync(pipeline, etlContext,
                value: JsonNode.Parse("{}"));

            // 🔴 Register the borrower's pipeline, exactly as a real lease would: a registration
            // carries the pipeline's GlobalConfiguration, which is where the borrower's credentials
            // live once a pipeline is deployed (AB#5027). Without this the post-release assertion
            // about registrations would be vacuously true - an empty registry stays empty whether or
            // not anything drops it.
            var registeredPipeline = new NodeDefinitionRoot
            {
                Triggers = new List<TriggerNodeConfiguration> { new LeaseProbeTriggerNodeConfiguration() },
                Transformations = new List<NodeConfiguration>()
            };
            var nodeConfiguration = await services
                .GetRequiredService<IPipelineConfigurationSerializer>()
                .SerializeAsync(registeredPipeline);

            await services.GetRequiredService<IPipelineRegistryService>().RegisterPipelineAsync(tenantId,
                new PipelineConfigurationDto(OctoObjectId.GenerateNewId(),
                    new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId()),
                    isDebuggingEnabled: false,
                    nodeConfiguration: nodeConfiguration,
                    configurations: []));

            LastOutput = result?.ToString() ?? string.Empty;
            return LeaseWorkOutcome.Succeeded();
        }
    }

    /// <summary>
    ///     A trigger that does nothing. A pipeline registration requires a trigger, and every real one
    ///     subscribes to something — a bus exchange, a timer, an HTTP route — which this suite has no
    ///     business standing up. What matters here is that a registration EXISTS for the leased tenant
    ///     and is gone after the release.
    /// </summary>
    [NodeName("LeaseProbeTrigger", 1)]
    internal record LeaseProbeTriggerNodeConfiguration : TriggerNodeConfiguration;

    [NodeConfiguration(typeof(LeaseProbeTriggerNodeConfiguration))]
    internal sealed class LeaseProbeTriggerNode : ITriggerPipelineNode
    {
        public Task StartAsync(ITriggerContext context) => Task.CompletedTask;

        public Task StopAsync(ITriggerContext context) => Task.CompletedTask;
    }

    /// <summary>
    ///     A fake identity service that issues a token for whichever tenant the request's
    ///     <c>acr_values</c> named — the same behaviour the real one has, which is what makes the
    ///     identity assertion meaningful.
    /// </summary>
    // internal, not private: AB#4924 §9.9 / D4's LeasedPipelineWorkItemTests composes a pool
    // member the same way and must not fork a second copy of these two fakes.
    internal sealed class FakeIdentityService : HttpMessageHandler
    {
        /// <summary>The tenant the next token is expected to be requested for.</summary>
        public string? NextTenantId { get; set; }

        /// <summary>The <c>acr_values</c> of the last token request.</summary>
        public string? LastAcrValues { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var authority = $"{request.RequestUri.Scheme}://{request.RequestUri.Authority}";
                return Json($$"""
                              {
                                "issuer": "{{Issuer}}",
                                "token_endpoint": "{{authority}}/connect/token",
                                "jwks_uri": "{{authority}}/.well-known/openid-configuration/jwks"
                              }
                              """);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                    parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : string.Empty));

            LastAcrValues = form.GetValueOrDefault("acr_values");
            var tenantId = LastAcrValues?.StartsWith("tenant:", StringComparison.Ordinal) == true
                ? LastAcrValues["tenant:".Length..]
                : null;

            return Json($$"""
                          {"access_token":"{{Jwt(tenantId)}}","token_type":"Bearer","expires_in":3600}
                          """);
        }

        private static string Jwt(string? tenantId)
        {
            var payload = new Dictionary<string, object?>
            {
                ["client_id"] = "octo-pipeline-sa",
                ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };
            if (tenantId is not null)
            {
                payload["tenant_id"] = tenantId;
            }

            var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"eyJhbGciOiJub25lIn0.{encoded}.signature";
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Records what the member reported to the controller, without a network.</summary>
    internal sealed class RecordingHubClient : IAdapterPoolHubClient
    {
        public List<LeaseResultDto> Releases { get; } = [];

        public Task<PoolMemberRegistrationResultDto> RegisterPoolMemberAsync(
            PoolMemberRegistrationDto registration)
        {
            return Task.FromResult(new PoolMemberRegistrationResultDto
            {
                Accepted = true, MemberId = registration.MemberId, HeartbeatIntervalSeconds = 30
            });
        }

        public Task ReleaseLeaseAsync(LeaseResultDto result)
        {
            Releases.Add(result);
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(PoolMemberHeartbeatDto heartbeat) => Task.CompletedTask;

        public IServiceClientAccessToken ClientAccessToken { get; } =
            new Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants.ServiceClientAccessToken();

        public AdapterPoolHubClientOptions Options { get; } = new();
        public Uri? ServiceUri => new("https://controller.example.com/adapterPoolHub");
        public bool IsAlive => true;
        public void EnableReconnect(Func<bool, Task> onReconnectFunction) { }

        public Task StartAsync(Func<bool, Task> onConnectFunction, CancellationToken stoppingToken) =>
            Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }
}
