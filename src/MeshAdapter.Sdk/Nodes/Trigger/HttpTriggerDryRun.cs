using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Triggers;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
///     The dry run of an HTTP-triggered pipeline (AB#10015): the second way into a <c>FromHttpRequest@2</c> pipeline,
///     next to its route.
/// </summary>
/// <remarks>
///     <para>
///         The execute command of the controller goes to <c>execute-pipeline-{tenant}-pipeline-{rtId}</c>, which only
///         <c>FromExecutePipelineCommand@1</c> consumes. An HTTP-only pipeline had no consumer there, so every
///         <c>dry_run_pipeline</c> waited 30 s and failed with "did not answer". For a pipeline without an execute
///         trigger the controller sends a dry run to <see cref="GetAddress" /> instead (the same name plus
///         <see cref="AddressSuffix" />), which only this trigger consumes. A pipeline that has both triggers keeps one
///         consumer per address, so no exclusive endpoint is registered twice.
///     </para>
///     <para>
///         🔴 <b>Invariant: a caller who could not call the route cannot dry-run it, and nothing but a dry run comes in
///         this way.</b> <see cref="Authorize" /> refuses a real execute (a real run stays possible only through the
///         route), a request without a carried caller (also for an anonymous route), a caller whose token tenant is not
///         the adapter's tenant, and a caller without one of the route's required roles. The input is the request body;
///         the run carries the caller as its verified principal, as a route call does, and every dry-run-honouring load
///         node suppresses its side effect (<c>IPipelineExecutionMode.IsDryRun</c>).
///     </para>
///     <para>
///         Must stay in sync with <c>HttpTriggerDryRunRouting</c> in octo-communication-controller-services.
///     </para>
/// </remarks>
internal static class HttpTriggerDryRun
{
    /// <summary>Appended to the execute address of the pipeline; the controller uses the same suffix.</summary>
    internal const string AddressSuffix = "-http-dry-run";

    /// <summary>Why the dry-run consumer is exclusive: it is addressed to one pipeline, like the execute address.</summary>
    internal const string ExclusiveByDesignReason =
        "addressed to one HTTP-triggered pipeline (execute-pipeline-{tenant}-pipeline-{pipelineRtId}-http-dry-run); " +
        "the sender (communication-controller TriggerManagementService) uses the classic topology, so a further " +
        "adapter replica is a hot standby, as for FromExecutePipelineCommand@1 (AB#10015)";

    /// <summary>The dry-run address of a pipeline.</summary>
    internal static string GetAddress(string tenantId, string pipelineRtId) =>
        $"{PipelineQueueNames.ExecutePipelineCommand.ToLowerInvariant()}-{tenantId.ToLowerInvariant()}-pipeline-" +
        $"{pipelineRtId.ToLowerInvariant()}{AddressSuffix}";

