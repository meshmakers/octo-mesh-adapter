using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Writes adapter events into the tenant's event log - the audit trail an operator reads in
/// Studio under Repository / Events, filtered by the Adapter source.
/// </summary>
internal interface IAdapterEventService
{
    Task StoreDebugEventAsync(string? tenantId, string message);

    Task StoreInformationEventAsync(string? tenantId, string message);

    Task StoreWarningEventAsync(string? tenantId, string message);
}

/// <summary>
/// Event service for the adapter. The repository is registered scoped while its callers are
/// singletons, so a scope is opened per event - the same shape the communication controller
/// uses for its own event service.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class AdapterEventService(IServiceProvider serviceProvider, ILogger<AdapterEventService> logger)
    : IAdapterEventService
{
    public Task StoreDebugEventAsync(string? tenantId, string message)
    {
        return StoreEventAsync(tenantId, RtEventLevelsEnum.Debug, message);
    }

    public Task StoreInformationEventAsync(string? tenantId, string message)
    {
        return StoreEventAsync(tenantId, RtEventLevelsEnum.Information, message);
    }

    public Task StoreWarningEventAsync(string? tenantId, string message)
    {
        return StoreEventAsync(tenantId, RtEventLevelsEnum.Warning, message);
    }

    private async Task StoreEventAsync(string? tenantId, RtEventLevelsEnum level, string message)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            await eventRepository.StoreEventAsync(tenantId!, RtEventSourcesEnum.MeshAdapter, level, message);
        }
        catch (Exception e)
        {
            // Auditing must never turn an invocation into a 500. The catch is deliberately
            // broader than the event store's own exception so that a missing tenant id or an
            // absent event model degrades to this warning instead of failing the request.
            logger.LogWarning(e, "Failed to store {Level} event for tenant {TenantId}: {Message}",
                level, tenantId, message);
        }
    }
}
