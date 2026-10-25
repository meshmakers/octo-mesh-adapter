using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
///     AB#7462 — the method-invocation marker of the execution options reaches the ETL context as a side
///     channel, never through the pipeline-wide <see cref="IEtlContext.Properties" />.
/// </summary>
public class MeshContextCreatorServiceTests
{
    private const string TenantId = "test-tenant";

    private static async Task<(IMeshEtlContext Context, IDictionary<string, object?> Properties)> CreateAsync(
        ExecutePipelineMethodInvocation? marker)
    {
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantRepositoryAsync(TenantId))
            .Returns(Task.FromResult(A.Fake<ITenantRepository>()));
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<IServiceAccountTokenService>());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        var sut = new MeshContextCreatorService(services.BuildServiceProvider(), A.Fake<ICkCacheService>(),
            systemContext);

        var properties = new Dictionary<string, object?>();
        var registration = new PipelineRegistration(TenantId, OctoObjectId.GenerateNewId(),
            new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId()), false,
            new NodeDefinitionRoot(), A.Fake<IGlobalConfiguration>(), properties);
        var options = new ExecutePipelineOptions(DateTime.UtcNow) { MethodInvocation = marker };

        var context = await sut.CreateEtlContext<IMeshEtlContext>(registration, options, Guid.NewGuid());
        return (context, properties);
    }

    [Fact]
    public async Task TheMarkerOfTheOptions_IsExposedOnTheContext_ButNotInProperties()
    {
        var marker = new ExecutePipelineMethodInvocation
        {
            MethodId = "Test-1.0.0/Account/Create-1",
            InvocationId = Guid.NewGuid(),
            CkTypeId = "Test-1.0.0/Account-1",
            PipelineRtId = OctoObjectId.GenerateNewId().ToString()
        };

        var (context, properties) = await CreateAsync(marker);

        Assert.Same(marker, context.MethodInvocation);
        Assert.Empty(properties);
        Assert.DoesNotContain(context.Properties.Values, v => ReferenceEquals(v, marker));
    }

    [Fact]
    public async Task WithoutAMarker_TheContextHasNone()
    {
        var (context, _) = await CreateAsync(null);

        Assert.Null(context.MethodInvocation);
    }
}
