using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Xunit;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// AB#5341 — <c>DeployPipeline@1</c> must never start what the operator switched off.
///
/// <para>
/// <c>DeployDataFlow</c> has always dropped disabled pipelines from the configuration it pushes, so
/// on that path a disabled pipeline is an UNDEPLOYED one. This node went straight to the controller
/// instead, which registers the pipeline and starts its triggers regardless — so a computation that
/// keeps a trigger's settings fresh (the AB#5341 import-window recompute is the first one) would
/// resurrect an import nobody enabled, on every tenant that has the pipeline but never turned it on.
/// </para>
///
/// <para>
/// Skipped rather than failed: "the operator switched it off" is a normal state and the calling
/// pipeline has nothing to repair, so the chain continues.
/// </para>
/// </summary>
public class DeployPipelineNodeEnabledGuardTests : SessionNodeTestBase
{
    private static readonly RtCkId<CkTypeId> PipelineCkTypeId = new("System.Communication/Pipeline");
    private static readonly RtCkId<CkTypeId> DataFlowCkTypeId = new("System.Communication/DataFlow");
    private static readonly RtCkId<CkTypeId> AdapterCkTypeId = new("System.Communication/Adapter");

    private static readonly OctoObjectId TargetPipelineRtId = new("67d4a2f0b2e4d8c3a1f000b1");
    private static readonly OctoObjectId CallerPipelineRtId = new("67d4a2f0b2e4d8c3a1f000b2");
    private static readonly OctoObjectId DataFlowRtId = new("67d4a2f0b2e4d8c3a1f051b0");
    private static readonly OctoObjectId AdapterRtId = new("670000000000000000000002");

    private const string ServiceAccountConfigName = "DeployServiceAccount";
    private const string ServiceAccountToken = "deploy-service-account-token";

    private readonly ICommunicationServicesClient _client = A.Fake<ICommunicationServicesClient>();
    private readonly IServiceAccountCommunicationClientFactory _clientFactory =
        A.Fake<IServiceAccountCommunicationClientFactory>();
    private readonly IServiceAccountTokenService _tokenService = A.Fake<IServiceAccountTokenService>();

    public DeployPipelineNodeEnabledGuardTests()
    {
        // System by classification (AB#5028) — the node reads pure platform types and calls the
        // controller as the adapter's service identity.
        GivenSystemSessionIsExpected();

        A.CallTo(() => EtlContext.PipelineRtEntityId)
            .Returns(new RtEntityId(PipelineCkTypeId, CallerPipelineRtId));
        A.CallTo(() => EtlContext.DataFlowRtId).Returns(DataFlowRtId);

        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(TenantRepository, ServiceAccountConfigName,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(ServiceAccountToken));
        A.CallTo(() => _clientFactory.Create(ServiceAccountToken)).Returns(_client);
    }

    /// <summary>Puts one pipeline entity behind the by-id read, carrying the given Enabled value.</summary>
    private void GivenTargetPipeline(bool? enabled)
    {
        var entity = new RtEntity(PipelineCkTypeId, TargetPipelineRtId);
        entity.SetAttributeRawValue("PipelineDefinition", "triggers: []");
        if (enabled.HasValue)
        {
            entity.SetAttributeRawValue("Enabled", enabled.Value);
        }

        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity> { entity });

        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));

        GivenAssociationTarget(DataFlowCkTypeId, new RtEntity(DataFlowCkTypeId, DataFlowRtId));
        GivenAssociationTarget(AdapterCkTypeId, new RtEntity(AdapterCkTypeId, AdapterRtId));
    }

    private void GivenAssociationTarget(RtCkId<CkTypeId> targetCkTypeId, RtEntity target)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity> { target });

        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() =>
            new List<KeyValuePair<RtEntityId, IResultSet<RtEntity>>>
            {
                new(new RtEntityId(PipelineCkTypeId, TargetPipelineRtId), resultSet)
            }.GetEnumerator());

        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(
                A<IOctoSession>._, A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._,
                A<RtCkId<CkAssociationRoleId>>._, targetCkTypeId, A<GraphDirections>._,
                A<IReadOnlyList<OctoObjectId>?>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(multi));
    }

    private async Task RunAsync()
    {
        var config = new DeployPipelineNodeConfiguration
        {
            PipelineRtId = TargetPipelineRtId,
            ServiceAccountConfigName = ServiceAccountConfigName
        };
        (_dataContext, _nodeContext, _next) = PrepareTest(config);

        var node = new DeployPipelineNode(_next, EtlContext, _clientFactory, _tokenService);

        await node.ProcessObjectAsync(_dataContext, _nodeContext);
    }

    private IDataContext _dataContext = null!;
    private INodeContext _nodeContext = null!;
    private NodeDelegate _next = null!;

    [Fact]
    public async Task ADisabledPipelineIsNotDeployed_AndTheChainContinues()
    {
        GivenTargetPipeline(enabled: false);

        await RunAsync();

        A.CallTo(() => _client.DeployPipelineAsync(A<string>._, A<string>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AnEnabledPipelineIsDeployed()
    {
        GivenTargetPipeline(enabled: true);

        await RunAsync();

        A.CallTo(() => _client.DeployPipelineAsync(
                AdapterRtId.ToString(), TargetPipelineRtId.ToString(), A<string>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// A pipeline entity that never carried the attribute is not a disabled one — the guard tests
    /// for an explicit <c>false</c>, so nothing that worked before AB#5341 stops working.
    /// </summary>
    [Fact]
    public async Task APipelineWithoutTheAttributeIsStillDeployed()
    {
        GivenTargetPipeline(enabled: null);

        await RunAsync();

        A.CallTo(() => _client.DeployPipelineAsync(A<string>._, A<string>._, A<string>._))
            .MustHaveHappenedOnceExactly();
    }

    // ------------------------------------------------------------------ AB#8699: service-account token

    [Fact]
    public async Task TheDeployRunsWithTheConfiguredServiceAccountsOwnToken_OnAClientOfItsOwn()
    {
        GivenTargetPipeline(enabled: true);

        await RunAsync();

        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(TenantRepository, ServiceAccountConfigName,
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _clientFactory.Create(ServiceAccountToken)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _client.DeployPipelineAsync(
                AdapterRtId.ToString(), TargetPipelineRtId.ToString(), A<string>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WithoutAServiceAccountToken_NothingIsDeployed_AndTheChainStops()
    {
        // Fail-closed: before AB#8699 the call went out with whatever the shared holder carried.
        A.CallTo(() => _tokenService.AcquireServiceAccountTokenAsync(TenantRepository, ServiceAccountConfigName,
                A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(null));
        GivenTargetPipeline(enabled: true);

        await RunAsync();

        A.CallTo(() => _clientFactory.Create(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _client.DeployPipelineAsync(A<string>._, A<string>._, A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _next(_dataContext, _nodeContext)).MustNotHaveHappened();
    }

    [Fact]
    public void TheNodeCannotReachTheAdaptersProcessWideTokenHolder()
    {
        // AB#8699: neither the holder nor the shared client (which reads the holder) is a dependency.
        var parameters = typeof(DeployPipelineNode).GetConstructors().Single().GetParameters();
        Assert.DoesNotContain(parameters,
            p => typeof(Meshmakers.Octo.Sdk.ServiceClient.IServiceClientAccessToken).IsAssignableFrom(p.ParameterType)
                 || p.ParameterType == typeof(ICommunicationServicesClient));
    }
}
