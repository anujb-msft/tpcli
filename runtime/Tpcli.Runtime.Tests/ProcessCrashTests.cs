using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class ProcessCrashTests
{
    [Fact]
    public async Task ASeparateWatchdogSurvivesRuntimeSigkillAndConfirmsOnlySimulationTermination()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new RuntimeSettings
        {
            Mode = "local-fake",
            Store = new StoreSettings
            {
                Provider = "sqlite",
                ConnectionString = "Data Source=" + Path.Combine(directory, "crash.db")
            }
        };
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var address = "http://127.0.0.1:" + port;
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        Process? runtime = null;
        Process? watchdog = null;
        var readers = new List<Task<string>>();
        try
        {
            runtime = Start("Tpcli.Runtime", directory, settings, token, address);
            readers.Add(runtime.StandardOutput.ReadToEndAsync());
            readers.Add(runtime.StandardError.ReadToEndAsync());
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(2) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await Harness.EventuallyAsync(async () =>
            {
                Assert.False(runtime.HasExited, "Fake runtime exited before becoming ready.");
                try { return (await client.GetAsync("/healthz")).IsSuccessStatusCode; }
                catch (HttpRequestException) { return false; }
            }, 8000);
            var sessionResponse = await client.PostAsync("/v1/sessions", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
            var session = (await sessionResponse.Content.ReadFromJsonAsync<SessionInfo>(Protocol.Json))!;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/v1/sessions/" + session.SessionId + "/control"),
                CancellationToken.None);
            Assert.Equal("session.ready", (await ApiHarness.ReceiveAsync(socket)).Type);
            var response = await client.PostAsJsonAsync("/v1/commands", ApiHarness.StartRequest(session), Protocol.Json);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = (await response.Content.ReadFromJsonAsync<CommandReceipt>(Protocol.Json))!;
            await Harness.EventuallyAsync(async () =>
                (await client.GetFromJsonAsync<CallState>("/v1/calls/" + receipt.CallId, Protocol.Json))!.Lifecycle == "connected");
            watchdog = Start("Tpcli.Watchdog", directory, settings);
            readers.Add(watchdog.StandardOutput.ReadToEndAsync());
            readers.Add(watchdog.StandardError.ReadToEndAsync());
            await Task.Delay(200);
            Assert.False(watchdog.HasExited, "Independent watchdog failed to remain running.");
            runtime.Kill(entireProcessTree: true);
            await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            socket.Abort();
            using var inspection = new ControlStore(settings, TimeProvider.System);
            CallRecord? result = null;
            await Harness.EventuallyAsync(async () =>
            {
                Assert.False(watchdog.HasExited, "Watchdog exited after runtime loss.");
                result = await inspection.TransactionAsync(tx => tx.CallAsync(receipt.CallId!));
                return result?.State.Lifecycle == "ended";
            }, 9000);
            Assert.Equal("confirmed", result!.State.HangupStatus);
            Assert.Equal("worker_lost", result.State.TerminationReason);
            Assert.Equal("local-fake", result.State.ProviderMode);
            Assert.Equal("simulation", result.State.Route);
            var attempts = await inspection.TransactionAsync(tx => tx.AttemptsAsync(receipt.CallId!));
            Assert.Contains(attempts, a => a.Executor.StartsWith("watchdog_", StringComparison.Ordinal)
                && a.Status == "confirmed" && a.ProviderMode == "local-fake" && a.CompletedAt is not null);
            Assert.Equal(1, (await inspection.TransactionAsync(tx => tx.FakeCallAsync("fake:" + receipt.CallId)))!.DialCount);
        }
        finally
        {
            foreach (var process in new[] { runtime, watchdog }.OfType<Process>())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                process.Dispose();
            }
            foreach (var reader in readers)
            {
                var output = await reader;
                Assert.DoesNotContain(token, output);
                Assert.DoesNotContain("Ask for public hours.", output);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process Start(string project, string directory, RuntimeSettings settings, string? token = null, string? address = null)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var dll = Path.Combine(root, project, "bin", configuration, "net10.0", project + ".dll");
        Assert.True(File.Exists(dll), "Required executable was not built.");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(dll);
        start.Environment["Tpcli__Mode"] = settings.Mode;
        start.Environment["Tpcli__Store__Provider"] = settings.Store.Provider;
        start.Environment["Tpcli__Store__ConnectionString"] = settings.Store.ConnectionString;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        if (token is not null) start.Environment["Tpcli__FakeToken"] = token;
        if (address is not null) start.Environment["ASPNETCORE_URLS"] = address;
        return Process.Start(start)!;
    }
}
