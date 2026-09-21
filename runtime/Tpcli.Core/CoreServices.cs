using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tpcli.Contracts;

namespace Tpcli.Core;

public static class CoreServices
{
    public static IServiceCollection AddTpcliControlStore(this IServiceCollection services, RuntimeSettings settings)
    {
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ControlStore>();
        services.AddSingleton<DurableCallCorrelation>();
        services.AddSingleton<EventJournal>();
        services.AddSingleton<TerminationEngine>();
        if (settings.Mode == "local-fake")
        {
            services.AddSingleton<ICallTerminator, FakeCallTerminator>();
            services.AddSingleton<ICallProviderFactory, FakeCallProvider>();
        }
        return services;
    }

    public static IServiceCollection AddTpcliCallRuntime(this IServiceCollection services)
    {
        services.AddSingleton<CallRuntime>();
        services.AddSingleton(provider => new DurableMediaGrants(
            provider.GetRequiredService<ControlStore>(),
            () => provider.GetRequiredService<CallRuntime>().WorkerId));
        services.AddSingleton<IProviderEventSink>(provider =>
            new DeferredSink(() => provider.GetRequiredService<CallRuntime>()));
        services.AddHostedService<RuntimeSupervisor>();
        return services;
    }

    private sealed class DeferredSink(Func<CallRuntime> runtime) : IProviderEventSink
    {
        public Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default) =>
            runtime().PublishAsync(callId, signal, cancellationToken);
    }
}

public sealed class RuntimeSupervisor(CallRuntime runtime, ILogger<RuntimeSupervisor> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await runtime.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do
        {
            try { await runtime.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("CONTROL_SUPERVISION_UNAVAILABLE"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await runtime.DisposeAsync();
    }
}
