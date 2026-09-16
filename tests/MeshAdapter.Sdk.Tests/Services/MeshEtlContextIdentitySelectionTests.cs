using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
///     Proves <see cref="MeshEtlContext.GetSessionForAsync" /> maps the configured
///     <see cref="NodeExecutionIdentity" /> onto the right <see cref="RtSecurityContext" /> (AB#5127):
///     Caller ▶ the scoped resolution, ServiceAccount ▶ the service account's own identity even with a
///     caller present, System ▶ the system context — and a missing value ▶ Caller.
/// </summary>
public class MeshEtlContextIdentitySelectionTests
{
    private const string TenantId = "test-tenant";

    private static readonly RtSecurityContext CallerContext =
        RtSecurityContext.ForUser("user-42", ["Reader"]);

    private static readonly RtSecurityContext ServiceAccountContext =
        RtSecurityContext.ForUser("octo-pipeline-sa", ["CommunicationManagement", "Accounting"]);

    private readonly ITenantRepository _tenantRepository =
        A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());

    private readonly ISecureSessionFactory _secureSessionFactory;
    private readonly IPipelineIdentityResolver _resolver = A.Fake<IPipelineIdentityResolver>();

    public MeshEtlContextIdentitySelectionTests()
    {
        _secureSessionFactory = (ISecureSessionFactory)_tenantRepository;
        A.CallTo(() => _tenantRepository.TenantId).Returns(TenantId);
        A.CallTo(() => _secureSessionFactory.GetSessionAsync(A<RtSecurityContext>._))
            .Returns(Task.FromResult(A.Fake<IOctoSession>()));

        A.CallTo(() => _resolver.ResolveAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(CallerContext));
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
            .Returns(new ValueTask<RtSecurityContext>(ServiceAccountContext));
    }

    private MeshEtlContext CreateContext(IPipelineIdentityResolver? resolver = null)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");
        return new MeshEtlContext(
            tenantId: TenantId,
            tenantRepository: _tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: A.Fake<IGlobalConfiguration>(),
            properties: new Dictionary<string, object?>(),
            verifiedPrincipal: null,
            callerAccessToken: null,
            identityResolver: resolver);
    }

    private RtSecurityContext CapturedContext()
    {
        var call = Fake.GetCalls(_secureSessionFactory)
            .Single(c => c.Method.Name == nameof(ISecureSessionFactory.GetSessionAsync));
        return (RtSecurityContext)call.Arguments[0]!;
    }

    [Fact]
    public async Task Caller_UsesTheScopedResolution()
    {
        await CreateContext(_resolver).GetSessionForAsync(NodeExecutionIdentity.Caller);

        Assert.Equal(CallerContext.SubjectId, CapturedContext().SubjectId);
        A.CallTo(() => _resolver.ResolveAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MissingIdentity_DefaultsToCaller()
    {
        // default(NodeExecutionIdentity) is what a pipeline authored before AB#5127 deserialises to.
        await CreateContext(_resolver).GetSessionForAsync(default);

        Assert.Equal(CallerContext.SubjectId, CapturedContext().SubjectId);
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ServiceAccount_UsesTheServiceAccountIdentity_NotTheCaller()
    {
        await CreateContext(_resolver).GetSessionForAsync(NodeExecutionIdentity.ServiceAccount);

        var captured = CapturedContext();
        Assert.False(captured.IsSystem);
        Assert.Equal(ServiceAccountContext.SubjectId, captured.SubjectId);
        Assert.Equal(ServiceAccountContext.Roles, captured.Roles);
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _resolver.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task System_UsesTheSystemContext()
    {
        await CreateContext(_resolver).GetSessionForAsync(NodeExecutionIdentity.System);

        Assert.True(CapturedContext().IsSystem);
        A.CallTo(() => _resolver.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _resolver.ResolveServiceAccountAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ServiceAccount_WithoutAResolver_FallsBackToSystem()
    {
        // A hand-built context (test or non-pipeline host) has no service-account source, so the best
        // available non-caller identity is the system context.
        await CreateContext(resolver: null).GetSessionForAsync(NodeExecutionIdentity.ServiceAccount);

        Assert.True(CapturedContext().IsSystem);
    }
}
