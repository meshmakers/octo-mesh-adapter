using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FakeItEasy;
using IdentityModel;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Trigger;

/// <summary>
/// Drives real HTTP requests through the adapter's own middleware chain
/// (<c>UseOctoMeshAdapter</c>) so the JWT bearer wiring, the production
/// <see cref="ConfigureJwtBearerOptions"/> and the route gate are exercised together. Unit
/// tests inject a ready-made <c>ClaimsPrincipal</c>; only here does a token get signed,
/// transmitted in a header and validated, which is what covers expiry.
/// </summary>
public sealed class FromHttpRequestNode2AuthorizationTests : IDisposable
{
    private const string TenantId = "testTenant";
    private const string Authority = "https://identity.test.local";
    private const string Route = "/webhook";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly ITriggerContext _triggerContext = A.Fake<ITriggerContext>();
    private readonly RecordingEventService _eventService = new();
    private IHost? _host;
    private string? _authenticationFailure;

    /// <summary>Captures the audit trail a decision writes, without touching the event store.</summary>
    private sealed class RecordingEventService : IAdapterEventService
    {
        public List<(RtEventLevelsEnum Level, string Message)> Events { get; } = [];

        public Task StoreDebugEventAsync(string? tenantId, string message)
        {
            Events.Add((RtEventLevelsEnum.Debug, message));
            return Task.CompletedTask;
        }

        public Task StoreInformationEventAsync(string? tenantId, string message)
        {
            Events.Add((RtEventLevelsEnum.Information, message));
            return Task.CompletedTask;
        }

