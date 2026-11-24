using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace MeshAdapter.Sdk.Tests.Services.CrossTenant;

/// <summary>
///     How the execution context hands a node's cross-tenant request to the guard: the source is the registration's
///     tenant, the execution's caller and proofs travel with it, and a context without a guard refuses.
/// </summary>
public class MeshEtlContextCrossTenantTests
{
    private static readonly TenantTargetConfiguration Parent = new() { Relation = TenantRelation.Parent };

    private static MeshEtlContext CreateContext(string tenantId, ICrossTenantAccessGuard? guard,
        VerifiedPrincipal? caller = null, string? callerToken = null)
    {
        var id = new OctoObjectId("000000000000000000000099");
        return new MeshEtlContext(tenantId, A.Fake<ITenantRepository>(), id, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", id), DateTime.UtcNow, null, A.Fake<IGlobalConfiguration>(),
            new Dictionary<string, object?>(), caller, callerToken, A.Fake<IPipelineIdentityResolver>(),
            crossTenantAccessGuard: guard);
    }

    [Fact]
    public async Task WithoutAGuard_EveryCrossTenantAccessIsRefused()
    {
        var context = CreateContext("a", guard: null);

        var refused = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            context.OpenTenantAsync(Parent, NodeExecutionIdentity.Caller, A.Fake<IDataContext>(),
                A.Fake<INodeContext>()));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, refused.ReasonCode);
    }

    [Fact]
    public async Task TheDefaultInterfaceMember_Refuses()
    {
        IMeshEtlContext context = A.Fake<IMeshEtlContext>(o => o.CallsBaseMethods());

        await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            context.OpenTenantAsync(Parent, NodeExecutionIdentity.Caller, A.Fake<IDataContext>(),
                A.Fake<INodeContext>()));
    }

    // Abuse case 24: a pool adapter has no tenant of its own; the relation is always computed from the tenant the
    // pipeline is registered in.
    [Fact]
    public async Task TheSourceIsTheRegistrationTenant_AndTheExecutionsCallerTravelsWithIt()
    {
        var guard = A.Fake<ICrossTenantAccessGuard>();
        CrossTenantAccessRequest? seen = null;
        A.CallTo(() => guard.OpenAsync(A<CrossTenantAccessRequest>._, A<CancellationToken>._))
            .Invokes((CrossTenantAccessRequest r, CancellationToken _) => seen = r)
            .Returns(A.Fake<ICrossTenantAccess>());
        var caller = new VerifiedPrincipal("user-1", "a", null, null, ["R"]);
        var context = CreateContext("a", guard, caller, "caller-token");

        await context.OpenTenantAsync(Parent, NodeExecutionIdentity.ServiceAccount, A.Fake<IDataContext>(),
            A.Fake<INodeContext>());

        Assert.NotNull(seen);
        Assert.Equal("a", seen.SourceTenantId);
        Assert.Equal(NodeExecutionIdentity.ServiceAccount, seen.Identity);
        Assert.Same(caller, seen.Caller);
        Assert.Equal("caller-token", seen.CallerAccessToken);
        Assert.NotNull(seen.IdentityResolver);
    }

    [Fact]
    public async Task OneExecution_KeepsOneProofCache_AndAnotherExecutionGetsItsOwn()
    {
        var guard = A.Fake<ICrossTenantAccessGuard>();
        var caches = new List<CrossTenantProofCache>();
        A.CallTo(() => guard.OpenAsync(A<CrossTenantAccessRequest>._, A<CancellationToken>._))
            .Invokes((CrossTenantAccessRequest r, CancellationToken _) => caches.Add(r.ProofCache))
            .Returns(A.Fake<ICrossTenantAccess>());
        var first = CreateContext("a", guard);
        var second = CreateContext("a", guard);

        await first.OpenTenantAsync(Parent, NodeExecutionIdentity.Caller, A.Fake<IDataContext>(), A.Fake<INodeContext>());
        await first.OpenTenantAsync(Parent, NodeExecutionIdentity.Caller, A.Fake<IDataContext>(), A.Fake<INodeContext>());
        await second.OpenTenantAsync(Parent, NodeExecutionIdentity.Caller, A.Fake<IDataContext>(), A.Fake<INodeContext>());

        Assert.Same(caches[0], caches[1]);
        Assert.NotSame(caches[0], caches[2]);
    }
}
