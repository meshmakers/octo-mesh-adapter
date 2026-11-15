using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#9022 — <b>the released tenant's cached tenant generation leaves with the lease.</b>
/// </summary>
/// <remarks>
///     The tenant-freedom sweep clears <c>TenantGenerationReader</c> (common-services AB#8999) because this
///     participant calls <see cref="ITenantGenerationReader.Invalidate" /> for the released tenant. These tests pin
///     that call and the registration, so the sweep's reason stays true.
/// </remarks>
public class TenantGenerationLeaseParticipantTests
{
    private const string BorrowerTenantId = "tenant-b";

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
    public async Task LeaveInvalidatesTheReleasedTenantsGeneration()
    {
        var reader = A.Fake<ITenantGenerationReader>();

        await new TenantGenerationLeaseParticipant(reader).LeaveLeaseAsync(ALease(), CancellationToken.None);

        A.CallTo(() => reader.Invalidate(BorrowerTenantId)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EnterTouchesNothing()
    {
        var reader = A.Fake<ITenantGenerationReader>();

        await new TenantGenerationLeaseParticipant(reader).EnterLeaseAsync(ALease(), CancellationToken.None);

        A.CallTo(reader).MustNotHaveHappened();
    }

    [Fact]
    public async Task WithoutAReaderLeaveIsANoOp()
    {
        await new TenantGenerationLeaseParticipant().LeaveLeaseAsync(ALease(), CancellationToken.None);
    }

    /// <summary>
    ///     The pool-member composition registers the participant, and it is built with the reader of the container,
    ///     so the release reaches the same instance the tenant generation check reads from.
    /// </summary>
    [Fact]
    public async Task ThePoolMemberCompositionRegistersItAgainstTheSharedReader()
    {
        var services = new ServiceCollection();
        services.AddOctoMeshAdapterPoolMember();
        services.AddOctoMeshAdapter();

        Assert.Contains(services, d => d.ServiceType == typeof(IAdapterLeaseParticipant)
                                       && d.ImplementationType == typeof(TenantGenerationLeaseParticipant)
                                       && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(ITenantGenerationReader)
                                       && d.Lifetime == ServiceLifetime.Singleton);

        var reader = A.Fake<ITenantGenerationReader>();
        await using var provider = new ServiceCollection().AddSingleton(reader).BuildServiceProvider();
        var participant = ActivatorUtilities.CreateInstance<TenantGenerationLeaseParticipant>(provider);

        await participant.LeaveLeaseAsync(ALease(), CancellationToken.None);

        A.CallTo(() => reader.Invalidate(BorrowerTenantId)).MustHaveHappenedOnceExactly();
    }
}
