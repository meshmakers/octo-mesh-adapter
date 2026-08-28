using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Eagerly warms the tenant's CK model cache (AB#4920, on-demand lifecycle Epic AB#4914).
/// Without this, the model loads lazily on the first pipeline execution
/// (<see cref="MeshContextCreatorService.CreateEtlContext{TContext}"/>), so the first request
/// after a wake from 0 replicas pays the full model-load latency on top of the pod boot.
/// </summary>
public interface ICkModelWarmupService
{
    /// <summary>
    /// Kicks off a background warm-up for the tenant. Fire-and-forget: never blocks the
    /// caller, never throws; a concurrent trigger while a warm-up is running is a no-op.
    /// </summary>
    void TriggerWarmup(string tenantId);
}

/// <summary>
/// Triggered from <c>MeshAdapterService.StartupAsync</c> after the adapter configuration has
/// been applied — NOT a hosted service: at process start the system context is not configured
/// yet (Mongo settings arrive with the configuration pushed by the controller), so a warm-up
/// launched from the host would spin against an unconfigured context and never complete.
/// Background-only: never blocks startup, readiness or the SignalR registration; a failed
/// warm-up is retried a few times and then left to the lazy path (which stays authoritative —
/// a CK-cache flush via CkModelChanged is also reloaded lazily, not by this service).
/// Opt-out via <see cref="AdapterOptions.EagerCkModelLoad"/> (env OCTO_ADAPTER__EAGERCKMODELLOAD=false).
/// </summary>
internal sealed class CkModelWarmupService(
    ILogger<CkModelWarmupService> logger,
    IOptions<AdapterOptions> adapterOptions,
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    IHostApplicationLifetime applicationLifetime) : ICkModelWarmupService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
    ];

    private int _running;

    public void TriggerWarmup(string tenantId)
    {
        if (!adapterOptions.Value.EagerCkModelLoad)
        {
            logger.LogInformation("CK model warm-up disabled (EagerCkModelLoad=false); first execution loads lazily");
            return;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        // One warm-up at a time; a configuration re-apply while one is still retrying
        // must not stack a second loop (the running one loads the same tenant anyway).
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            logger.LogDebug("CK model warm-up for tenant '{TenantId}' already running; trigger skipped", tenantId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WarmUpAsync(tenantId, applicationLifetime.ApplicationStopping);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private async Task WarmUpAsync(string tenantId, CancellationToken stoppingToken)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                if (ckCacheService.IsTenantLoaded(tenantId))
                {
                    logger.LogDebug("CK model for tenant '{TenantId}' already loaded; warm-up skipped", tenantId);
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
                await tenantRepository.LoadCacheForTenantAsync(ckCacheService);
                logger.LogInformation(
                    "CK model warm-up for tenant '{TenantId}' completed in {ElapsedMs} ms",
                    tenantId, stopwatch.ElapsedMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (attempt == RetryDelays.Length)
                {
                    // Give up loudly but harmlessly — the lazy path in CreateEtlContext covers it.
                    logger.LogWarning(e,
                        "CK model warm-up for tenant '{TenantId}' failed after {Attempts} attempts; " +
                        "the first pipeline execution will load the model lazily",
                        tenantId, attempt + 1);
                    return;
                }

                logger.LogDebug(e,
                    "CK model warm-up attempt {Attempt} for tenant '{TenantId}' failed; retrying",
                    attempt + 1, tenantId);
            }

            try
            {
                await Task.Delay(RetryDelays[attempt], stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
