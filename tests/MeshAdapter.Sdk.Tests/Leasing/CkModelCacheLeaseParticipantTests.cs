using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#4924 / AB#8883 — <b>the released tenant's process-wide engine state leaves with the lease.</b>
/// </summary>
/// <remarks>
///     The tenant-freedom sweep clears <c>SystemContext</c> and <c>TenantOwnershipConflictTracker</c>
///     because this participant calls <see cref="ISystemContext.InvalidateTenantResolveImportGuards" />
///     for the released tenant: the engine drops the tenant's resolve attempt guards and its recorded
///     ownership conflict there (engine-mongodb AB#8796). These tests pin that call, so the sweep's
///     reasons stay true.
/// </remarks>
public class CkModelCacheLeaseParticipantTests
{
    private const string BorrowerTenantId = "tenant-b";

    private readonly ICkCacheService _ckCacheService = A.Fake<ICkCacheService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    private CkModelCacheLeaseParticipant Participant =>
        new(_ckCacheService, _systemContext, NullLogger<CkModelCacheLeaseParticipant>.Instance);

    private static LeaseDto ALease() => new()
    {
        LeaseId = "lease-1",
        TenantId = BorrowerTenantId,
        AdapterPoolTenantId = "lender",
        AdapterPoolRtId = "665f0000000000000000ee21",
        AdapterRtId = "665f0000000000000000ee22",
        AdapterCkTypeId = "System.Communication/Adapter",
        GrantedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };

    [Fact]
    public async Task LeaveUnloadsTheModelAndInvalidatesTheTenantsEngineState()
    {
        A.CallTo(() => _ckCacheService.IsTenantLoaded(BorrowerTenantId)).Returns(true);

        await Participant.LeaveLeaseAsync(ALease(), CancellationToken.None);

        A.CallTo(() => _ckCacheService.Unload(BorrowerTenantId)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _systemContext.InvalidateTenantResolveImportGuards(BorrowerTenantId))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    ///     A lease whose warm-up failed never loaded the model, but its resolve may still have recorded
    ///     an ownership conflict — the engine state is invalidated regardless of the model cache.
    /// </summary>
    [Fact]
    public async Task LeaveInvalidatesTheTenantsEngineStateEvenWhenNoModelWasLoaded()
    {
        A.CallTo(() => _ckCacheService.IsTenantLoaded(BorrowerTenantId)).Returns(false);

        await Participant.LeaveLeaseAsync(ALease(), CancellationToken.None);

        A.CallTo(() => _ckCacheService.Unload(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _systemContext.InvalidateTenantResolveImportGuards(BorrowerTenantId))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _systemContext.InvalidateTenantResolveImportGuards(
                A<string>.That.Not.IsEqualTo(BorrowerTenantId)))
            .MustNotHaveHappened();
    }
}
