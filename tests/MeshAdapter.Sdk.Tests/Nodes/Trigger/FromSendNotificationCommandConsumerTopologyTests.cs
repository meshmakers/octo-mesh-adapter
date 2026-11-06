using System.Collections.Concurrent;
using FakeItEasy;
using MassTransit;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

/// <summary>
///     AB#8405: <see cref="FromSendNotificationNode" /> declares its command consumer <b>exclusive by design</b> (option
///     from AB#8363, same pattern as the SDK trigger nodes in AB#8105), so registering a notification pipeline writes no
///     AB#6909 deprecation warning and the topology is a stated decision.
/// </summary>
/// <remarks>
///     <para>
///         The first test checks what the node hands to <see cref="IEventHubControl" />: the options overload with
///         <see cref="CommandConsumerOptions.ExclusiveByDesignReason" /> set and nothing that would change the topology,
///         never the classic two-argument overload.
///     </para>
///     <para>
///         The other tests run the node against the real <see cref="IEventHubControl" /> of the event hub package
///         (resolved through <c>AddDistributionEventHub</c>, only the MassTransit bus is faked, no broker) and assert on
///         its log and on the receive endpoint: no deprecation warning, the reason logged at Debug, the classic queue
///         name (no <c>::shared</c> queue), and the consume topology left at the MassTransit default. The notifications
///         arrive by publish through the message-type binding of that queue, so the binding must not be switched off.
///         A control registration through the classic overload in the same harness does log the warning, so the absence
///         is not an artefact of the harness, and a plain shared registration does switch the consume topology off,
///         so the binding check can fail.
///     </para>
/// </remarks>
public class FromSendNotificationCommandConsumerTopologyTests
{
    private const string InstancePrefix = "ab8405";
    private const string DeprecationMarker = "Deprecated (AB#6909)";