    /// <summary>
    ///     The reason the request is refused, or <c>null</c> when the caller may dry-run the route. Mirrors the route's
    ///     own check (<c>HttpRequestService.IsCallerAuthorizedAsync</c>): tenant of the token first, then any-of the
    ///     required roles, compared ordinally, blank entries skipped.
    /// </summary>
    internal static string? Authorize(ExecutePipelineRequest message, string adapterTenantId,
        FromHttpRequestNodeConfiguration2 route)
    {
        var routeName = $"{route.Method.ToString().ToUpperInvariant()} {route.Path}";
        if (!message.IsDryRun)
        {
            return $"The pipeline is triggered by its HTTP route {routeName} and runs only through that route; " +
                   "the execute command starts it only as a dry run.";
        }

        var caller = message.Caller;
        if (caller == null)
        {
            return $"A dry run of the HTTP route {routeName} needs an authenticated caller; the request carried none.";
        }

        if (string.IsNullOrEmpty(caller.TenantId) ||
            !string.Equals(caller.TenantId, adapterTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return $"The caller's token of tenant '{caller.TenantId}' does not serve tenant '{adapterTenantId}'; " +
                   $"the route {routeName} refuses it too. Sign in to tenant '{adapterTenantId}' itself.";
        }

        var required = route.RequiredRoles.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        if (required.Length > 0 && !required.Any(r => caller.Roles.Contains(r, StringComparer.Ordinal)))
        {
            return $"The caller holds none of the roles {string.Join(", ", required)} the route {routeName} requires.";
        }

        return null;
    }

    /// <summary>
    ///     The data root of the dry run, shaped as the route builds it: <c>path</c>, <c>method</c>, the caller as
    ///     <c>principal</c> and the input as <c>body</c> (JSON when it parses, else the text; no body for empty input).
    ///     Query and headers are not part of a dry run.
    /// </summary>
    internal static JsonObject BuildInput(string? pipelineInput, VerifiedPrincipal principal,
        FromHttpRequestNodeConfiguration2 route)
    {
        var roles = new JsonArray();
        foreach (var role in principal.Roles)
        {
            roles.Add(role);
        }

        var input = new JsonObject
        {
            ["path"] = route.Path.ToLowerInvariant(),
            ["method"] = route.Method.ToString().ToUpperInvariant(),
            ["principal"] = new JsonObject
            {
                ["subjectId"] = principal.SubjectId,
                ["tenantId"] = principal.TenantId,
                ["email"] = principal.Email,
                ["name"] = principal.Name,
                ["roles"] = roles
            }
        };

        if (!string.IsNullOrWhiteSpace(pipelineInput))
        {
            input["body"] = ParseBody(pipelineInput);
        }

        return input;
    }

    private static JsonNode? ParseBody(string pipelineInput)
    {
        try
        {
            return JsonNode.Parse(pipelineInput);
        }
        catch (JsonException)
        {
            return JsonValue.Create(pipelineInput);
        }
    }

    /// <summary>Handles one command on the dry-run address: refuses, or starts the dry run and answers its id.</summary>
    internal static async Task HandleAsync(ITriggerContext context, FromHttpRequestNodeConfiguration2 route,
        ExecutePipelineRequest message, RespondToCommandHandler respond)
    {
        var refusal = Authorize(message, context.TenantId, route);
        if (refusal != null)
        {
            context.NodeContext.Warning("[{TenantId}] Refusing execute command for HTTP pipeline '{PipelineId}': {Reason}",
                context.TenantId, context.PipelineRtEntityId, refusal);
            await respond(new ExecutePipelineResponse(false, refusal, null, null));
            return;
        }

        try
        {
            var caller = message.Caller!;
            var principal = ExecuteCommandCallerCarryThrough.ToPrincipal(caller)!;
            var startDateTime = DateTime.UtcNow;
            var options = new ExecutePipelineOptions(startDateTime)
            {
                IsDryRun = true,
                TriggerType = PipelineTriggerType.Manual,
                VerifiedPrincipal = principal,
                CallerAccessToken = message.CallerAccessToken,
                CallerTrust = ExecuteCommandCallerCarryThrough.ToTrust(caller)
            };

            context.NodeContext.Info("Received dry run of the HTTP route {Method} {Path}", route.Method, route.Path);
            var executionId = await context.StartExecutePipelineAsync(options,
                BuildInput(message.PipelineInput, principal, route));
            await respond(new ExecutePipelineResponse(true, null, executionId, startDateTime));

            // As FromExecutePipelineCommand@1 (AB#4279): never hold the delivery ack open for the whole run.
            _ = CompleteDetachedAsync(context, executionId);
        }
        catch (Exception ex)
        {
            context.NodeContext.Error(ex, "[{TenantId}] Dry run of HTTP pipeline '{PipelineId}' failed to start",
                context.TenantId, context.PipelineRtEntityId);
            await respond(new ExecutePipelineResponse(false, ex.Message, null, null));
        }
    }

    private static async Task CompleteDetachedAsync(ITriggerContext context, Guid executionId)
    {
        try
        {
            await context.EndExecutePipelineAsync(executionId);
        }
        catch (Exception ex)
        {
            context.NodeContext.Error(ex, "[{TenantId}] Dry run '{PipelineExecutionId}' failed after detached completion",
                context.TenantId, executionId);
        }
    }
}

/// <summary>Registers the dry-run consumer of an HTTP-triggered pipeline (AB#10015).</summary>
internal interface IHttpTriggerDryRunEndpoints
{
    /// <summary>
    ///     Registers the consumer on <see cref="HttpTriggerDryRun.GetAddress" />; disposing the result removes it.
    ///     <c>null</c> when another HTTP trigger of the same pipeline already holds the address (a pipeline with two
    ///     routes dry-runs through the first one).
    /// </summary>
    IAsyncDisposable? Register(ITriggerContext context, FromHttpRequestNodeConfiguration2 route);
}

/// <inheritdoc />
internal sealed class HttpTriggerDryRunEndpoints(
    IEventHubControl eventHubControl,
    ILogger<HttpTriggerDryRunEndpoints> logger) : IHttpTriggerDryRunEndpoints
{
    private readonly ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IAsyncDisposable? Register(ITriggerContext context, FromHttpRequestNodeConfiguration2 route)
    {
        var address = HttpTriggerDryRun.GetAddress(context.TenantId, $"{context.PipelineRtEntityId.RtId}");
        if (!_claimed.TryAdd(address, 0))
        {
            logger.LogInformation(
                "Dry-run address of pipeline {PipelineId} is held by another HTTP trigger of the pipeline; route {Method} {Path} is not dry-runnable on its own",
                context.PipelineRtEntityId, route.Method, route.Path);
            return null;
        }

        try
        {
            var handle = eventHubControl.RegisterCommandConsumer<ExecutePipelineRequest>(address,
                (message, respond) => HttpTriggerDryRun.HandleAsync(context, route, message, respond),
                CommandConsumerOptions.ExclusiveByDesign(HttpTriggerDryRun.ExclusiveByDesignReason));
            return new Registration(this, address, handle);
        }
        catch
        {
            _claimed.TryRemove(address, out _);
            throw;
        }
    }

    private sealed class Registration(HttpTriggerDryRunEndpoints owner, string address, EndpointHandle handle)
        : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await handle.DisposeAsync();
            }
            finally
            {
                owner._claimed.TryRemove(address, out _);
            }
        }
    }
}
