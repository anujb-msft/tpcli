using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tpcli.Azure;
using Tpcli.Contracts;
using Tpcli.Core;

try
{
    var once = args.Contains("--once", StringComparer.Ordinal);
    var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--once").ToArray());
    var settings = RuntimeSettings.Load(builder.Configuration);
    settings.Validate(httpServer: false);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft", LogLevel.None);
    builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
    builder.Services.AddTpcliControlStore(settings);
    builder.Services.AddSingleton<IProviderEventSink, NoCallbacks>();
    if (settings.Mode == "azure") builder.Services.AddTpcliAzure(builder.Configuration);
    if (!once) builder.Services.AddHostedService<WatchdogService>();
    using var host = builder.Build();
    if (once)
    {
        var store = host.Services.GetRequiredService<ControlStore>();
        await store.InitializeAsync();
        var termination = host.Services.GetRequiredService<TerminationEngine>();
        await termination.SweepAsync(Safe.Id("watchdog"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        await termination.DrainAsync(timeout.Token);
    }
    else await host.RunAsync();
}
catch
{
    Console.Error.WriteLine("WATCHDOG_STARTUP_OR_HOST_FAILURE");
    Environment.ExitCode = 1;
}

internal sealed class NoCallbacks : IProviderEventSink
{
    public Task PublishAsync(string callId, ProviderSignal signal, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("WATCHDOG_DOES_NOT_ACCEPT_CALLBACKS");
}

internal sealed class WatchdogService(ControlStore store, TerminationEngine termination, ILogger<WatchdogService> logger) : BackgroundService
{
    private readonly string executor = Safe.Id("watchdog");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(RuntimeSettings.WatchdogCadence);
        var iterations = 0;
        do
        {
            try
            {
                await termination.SweepAsync(executor, stoppingToken);
                if (++iterations % 900 == 0) await store.TransactionAsync(tx => tx.PruneAsync(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("WATCHDOG_CONTROL_STORE_UNAVAILABLE"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