    [Fact]
    public async Task FromSendNotification_RegistersExclusiveByDesign_WithTheDeclaredReason()
    {
        var eventHubControl = A.Fake<IEventHubControl>();
        CommandConsumerOptions? captured = null;
        A.CallTo(() => eventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(
                A<string>._, A<ExecuteCommandHandler<SendNotificationsRequest>>._, A<CommandConsumerOptions>._))
            .Invokes((string _, ExecuteCommandHandler<SendNotificationsRequest> _, CommandConsumerOptions o) =>
                captured = o)
            .Returns(A.Fake<EndpointHandle>());

        await new FromSendNotificationNode(eventHubControl).StartAsync(CreateTriggerContext());

        Assert.NotNull(captured);
        Assert.Equal(FromSendNotificationNode.ExclusiveByDesignReason, captured.ExclusiveByDesignReason);
        Assert.Contains("AB#8405", FromSendNotificationNode.ExclusiveByDesignReason, StringComparison.Ordinal);
        // Nothing that would change the topology: no migration, no bridge probing, no consume-topology override.
        Assert.False(captured.MigratesLegacyCommand);
        Assert.Null(captured.LegacyProbeInterval);
        Assert.False(captured.ConfigureConsumeTopology);
        A.CallTo(() => eventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(
                A<string>._, A<ExecuteCommandHandler<SendNotificationsRequest>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task FromSendNotification_RegisteringAPipeline_LogsNoDeprecationWarning_AndKeepsTheClassicQueue()
    {
        using var harness = new EventHubHarness();
        var context = CreateTriggerContext();
        var expectedQueue =
            $"{InstancePrefix}-{QueueNames.SendNotificationCommand.ToLower()}-{context.TenantId.ToLower()}" +
            $"-data-flow-{context.DataFlowRtId.ToString()!.ToLower()}";

        await new FromSendNotificationNode(harness.EventHubControl).StartAsync(context);

        Assert.Contains(expectedQueue, harness.ConnectedQueues);
        Assert.DoesNotContain(harness.ConnectedQueues, q => q.Contains("::shared", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logs, l => l.Message.Contains(DeprecationMarker, StringComparison.Ordinal));
        Assert.Contains(harness.Logs, l => l.Level == LogLevel.Debug &&
                                           l.Message.Contains(expectedQueue, StringComparison.Ordinal) &&
                                           l.Message.Contains(FromSendNotificationNode.ExclusiveByDesignReason,
                                               StringComparison.Ordinal));
    }

    [Fact]
    public async Task FromSendNotification_RegisteringAPipeline_KeepsTheMessageTypeBinding()
    {
        // SendNotificationsRequest is published (identity-services, NotificationService), never sent to the
        // per-data-flow address: it reaches the queue only through the message-type binding that MassTransit's
        // default consume topology creates. Switching the consume topology off would silently drop every mail.
        using var harness = new EventHubHarness();

        await new FromSendNotificationNode(harness.EventHubControl).StartAsync(CreateTriggerContext());

        var configurator = ConfigureSingleEndpoint(harness);
        A.CallTo(configurator).MustHaveHappened();
        Assert.False(ConsumeTopologySwitchedOff(configurator));
    }

    [Fact]
    public void Control_ASharedRegistrationInTheSameHarness_SwitchesTheMessageTypeBindingOff()
    {
        // Proves the assertion above can fail: the plain shared options turn the consume topology off.
        using var harness = new EventHubHarness();

        harness.EventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(
            $"ab8405-shared-control-{Guid.NewGuid():N}", (_, _) => Task.CompletedTask, new CommandConsumerOptions());

        Assert.True(ConsumeTopologySwitchedOff(ConfigureSingleEndpoint(harness)));
    }

    private static IReceiveEndpointConfigurator ConfigureSingleEndpoint(EventHubHarness harness)
    {
        var configurator = A.Fake<IReceiveEndpointConfigurator>();
        var configure = Assert.Single(harness.EndpointConfigurations);
        configure(configurator);
        return configurator;
    }

    private static bool ConsumeTopologySwitchedOff(IReceiveEndpointConfigurator configurator) =>
        Fake.GetCalls(configurator).Any(call =>
            call.Method.Name == "set_" + nameof(IReceiveEndpointConfigurator.ConfigureConsumeTopology) &&
            Equals(call.Arguments[0], false));

    [Fact]
    public void Control_AClassicRegistrationInTheSameHarness_LogsTheDeprecationWarning()
    {
        using var harness = new EventHubHarness();
        // Unique name: the event hub warns once per command name and process.
        var commandName = $"ab8405-control-{Guid.NewGuid():N}";

        harness.EventHubControl.RegisterCommandConsumer<SendNotificationsRequest>(commandName,
            (_, _) => Task.CompletedTask);

        Assert.Contains(harness.Logs, l => l.Level == LogLevel.Warning &&
                                           l.Message.Contains(DeprecationMarker, StringComparison.Ordinal) &&
                                           l.Message.Contains(commandName, StringComparison.Ordinal));
    }

    private static ITriggerContext CreateTriggerContext()
    {
        var context = A.Fake<ITriggerContext>();
        A.CallTo(() => context.TenantId).Returns("Tenant-AB8405");
        A.CallTo(() => context.DataFlowRtId).Returns(OctoObjectId.GenerateNewId());
        A.CallTo(() => context.PipelineRtEntityId).Returns(
            new RtEntityId(new RtCkId<CkTypeId>("System.Communication/Pipeline"), OctoObjectId.GenerateNewId()));
        return context;
    }

    /// <summary>
    ///     The event hub's own <see cref="IEventHubControl" /> with a fake MassTransit bus (records the receive endpoint
    ///     names and their configure actions) and a log sink. No broker connection is made.
    /// </summary>
    private sealed class EventHubHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CapturingLoggerProvider _logs = new();

        public EventHubHarness()
        {
            var bus = A.Fake<IBusControl>();
            A.CallTo(() => bus.ConnectReceiveEndpoint(A<string>._, A<Action<IReceiveEndpointConfigurator>>._))
                .Invokes((string queueName, Action<IReceiveEndpointConfigurator>? configure) =>
                {
                    ConnectedQueues.Add(queueName);
                    if (configure != null)
                    {
                        EndpointConfigurations.Add(configure);
                    }
                })
                .Returns(A.Fake<HostReceiveEndpointHandle>());

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(_logs));
            services.Configure<DistributionEventHubOptions>(o => o.InstancePrefix = InstancePrefix);
            services.AddDistributionEventHub(config =>
            {
                config.UniqueServiceAddress = "ab8405-tests";
                config.AutomaticallyStartBusDuringStartup = false;
            });
            // Replace the MassTransit bus: the control only connects receive endpoints on it.
            services.AddSingleton(bus);
            _provider = services.BuildServiceProvider();
            EventHubControl = _provider.GetRequiredService<IEventHubControl>();
        }

        public IEventHubControl EventHubControl { get; }

        public ConcurrentBag<string> ConnectedQueues { get; } = [];

        public ConcurrentBag<Action<IReceiveEndpointConfigurator>> EndpointConfigurations { get; } = [];

        public IReadOnlyCollection<(LogLevel Level, string Message)> Logs => _logs.Entries.ToArray();

        public void Dispose() => _provider.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