        public Task StoreWarningEventAsync(string? tenantId, string message)
        {
            Events.Add((RtEventLevelsEnum.Warning, message));
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    [Fact]
    public async Task RequestWithoutAuthorizationHeader_IsRejectedUnauthorized()
    {
        using var client = await CreateClientAsync();

        var response = await client.PostAsJsonAsync($"/{TenantId}{Route}", new { probe = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        PipelineExecutions().Should().Be(0);
    }

    [Fact]
    public async Task RequestWithExpiredToken_IsRejectedUnauthorized()
    {
        using var client = await CreateClientAsync();
        var token = CreateToken(TenantId, expires: DateTime.UtcNow.AddMinutes(-30), roles: "TenantAdmin");

        var response = await Post(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        PipelineExecutions().Should().Be(0);
    }

    [Fact]
    public async Task RequestWithTokenSignedByAnotherKey_IsRejectedUnauthorized()
    {
        using var client = await CreateClientAsync();
        using var foreignRsa = RSA.Create(2048);
        var foreignKey = new RsaSecurityKey(foreignRsa) { KeyId = "test-key" };
        var token = CreateToken(TenantId, roles: "TenantAdmin", signingKey: foreignKey);

        var response = await Post(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        PipelineExecutions().Should().Be(0);
    }

    [Fact]
    public async Task RequestWithoutTheRequiredRole_IsRejectedForbidden()
    {
        using var client = await CreateClientAsync();
        var token = CreateToken(TenantId, roles: "Reader");

        var response = await Post(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        PipelineExecutions().Should().Be(0);
    }

    [Fact]
    public async Task RequestWithATokenOfAnotherTenant_IsRejectedForbidden()
    {
        using var client = await CreateClientAsync();
        var token = CreateToken("otherTenant", roles: "TenantAdmin");

        var response = await Post(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        PipelineExecutions().Should().Be(0);
    }

    [Fact]
    public async Task RequestWithTheRequiredRole_TriggersThePipeline()
    {
        using var client = await CreateClientAsync();
        var token = CreateToken(TenantId, roles: "TenantAdmin");

        var response = await Post(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK, _authenticationFailure ?? "no failure recorded");
        PipelineExecutions().Should().Be(1);
    }

    [Fact]
    public async Task EveryDecisionOnASecuredRoute_IsAudited()
    {
        using var client = await CreateClientAsync();

        await client.PostAsJsonAsync($"/{TenantId}{Route}", new { probe = 1 });
        await Post(client, CreateToken(TenantId, roles: "Reader"));
        await Post(client, CreateToken(TenantId, roles: "TenantAdmin"));

        _eventService.Events.Should().HaveCount(3);
        _eventService.Events[0].Should().Match<(RtEventLevelsEnum Level, string Message)>(
            e => e.Level == RtEventLevelsEnum.Warning && e.Message.Contains("no valid access token"));
        _eventService.Events[1].Should().Match<(RtEventLevelsEnum Level, string Message)>(
            e => e.Level == RtEventLevelsEnum.Warning && e.Message.Contains("none of the required roles"));
        _eventService.Events[2].Should().Match<(RtEventLevelsEnum Level, string Message)>(
            e => e.Level == RtEventLevelsEnum.Information && e.Message.Contains("Allowed"));
        _eventService.Events.Should().OnlyContain(e => !e.Message.Contains("Bearer"));
    }

    [Fact]
    public async Task SecuredRoute_DoesNotHandTheTokenToThePipeline()
    {
        JsonNode? receivedInput = null;
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes((ExecutePipelineOptions _, object? input) => receivedInput = input as JsonNode)
            .Returns(Task.FromResult<object?>(null));

        using var client = await CreateClientAsync();
        var token = CreateToken(TenantId, roles: "TenantAdmin");

        await Post(client, token);

        receivedInput.Should().NotBeNull();
        receivedInput!["headers"]?["Authorization"].Should().BeNull();
    }

    private Task<HttpResponseMessage> Post(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"/{TenantId}{Route}")
        {
            Content = JsonContent.Create(new { probe = 1 })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client.SendAsync(request);
    }

    private int PipelineExecutions()
    {
        return Fake.GetCalls(_triggerContext)
            .Count(call => call.Method.Name == nameof(ITriggerContext.ExecuteAsync));
    }

    /// <summary>
    /// Builds the adapter's HTTP host with one secured route requiring the TenantAdmin role.
    /// Everything about token validation comes from production configuration; only the signing
    /// keys are supplied statically, so no OpenID discovery document has to be fetched.
    /// </summary>
    private async Task<HttpClient> CreateClientAsync()
    {
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<FromHttpRequestNodeConfiguration2>())
            .Returns(new FromHttpRequestNodeConfiguration2
            {
                Path = Route, Method = HttpMethod.Post, RequiredRoles = ["TenantAdmin"]
            });
        A.CallTo(() => _triggerContext.NodeContext).Returns(nodeContext);

        _host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.Configure<AdapterOptions>(options => options.TenantId = TenantId);
                    services.Configure<MeshAdapterConfiguration>(options => options.AuthorityUrl = Authority);
                    services.AddCors();
                    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
                    services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
                    services.AddSingleton<IAdapterEventService>(_eventService);
                    services.AddSingleton<IHttpRequestService, HttpRequestService>();
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        // Authority makes the handler build a ConfigurationManager that would fetch
                        // the discovery document over the network, so it is replaced by a static one
                        // holding the test signing key. Issuer, audience and lifetime handling stay
                        // exactly as production configured them.
                        var configuration = new OpenIdConnectConfiguration { Issuer = $"{Authority}/" };
                        configuration.SigningKeys.Add(_signingKey);
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                        options.Events = new JwtBearerEvents
                        {
                            OnAuthenticationFailed = ctx =>
                            {
                                _authenticationFailure = ctx.Exception.ToString();
                                return Task.CompletedTask;
                            }
                        };
                    });
                })
                .Configure(app =>
                {
                    app.UseOctoMeshAdapter();

                    var node = new FromHttpRequestNode2(NullLogger<FromHttpRequestNode2>.Instance,
                        app.ApplicationServices.GetRequiredService<IHttpRequestService>(), _eventService);
                    node.StartAsync(_triggerContext).GetAwaiter().GetResult();
                }))
            .StartAsync();

        return _host.GetTestClient();
    }

    private string CreateToken(string tenantId, DateTime? expires = null, string? roles = null,
        SecurityKey? signingKey = null)
    {
        // An expired token has to have been issued before it expired, so the issue time is
        // derived from the expiry rather than from now.
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        var issuedAt = expiry.AddHours(-1);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = $"{Authority}/",
            Audience = CommonConstants.OctoApi,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expiry,
            SigningCredentials = new SigningCredentials(signingKey ?? _signingKey,
                SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtClaimTypes.Subject] = "660000000000000000000042",
                ["tenant_id"] = tenantId
            }
        };

        if (roles != null)
        {
            descriptor.Claims[JwtClaimTypes.Role] = roles;
        }

        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }
}
