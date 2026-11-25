using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeshAdapter.Sdk.IntegrationTests.Services;

/// <summary>
///     The cross-tenant guard against real tenants in a real MongoDB (<see cref="TenantSubtreeFixture" />): the
///     relation comes from the real tenant registry (parents, detach), the target's repository is really opened and
///     read by a test node. Only the identity service is scripted (grants per target tenant), so these tests prove
///     the relation and isolation half; the identity half is covered by the unit tests and the identity service.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class CrossTenantAccessGuardIntegrationTests(TenantSubtreeFixture fixture) : IClassFixture<TenantSubtreeFixture>
{
    private const string Generation = "gen-1";

    private static readonly VerifiedPrincipal CallerOfA =
        new("user-of-a", TenantSubtreeFixture.TenantA, null, null, ["AdminOfA"]);

    /// <summary>Targets the scripted identity service grants to the caller (roles in that target).</summary>
    private readonly HashSet<string> _grantedTargets = new(StringComparer.OrdinalIgnoreCase);

    private ICrossTenantAccessGuard CreateGuard()
    {
        var provider = A.Fake<ICrossTenantIdentityProvider>();
        A.CallTo(() => provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                _grantedTargets.Contains(r.TargetTenantId)
                    ? new CrossTenantProof(r.TargetTenantId, Generation,
                        RtSecurityContext.ForUser($"xt_{r.SourceTenantId}_{r.SourceIdentity.SubjectId}",
                            ["TenantDataReader"]))
                    : null));
        var generations = A.Fake<ITenantGenerationReader>();
        A.CallTo(() => generations.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
            .Returns(new ValueTask<string?>(Generation));

        var systemContext = fixture.GetSystemContext();
        return new CrossTenantAccessGuard(systemContext, fixture.GetService<ICkCacheService>(),
            new TenantRegistryReader(systemContext, fixture.GetService<IOptions<OctoSystemConfiguration>>()),
            provider, NullLogger<CrossTenantAccessGuard>.Instance, generations);
    }

    private async Task<MeshEtlContext> CreateContextAsync(string sourceTenantId, VerifiedPrincipal? caller)
    {
        var repository = await fixture.GetSystemContext().FindTenantRepositoryAsync(sourceTenantId);
        var id = new OctoObjectId("000000000000000000000077");
        return new MeshEtlContext(sourceTenantId, repository, id, Guid.NewGuid(),
            new RtEntityId("System.Communication/Pipeline", id), DateTime.UtcNow, null, A.Fake<IGlobalConfiguration>(),
            new Dictionary<string, object?>(), caller, caller == null ? null : "caller-token",
            A.Fake<IPipelineIdentityResolver>(), crossTenantAccessGuard: CreateGuard());
    }

    private async Task<IReadOnlyList<string>> RunProbeAsync(string sourceTenantId, TenantTargetConfiguration target,
        NodeExecutionIdentity identity = NodeExecutionIdentity.Caller, VerifiedPrincipal? caller = null,
        IDataContext? dataContext = null)
    {
        fixture.EnsureInitialized();
        var context = await CreateContextAsync(sourceTenantId, caller ?? CallerOfA);
        var node = new CrossTenantProbeNode((_, _) => Task.CompletedTask, context);
        var nodeContext = A.Fake<INodeContext>();
        A.CallTo(() => nodeContext.GetNodeConfiguration<CrossTenantProbeNodeConfiguration>())
            .Returns(new CrossTenantProbeNodeConfiguration { TargetTenant = target, Identity = identity });

        await node.ProcessObjectAsync(dataContext ?? A.Fake<IDataContext>(), nodeContext);
        return node.SerialNumbersRead;
    }

    private async Task<CrossTenantAccessDeniedException> RefusedAsync(string sourceTenantId,
        TenantTargetConfiguration target, NodeExecutionIdentity identity = NodeExecutionIdentity.Caller,
        IDataContext? dataContext = null)
    {
        return await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            RunProbeAsync(sourceTenantId, target, identity, dataContext: dataContext));
    }

    private static TenantTargetConfiguration Target(TenantRelation relation, string? tenantId = null) =>
        new() { Relation = relation, TenantId = tenantId };

    [Fact]
    public async Task CompanyA_WithAGrant_ReadsTheGroup_AndOnlyTheGroup()
    {
        _grantedTargets.Add(TenantSubtreeFixture.Group);

        var read = await RunProbeAsync(TenantSubtreeFixture.TenantA, Target(TenantRelation.Parent));

        Assert.Equal([TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.Group)], read);
    }

    [Fact]
    public async Task Group_WithAGrant_ReadsChildA()
    {
        _grantedTargets.Add(TenantSubtreeFixture.TenantA);

        var read = await RunProbeAsync(TenantSubtreeFixture.Group, Target(TenantRelation.Child, TenantSubtreeFixture.TenantA),
            caller: new VerifiedPrincipal("user-of-g", TenantSubtreeFixture.Group, null, null, ["AdminOfG"]));

        Assert.Equal([TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA)], read);
    }

    [Fact]
    public async Task CompanyA_OpeningCompanyB_WithoutAGrant_IsForbidden_AndReadsNothingOfB()
    {
        // Acceptance criterion 1: A opens B without a grant.
        var refused = await RefusedAsync(TenantSubtreeFixture.TenantA,
            Target(TenantRelation.Sibling, TenantSubtreeFixture.TenantB));

        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused.ErrorCode);
        Assert.Equal(CrossTenantRefusalReason.IdentityNotProvable, refused.ReasonCode);
        Assert.DoesNotContain(TenantSubtreeFixture.PoisonCanary, refused.Message);
    }

    [Fact]
    public async Task CompanyA_WithAGrantForB_ReadsB()
    {
        _grantedTargets.Add(TenantSubtreeFixture.TenantB);

        var read = await RunProbeAsync(TenantSubtreeFixture.TenantA,
            Target(TenantRelation.Sibling, TenantSubtreeFixture.TenantB));

        Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantB), read);
        Assert.DoesNotContain(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantA), read);
    }

    [Theory]
    [InlineData(TenantSubtreeFixture.TenantA, TenantRelation.Sibling, TenantSubtreeFixture.Outsider,
        CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData(TenantSubtreeFixture.Group, TenantRelation.Sibling, TenantSubtreeFixture.OtherGroup,
        CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData(TenantSubtreeFixture.Group, TenantRelation.Child, TenantSubtreeFixture.Detached,
        CrossTenantRefusalReason.TargetNotRegistered)]
    [InlineData(TenantSubtreeFixture.Group, TenantRelation.Child, TenantSubtreeFixture.GrandChild,
        CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData(TenantSubtreeFixture.GrandChild, TenantRelation.Sibling, TenantSubtreeFixture.Group,
        CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData(TenantSubtreeFixture.GrandChild, TenantRelation.Sibling, TenantSubtreeFixture.TenantB,
        CrossTenantRefusalReason.RelationNotAllowed)]
    public async Task OutsiderDetachedGrandchildAncestorAndCousin_AreRefused_EvenWithAGrant(string source,
        TenantRelation relation, string target, string reason)
    {
        // Acceptance criterion 2: outside the subtree or detached is refused; the grant does not widen the relation.
        _grantedTargets.Add(target);

        var refused = await RefusedAsync(source, Target(relation, target));

        Assert.Equal(reason, refused.ReasonCode);
    }

    [Fact]
    public async Task TopLevelGroup_HasNoParent_TheSystemTenantIsNeverATarget()
    {
        _grantedTargets.Add(fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemTenantId);

        var refused = await RefusedAsync(TenantSubtreeFixture.Group, Target(TenantRelation.Parent));

        Assert.Equal(CrossTenantRefusalReason.RelationNotAllowed, refused.ReasonCode);
    }

    [Theory]
    [InlineData(TenantRelation.Parent, null)]
    [InlineData(TenantRelation.Sibling, TenantSubtreeFixture.TenantB)]
    public async Task SystemIdentity_IsRefused_EvenWithRelationAndGrant(TenantRelation relation, string? target)
    {
        // Acceptance criterion 3: no named System exception.
        _grantedTargets.Add(TenantSubtreeFixture.Group);
        _grantedTargets.Add(TenantSubtreeFixture.TenantB);

        var refused = await RefusedAsync(TenantSubtreeFixture.TenantA, Target(relation, target),
            NodeExecutionIdentity.System);

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, refused.ReasonCode);
    }

    [Theory]
    [InlineData(TenantSubtreeFixture.TenantB, null)]
    [InlineData(TenantSubtreeFixture.Outsider, CrossTenantRefusalReason.RelationNotAllowed)]
    [InlineData(TenantSubtreeFixture.Detached, CrossTenantRefusalReason.TargetNotRegistered)]
    public async Task TargetIdFromPipelineInput_IsCheckedLikeAnyOther(string dataValue, string? reason)
    {
        // Acceptance criterion 4.
        _grantedTargets.Add(TenantSubtreeFixture.TenantB);
        _grantedTargets.Add(TenantSubtreeFixture.Outsider);
        _grantedTargets.Add(TenantSubtreeFixture.Detached);
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(() => dataContext.Get<string>("$.body.recipientTenantId")).Returns(dataValue);
        var target = new TenantTargetConfiguration
            { Relation = TenantRelation.Sibling, TenantIdPath = "$.body.recipientTenantId" };

        if (reason == null)
        {
            var read = await RunProbeAsync(TenantSubtreeFixture.TenantA, target, dataContext: dataContext);
            Assert.Contains(TenantSubtreeFixture.MarkerOf(TenantSubtreeFixture.TenantB), read);
        }
        else
        {
            var refused = await RefusedAsync(TenantSubtreeFixture.TenantA, target, dataContext: dataContext);
            Assert.Equal(reason, refused.ReasonCode);
        }
    }
}

/// <summary>Configuration of <see cref="CrossTenantProbeNode" />.</summary>
public record CrossTenantProbeNodeConfiguration : NodeConfiguration
{
    public NodeExecutionIdentity Identity { get; init; } = NodeExecutionIdentity.Caller;

    public required TenantTargetConfiguration TargetTenant { get; init; }
}

/// <summary>
///     Test-only node: reads the marker entities of the target tenant the way a real cross-tenant read node will —
///     through <see cref="IMeshEtlContext.OpenTenantAsync" /> and only with the session the access hands out.
/// </summary>
public class CrossTenantProbeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public List<string> SerialNumbersRead { get; } = [];

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CrossTenantProbeNodeConfiguration>();
        var access = await etlContext.OpenTenantAsync(c.TargetTenant, c.Identity, dataContext, nodeContext);

        using var session = await access.OpenSessionAsync();
        session.StartTransaction();
        var result = await access.Repository.GetRtEntitiesByTypeAsync(session,
            TenantSubtreeFixture.SensorReadingCkTypeId, RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();

        SerialNumbersRead.AddRange(result.Items
            .Select(e => e.GetAttributeStringValueOrDefault("SerialNumber"))
            .Where(s => s != null)
            .Select(s => s!));
        await next(dataContext, nodeContext);
    }
}
