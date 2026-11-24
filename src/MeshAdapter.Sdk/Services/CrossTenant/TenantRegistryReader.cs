using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>Reads the platform tenant registry for the cross-tenant relation check.</summary>
public interface ITenantRegistryReader
{
    /// <summary>
    ///     The registry, from a short cache. Throws when the registry cannot be read: the caller refuses then (fail
    ///     closed), it never passes.
    /// </summary>
    /// <param name="refresh">Read the registry again (at most once per second), e.g. after a miss.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<TenantRegistrySnapshot> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="ITenantRegistryReader" />: the system database's tenant registry
///     (<see cref="ISystemContext.GetAllTenantsAsync" />), cached for 30 seconds.
/// </summary>
/// <remarks>
///     A cached snapshot can be at most 30 seconds old. A tenant detached within that window can still pass the relation
///     check, but not the identity proof: the identity service evaluates the current tree and the generation check
///     refuses a recreated tenant.
/// </remarks>
internal sealed class TenantRegistryReader : ITenantRegistryReader
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ISystemContext _systemContext;
    private readonly string _systemTenantId;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TenantRegistrySnapshot? _snapshot;
    private DateTimeOffset _readAt;

    public TenantRegistryReader(ISystemContext systemContext, IOptions<OctoSystemConfiguration> systemConfiguration,
        TimeProvider? timeProvider = null)
    {
        _systemContext = systemContext;
        _systemTenantId = systemConfiguration.Value.SystemTenantId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TenantRegistrySnapshot> GetAsync(bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var age = now - _readAt;
            if (_snapshot != null && (refresh ? age < MinRefreshInterval : age < CacheDuration))
            {
                return _snapshot;
            }

            using var adminSession = await _systemContext.GetAdminSessionAsync();
            var tenants = await _systemContext.GetAllTenantsAsync(adminSession);
            _snapshot = new TenantRegistrySnapshot(_systemTenantId,
                tenants.Items.Select(t => (t.TenantId, t.ParentTenantId)));
            _readAt = now;
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
