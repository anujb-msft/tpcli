using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

internal sealed class ApiHarness : IAsyncDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly ConcurrentBag<WebSocket> sockets = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> requests = new();
    public WebApplication Application { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string DatabasePath => Path.Combine(directory, "api.db");

    public async Task InitializeAsync(Action<Dictionary<string, string?>>? change = null,
        Func<Task>? afterSocketCleanup = null)
    {
        Directory.CreateDirectory(directory);
        var settings = new Dictionary<string, string?>
        {
            ["Tpcli:Mode"] = "local-fake",
            ["Tpcli:FakeToken"] = token,
            ["Tpcli:Store:Provider"] = "sqlite",
            ["Tpcli:Store:ConnectionString"] = "Data Source=" + DatabasePath,
            ["Tpcli:Fake:ConnectDelayMilliseconds"] = "10",
            ["urls"] = "http://127.0.0.1:5080"
        };
        change?.Invoke(settings);
        Application = RuntimeApplication.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(settings);
            builder.WebHost.UseTestServer();
        });
        Application.Use(async (context, next) =>
        {
            var id = Guid.NewGuid();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            requests[id] = finished;
            try { await next(context); }
            finally
            {
                try
                {
                    if (afterSocketCleanup is not null && context.WebSockets.IsWebSocketRequest)
                        await afterSocketCleanup();
                }
                finally
                {
                    requests.TryRemove(id, out _);
                    finished.TrySetResult();
                }
            }
        });
        await Application.StartAsync();
        Client = NewClient();
    }
    public HttpClient NewClient(bool authorized = true)
    {
        var client = Application.GetTestClient();
        if (authorized) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    public async Task<SessionInfo> CreateSessionAsync()
    {
        var response = await Client.PostAsync("/v1/sessions", new StringContent(""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionInfo>(Protocol.Json))!;
    }
    public async Task<WebSocket> ConnectAsync(string sessionId, bool authorized = true)
    {
        var client = Application.GetTestServer().CreateWebSocketClient();
        if (authorized) client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        var socket = await client.ConnectAsync(new Uri("ws://localhost/v1/sessions/" + sessionId + "/control"), CancellationToken.None);
        sockets.Add(socket);
        return socket;
    }
    public static async Task<EventEnvelope> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult read;
        do
        {
            read = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, read.MessageType);
            body.Write(buffer, 0, read.Count);
            Assert.True(body.Length <= RuntimeSettings.MaxRequestBytes + 1024);
        } while (!read.EndOfMessage);
        return JsonSerializer.Deserialize<EventEnvelope>(body.ToArray(), Protocol.Json)!;
    }
    public static Task SendAsync(WebSocket socket, JsonObject message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json), WebSocketMessageType.Text, true, CancellationToken.None);
    public static CommandRequest StartRequest(SessionInfo session, string? key = null) =>
        new(Protocol.Version, session.SessionId, "calls.start", key ?? Guid.NewGuid().ToString("N"),
            new JsonObject { ["target"] = "pstn:+12025550123", ["task"] = "Ask for public hours.", ["max_duration_seconds"] = 30 });
    public async Task<CommandReceipt> StartAsync(SessionInfo session)
    {
        var response = await Client.PostAsJsonAsync("/v1/commands", StartRequest(session), Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommandReceipt>(Protocol.Json))!;
    }
    public Task<CallState?> StateAsync(string callId) => Client.GetFromJsonAsync<CallState>("/v1/calls/" + callId, Protocol.Json);

    public async ValueTask DisposeAsync()
    {
        if (Application is not null)
        {
            foreach (var socket in sockets)
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
                    socket.Abort();
                socket.Dispose();
            }
            // TestServer disposal does not wait for a WebSocket handler's finally block.
            // Its owner revocation must finish before disposing/deleting the control store.
            using (var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            {
                while (!requests.IsEmpty)
                    await Task.WhenAll(requests.Values.Select(request => request.Task)).WaitAsync(requestTimeout.Token);
            }
            await Application.StopAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await Application.Services.GetRequiredService<TerminationEngine>().DrainAsync(timeout.Token);
            await Application.DisposeAsync();
        }
        Client?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task HarnessDrainsSocketCleanupBeforeStoppingHostOrDeletingStore()
    {
        var h = new ApiHarness();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposal = null;
        string? sessionId = null;
        string? observedStatus = null;
        try
        {
            await h.InitializeAsync(afterSocketCleanup: async () =>
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var store = h.Application.Services.GetRequiredService<ControlStore>();
                observedStatus = (await store.TransactionAsync(tx => tx.SessionAsync(sessionId!)))?.Status;
            });
            using var stopping = h.Application.Lifetime.ApplicationStopping.Register(() => hostStopping.TrySetResult());
            var session = await h.CreateSessionAsync();
            sessionId = session.SessionId;
            using var socket = await h.ConnectAsync(sessionId);
            await ApiHarness.ReceiveAsync(socket);
            disposal = h.DisposeAsync().AsTask();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(hostStopping.Task.IsCompleted, "Host shutdown overtook an unfinished WebSocket request.");
            Assert.False(disposal.IsCompleted, "Harness disposal overtook an unfinished WebSocket request.");
            Assert.True(File.Exists(h.DatabasePath));
            releaseCleanup.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal("revoked", observedStatus);
            Assert.True(hostStopping.Task.IsCompleted);
            Assert.False(Directory.Exists(Path.GetDirectoryName(h.DatabasePath)));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (disposal is not null) await disposal;
            else await h.DisposeAsync();
        }
    }

    [Fact]
    public async Task AllV1AndInjectionEndpointsRequireTheInjectedBearerToken()
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        using var anonymous = h.NewClient(false);
        foreach (var path in new[] { "/v1/capabilities", "/v1/commands/missing", "/v1/calls/missing",
            "/v1/calls/missing/events", "/v1/calls/missing/approvals", "/v1/sessions/missing/control" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/sessions", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/test/calls/missing/signals",
            new ProviderSignal("disconnected", []), Protocol.Json)).StatusCode);
        using var wrong = h.NewClient(false);
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', 40));
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/v1/capabilities")).StatusCode);
        var capabilities = await h.Client.GetFromJsonAsync<ProviderCapabilities>("/v1/capabilities?online=true", Protocol.Json);
        Assert.Equal("local-fake", capabilities!.Mode);
        Assert.Equal("simulation", capabilities.Route);
    }

    [Fact]
    public async Task OneOwningSocketFullEventEnvelopesAndCommandClientExitDoesNotEndOwnership()
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        var noOwner = await h.Client.PostAsJsonAsync("/v1/commands", ApiHarness.StartRequest(session), Protocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, noOwner.StatusCode);
        using var socket = await h.ConnectAsync(session.SessionId);
        var ready = await ApiHarness.ReceiveAsync(socket);
        Assert.Equal("session.ready", ready.Type);
        Assert.Equal(Protocol.Version, ready.SchemaVersion);
        Assert.Null(ready.CallId);
        Assert.Equal(0, ready.Sequence);
        Assert.Equal(session.SessionId, Safe.Text(ready.Payload["session"]!.AsObject(), "session_id"));
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(session.SessionId));
        var receipt = await h.StartAsync(session);
        Assert.Equal("accepted", receipt.Status);
        Assert.NotNull(receipt.CallId);
        h.Client.Dispose();
        using var observer = h.NewClient();
        var seen = new List<EventEnvelope>();
        do
        {
            var envelope = await ApiHarness.ReceiveAsync(socket);
            Assert.Equal(session.SessionId, envelope.SessionId);
            Assert.Equal(receipt.CallId, envelope.CallId);
            seen.Add(envelope);
        } while (!seen.Any(e => e.Type == "transcript.final"));
        Assert.Contains(seen, e => e.Type == "call.state_changed"
            && Safe.Text(e.Payload["state"]!.AsObject(), "lifecycle") == "connected");
        Assert.Contains(seen, e => e.Type == "command.accepted"
            && Safe.Text(e.Payload["receipt"]!.AsObject(), "command_id") == receipt.CommandId);
        Assert.Equal(Enumerable.Range(1, seen.Count).Select(i => (long)i), seen.Select(e => e.Sequence));
        await ApiHarness.SendAsync(socket, new JsonObject { ["type"] = "ack", ["call_id"] = receipt.CallId, ["sequence"] = seen[^1].Sequence });
        await ApiHarness.SendAsync(socket, new JsonObject { ["type"] = "heartbeat" });
        var state = await observer.GetFromJsonAsync<CallState>("/v1/calls/" + receipt.CallId, Protocol.Json);
        Assert.Equal("connected", state!.Lifecycle);
        var status = await observer.GetFromJsonAsync<CommandReceipt>("/v1/commands/" + receipt.CommandId, Protocol.Json);
        Assert.Equal("succeeded", status!.Status);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "owner-exit", CancellationToken.None);
        await Harness.EventuallyAsync(async () =>
            (await observer.GetFromJsonAsync<CallState>("/v1/calls/" + receipt.CallId, Protocol.Json))!.Lifecycle == "ended");
        await Assert.ThrowsAnyAsync<Exception>(() => h.ConnectAsync(session.SessionId));
    }

    [Fact]
    public async Task InvalidControlMessageRevokesInsteadOfAcceptingOtherControlOperations()
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        using var socket = await h.ConnectAsync(session.SessionId);
        await ApiHarness.ReceiveAsync(socket);
        var receipt = await h.StartAsync(session);
        await Harness.EventuallyAsync(async () => (await h.StateAsync(receipt.CallId!))!.Lifecycle == "connected");
        await ApiHarness.SendAsync(socket, new JsonObject { ["type"] = "resume", ["call_id"] = receipt.CallId });
        await Harness.EventuallyAsync(async () => (await h.StateAsync(receipt.CallId!))!.Lifecycle == "ended");
        Assert.Equal("owner_disconnected", (await h.StateAsync(receipt.CallId!))!.TerminationReason);
    }

    [Fact]
    public async Task FakeSignalEndpointIsAuthenticatedOwnedAndARealProviderSignalPath()
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        using var socket = await h.ConnectAsync(session.SessionId);
        await ApiHarness.ReceiveAsync(socket);
        var receipt = await h.StartAsync(session);
        await Harness.EventuallyAsync(async () => (await h.StateAsync(receipt.CallId!))!.Lifecycle == "connected");
        var signal = new ProviderSignal("tool.requested", new JsonObject
        {
            ["tool_call_id"] = "fake-provider-request",
            ["name"] = "request_approval",
            ["arguments"] = new JsonObject
            {
                ["action"] = "confirm_test",
                ["description"] = "Confirm a simulated action.",
                ["material_terms"] = new JsonObject { ["charge"] = 0 }
            }
        }, "fake-provider-event");
        var response = await h.Client.PostAsJsonAsync("/test/calls/" + receipt.CallId + "/signals", signal, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        ApprovalView[]? approvals = null;
        await Harness.EventuallyAsync(async () =>
        {
            approvals = await h.Client.GetFromJsonAsync<ApprovalView[]>("/v1/calls/" + receipt.CallId + "/approvals", Protocol.Json);
            return approvals!.Length == 1;
        });
        Assert.Equal("pending", approvals![0].Status);
        Assert.Equal("Confirm a simulated action.", approvals[0].Description);
        var missing = await h.Client.PostAsJsonAsync("/test/calls/unknown/signals", signal, Protocol.Json);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var closed = await h.Client.DeleteAsync("/v1/sessions/" + session.SessionId);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        await Harness.EventuallyAsync(async () => (await h.StateAsync(receipt.CallId!))!.Lifecycle == "ended");
        var result = await h.Client.GetFromJsonAsync<EventBatch>("/v1/calls/" + receipt.CallId + "/events?after=0&wait_seconds=0", Protocol.Json);
        Assert.Contains(result!.Events, e => e.Type == "call.result");
    }

    [Fact]
    public async Task RequestLimitsStrictShapesAndSanitizedErrorsAreEnforced()
    {
        await using var h = new ApiHarness();
        await h.InitializeAsync();
        var session = await h.CreateSessionAsync();
        using var socket = await h.ConnectAsync(session.SessionId);
        await ApiHarness.ReceiveAsync(socket);
        var oversized = await h.Client.PostAsync("/v1/commands", new StringContent(new string('a', 65537), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        var request = ApiHarness.StartRequest(session);
        request.Payload["task"] = new string('\u03bb', RuntimeSettings.MaxTaskBytes / 2 + 1);
        var task = await h.Client.PostAsJsonAsync("/v1/commands", request, Protocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, task.StatusCode);
        var malformed = await h.Client.PostAsync("/v1/commands", new StringContent("{PRIVATE_INVALID_INPUT", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("PRIVATE_INVALID_INPUT", await malformed.Content.ReadAsStringAsync());
        var unknown = Safe.Json(ApiHarness.StartRequest(session));
        unknown["credential"] = "PRIVATE_UNEXPECTED_FIELD";
        var response = await h.Client.PostAsJsonAsync("/v1/commands", unknown, Protocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("PRIVATE_UNEXPECTED_FIELD", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/v1/capabilities?online=not-a-boolean")).StatusCode);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://example.invalid:5080")]
    [InlineData("https://127.0.0.1:5080")]
    public async Task FakeModeRejectsNonLoopbackOrNonHttpListeners(string url)
    {
        await using var h = new ApiHarness();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.InitializeAsync(values => values["urls"] = url));
        Assert.Equal("UNSAFE_LISTEN_URL", error.Message);
    }

    [Fact]
    public void HostedAzureRejectsSqliteAndFakeCredentialsBeforeAnyConnection()
    {
        var settings = new RuntimeSettings
        {
            Mode = "azure",
            TenantId = Guid.NewGuid().ToString("D"),
            Audience = "api://tpcli",
            Store = new StoreSettings { Provider = "sqlite", ConnectionString = "Data Source=never-created.db" }
        };
        Assert.Equal("AZURE_REQUIRES_POSTGRES_CONTROL_STORE", Assert.Throws<InvalidOperationException>(() => settings.Validate(true)).Message);
        settings.Store.Provider = "postgres";
        settings.Store.ConnectionString = "Host=example.invalid;Database=tpcli";
        settings.FakeToken = new string('x', 32);
        Assert.Equal("FAKE_CONFIGURATION_FORBIDDEN_IN_AZURE", Assert.Throws<InvalidOperationException>(() => settings.Validate(true)).Message);
    }
}
