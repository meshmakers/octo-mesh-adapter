using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace MeshAdapter.Sdk.Tests.Services.HttpRequests;

/// <summary>
/// Records the events a route decision writes. A hand-written double rather than a fake, because
/// faking an internal interface would require opening the production assembly to the proxy
/// generator, and the recorded list reads better in assertions.
/// </summary>
internal sealed class RecordingAdapterEventService : IAdapterEventService
{
    public List<(RtEventLevelsEnum Level, string? TenantId, string Message)> Events { get; } = [];

    public Task StoreDebugEventAsync(string? tenantId, string message)
    {
        Events.Add((RtEventLevelsEnum.Debug, tenantId, message));
        return Task.CompletedTask;
    }

    public Task StoreInformationEventAsync(string? tenantId, string message)
    {
        Events.Add((RtEventLevelsEnum.Information, tenantId, message));
        return Task.CompletedTask;
    }

    public Task StoreWarningEventAsync(string? tenantId, string message)
    {
        Events.Add((RtEventLevelsEnum.Warning, tenantId, message));
        return Task.CompletedTask;
    }
}
