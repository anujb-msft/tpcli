using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tpcli.Contracts;

namespace Tpcli.Core;

internal sealed class CallActor : IAsyncDisposable
{
    private abstract record Work;
    private sealed record Begin : Work;
    private sealed record Prepared(ICallConnection Connection) : Work;
    private sealed record Dialed(ProviderHandle Handle) : Work;
    private sealed record Failed(string Code, bool Ambiguous) : Work;
    private sealed record Signal(ProviderSignal Value) : Work;
    private sealed record Command(CommandReceipt Receipt, CommandRequest Request, Caller Caller) : Work;
    private sealed record Tick : Work;

    private readonly CallRuntime runtime;
    private readonly long fence;
    private readonly string sessionId;
    private CallContext? context;
    private readonly Channel<Work> queue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource actions = new();
    private readonly SemaphoreSlim providerSerial = new(1);
    private readonly Dictionary<string, string> approvalTools = [];
    private ICallConnection? connection;
    private Task? loop;
    private int pendingOperations;
    private int tickQueued;
    private DateTimeOffset? completedAt;
    public string CallId { get; }

    public CallActor(CallRuntime runtime, CallRecord call, CallContext context)
    {
        this.runtime = runtime;
        this.context = context;
        CallId = call.State.CallId;
        sessionId = call.State.SessionId;
        fence = call.Fence;
        queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(runtime.Settings.ActorQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Start()
    {
        queue.Writer.TryWrite(new Begin());
        loop = RunAsync();
    }
    public bool PostCommand(CommandReceipt receipt, CommandRequest request, Caller caller) =>
        queue.Writer.TryWrite(new Command(receipt, request, caller));
    public bool PostSignal(ProviderSignal signal) => queue.Writer.TryWrite(new Signal(signal));
    public void PostTick()
    {
        if (Interlocked.CompareExchange(ref tickQueued, 1, 0) == 0
            && !queue.Writer.TryWrite(new Tick())) Interlocked.Exchange(ref tickQueued, 0);
    }
    public void CancelActions() => actions.Cancel();

    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                try
                {
                    switch (work)
                    {
                        case Begin: await BeginAsync(); break;
                        case Prepared prepared: await PreparedAsync(prepared.Connection); break;
                        case Dialed dialed: await DialedAsync(dialed.Handle); break;
                        case Failed failed: await FailedAsync(failed); break;
                        case Signal signal: await SignalAsync(signal.Value); break;
                        case Command command: await CommandAsync(command); break;
                        case Tick:
                            Interlocked.Exchange(ref tickQueued, 0);
                            await TickAsync();
                            break;
                    }
                }
                catch (ControlException error)
                {
                    if (work is Command command)
                        await runtime.CompleteCommandAsync(CallId, command.Receipt.CommandId, error.Code);
                    else if (error.Code is "TOOL_ID_CONFLICT" or "PROVIDER_HANDLE_CONFLICT")
                        await runtime.StopAsync(CallId, "provider_error");
                    else if (work is Begin or Prepared)
                        await runtime.StopAsync(CallId, "owner_disconnected");
                    else if (work is Signal && error.Code is not ("SESSION_REVOKED" or "WORKER_FENCED"
                        or "CALL_ENDED" or "CALL_NOT_CONNECTED" or "DEADLINE_EXPIRED"))
                        await runtime.StopAsync(CallId, "provider_error");
                }
                catch (OperationCanceledException) when (actions.IsCancellationRequested || lifetime.IsCancellationRequested) { }
                catch
                {
                    if (work is Command command)
                        await runtime.CompleteCommandAsync(CallId, command.Receipt.CommandId, "RUNTIME_FAILURE");
                    await runtime.StopAsync(CallId, "provider_error");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            CancelActions();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await HangupAsync(timeout.Token);
            }
            catch { }
        }
    }

    private async Task BeginAsync()
    {
        await runtime.Store.TransactionAsync(async tx =>
        {
            var call = await RequiredCallAsync(tx);
            await tx.GuardActionAsync(call, runtime.WorkerId, fence);
            call.State = call.State with { Lifecycle = "preflight" };
            await tx.EmitStateAsync(call);
            return true;
        });
        runtime.Journal.Notify(sessionId);
        var callContext = context!;
        context = null;
        _ = PrepareAsync(callContext);
    }

    private async Task PrepareAsync(CallContext callContext)
    {
        try
        {
            await GuardAsync();
            var prepared = await runtime.Provider.PrepareAsync(callContext, actions.Token);
            if (!queue.Writer.TryWrite(new Prepared(prepared)))
            {
                try { await prepared.HangupAsync(CancellationToken.None); }
                finally { await prepared.DisposeAsync(); }
                await runtime.StopAsync(CallId, "media_error");
            }
        }
        catch (ProviderException error) { await PostInternalAsync(new Failed(Safe.ProviderCode(error.Code), error.MayHaveDispatched)); }
        catch (OperationCanceledException) { await PostInternalAsync(new Failed("PREFLIGHT_CANCELLED", false)); }
        catch (ControlException) { await PostInternalAsync(new Failed("SESSION_REVOKED", false)); }
        catch { await PostInternalAsync(new Failed("PREFLIGHT_FAILED", false)); }
    }

    private async Task PreparedAsync(ICallConnection prepared)
    {
        connection = prepared;
        try
        {
            await runtime.Store.TransactionAsync(async tx =>
            {
                var call = await RequiredCallAsync(tx);
                await tx.GuardActionAsync(call, runtime.WorkerId, fence);
                if (call.DispatchStatus != "none" || call.State.Lifecycle != "preflight")
                    throw new ControlException("DIAL_ALREADY_DISPATCHED");
                call.DispatchStatus = "intent";
                call.State = call.State with { Lifecycle = "dialing", HangupStatus = "pending" };
                await tx.EmitStateAsync(call);
                return true;
            });
            runtime.Journal.Notify(sessionId);
            _ = DialAsync(prepared);
        }
        catch (ControlException)
        {
            await runtime.StopAsync(CallId, "owner_disconnected");
            await prepared.DisposeAsync();
        }
    }

    private async Task DialAsync(ICallConnection prepared)
    {
        var invoked = false;
        try
        {
            await GuardAsync();
            actions.Token.ThrowIfCancellationRequested();
            invoked = true;
            var handle = await prepared.DialAsync(actions.Token);
            // Retain the handle even when cancellation/fencing wins the race with DialAsync.
            await runtime.Store.TransactionAsync(async tx =>
            {
                var call = await RequiredCallAsync(tx);
                await CallRuntime.RetainHandleAsync(tx, call, new JsonObject
                {
                    ["connection_id"] = handle.ConnectionId,
                    ["server_call_id"] = handle.ServerCallId
                });
                call.DispatchStatus = "returned";
                await tx.SaveCallAsync(call);
                return true;
            });
            await PostInternalAsync(new Dialed(handle));
        }
        catch (ProviderException error) { await PostInternalAsync(new Failed(Safe.ProviderCode(error.Code), error.MayHaveDispatched)); }
        catch (ControlException error) { await PostInternalAsync(new Failed(error.Code, invoked)); }
        catch (OperationCanceledException) { await PostInternalAsync(new Failed("DIAL_CANCELLED", invoked)); }
        catch { await PostInternalAsync(new Failed("PROVIDER_FAILURE", invoked)); }
    }

    private async Task DialedAsync(ProviderHandle handle)
    {
        var allowed = await runtime.Store.TransactionAsync(async tx =>
        {
            var call = await RequiredCallAsync(tx);
            try { await tx.GuardActionAsync(call, runtime.WorkerId, fence); return true; }
            catch (ControlException) { return false; }
        });
        if (!allowed) await runtime.StopAsync(CallId, "owner_disconnected", containLateConnection: true);
    }

    private async Task FailedAsync(Failed failure)
    {
        var needsTermination = await runtime.Store.TransactionAsync(async tx =>
        {
            var call = await RequiredCallAsync(tx);
            if (Safe.Terminal(call.State)) return false;
            if (!failure.Ambiguous && call.Handle is null) call.DispatchStatus = "none";
            if (failure.Ambiguous || call.Handle is not null)
            {
                call.DispatchStatus = "ambiguous";
                call.State = call.State with
                {
                    Lifecycle = "termination_unknown",
                    HangupStatus = "unknown",
                    TerminationReason = call.State.TerminationReason ?? "provider_error"
                };
                await tx.EmitStateAsync(call, "call.failed");
            }
            else
            {
                call.State = call.State with
                {
                    Lifecycle = "failed_before_connect",
                    HangupStatus = "not_applicable",
                    TerminationReason = call.State.TerminationReason ?? "provider_error"
                };
                call.CompletedAt = tx.Now;
                completedAt = tx.Now;
                TerminationEngine.FinalizeOutcome(call);
                await tx.InvalidateApprovalsAsync(call);
                await tx.EmitStateAsync(call, "call.failed");
                await tx.EmitStateAsync(call, "call.result");
            }
            foreach (var command in await tx.PendingCommandsAsync(CallId))
            {
                var receipt = command.Receipt with
                {
                    Status = "failed",
                    UpdatedAt = tx.Now,
                    Error = new WireError(failure.Code, failure.Code)
                };
                await tx.SaveCommandAsync(command with { Receipt = receipt });
                await tx.EmitReceiptAsync(call, receipt);
            }
            return failure.Ambiguous || call.Handle is not null;
        });
        runtime.Journal.Notify(sessionId);
        if (needsTermination) await runtime.StopAsync(CallId, "provider_error");
        else
        {
            CancelActions();
            if (connection is not null) await connection.DisposeAsync();
            if (completedAt is { } at) await runtime.Journal.MarkCompletedAsync(CallId, at);
        }
    }

    private async Task SignalAsync(ProviderSignal signal)
    {
        var novel = await runtime.Store.TransactionAsync(async tx =>
        {
            await RequiredCallAsync(tx);
            return await tx.RememberProviderEventAsync(CallId, signal.ProviderEventId);
        });
        if (!novel) return;
        switch (signal.Type)
        {
            case "connected":
                var allowed = await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await CallRuntime.RetainHandleAsync(tx, call, signal.Payload);
                    try { await tx.GuardActionAsync(call, runtime.WorkerId, fence); }
                    catch (ControlException) { return false; }
                    if (call.State.Lifecycle == "connected") return true;
                    if (call.State.Lifecycle != "dialing") return false;
                    if (call.State.ProviderMode == "local-fake"
                        && await tx.FakeCallAsync("fake:" + CallId) is { Status: not "terminated" } simulation)
                        await tx.SaveFakeCallAsync(simulation with { Status = "connected" });
                    call.State = call.State with { Lifecycle = "connected", TaskOutcome = "in_progress" };
                    await tx.EmitStateAsync(call);
                    foreach (var command in (await tx.PendingCommandsAsync(CallId)).Where(c => c.Receipt.Operation == "calls.start"))
                    {
                        var receipt = command.Receipt with { Status = "succeeded", UpdatedAt = tx.Now };
                        await tx.SaveCommandAsync(command with { Receipt = receipt });
                        await tx.EmitReceiptAsync(call, receipt);
                    }
                    return true;
                });
                if (!allowed) await runtime.StopAsync(CallId, "owner_disconnected", containLateConnection: true);
                break;
            case "disconnected":
                completedAt = await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await CallRuntime.RetainHandleAsync(tx, call, signal.Payload);
                    await CallRuntime.DisconnectedAsync(tx, call);
                    return call.CompletedAt;
                });
                CancelActions();
                if (connection is not null) await connection.DisposeAsync();
                if (completedAt is { } at) await runtime.Journal.MarkCompletedAsync(CallId, at);
                break;
            case "transcript.partial":
            case "transcript.final":
            case "transcript.interrupted":
            case "transcript.gap":
                await TranscriptAsync(signal);
                break;
            case "tool.requested": await ToolAsync(signal.Payload); break;
            case "warning":
                await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await tx.EmitWarningAsync(call, Safe.Text(signal.Payload, "code") ?? "PROVIDER_FAILURE");
                    return true;
                });
                break;
            case "failure":
                await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await tx.EmitWarningAsync(call, Safe.Text(signal.Payload, "code") ?? "PROVIDER_FAILURE");
                    return true;
                });
                await runtime.StopAsync(CallId, "provider_error");
                break;
            default: throw new ControlException("UNKNOWN_PROVIDER_SIGNAL", 400);
        }
        runtime.Journal.Notify(sessionId);
    }

    private async Task TranscriptAsync(ProviderSignal signal)
    {
        var payload = signal.Payload;
        await runtime.Journal.AppendAsync(CallId, async (tx, call) =>
        {
            try { await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true); }
            catch (ControlException) { return null; }
            if (signal.Type == "transcript.gap")
            {
                call.State = call.State with { TranscriptStatus = "partial" };
                return ("transcript.gap", new JsonObject { ["reason"] = "provider_gap" });
            }
            var segment = CommandValidation.RequiredText(payload, "segment_id", 512);
            var speaker = Safe.Text(payload, "speaker");
            var delivery = Safe.Text(payload, "delivery");
            var text = Safe.Text(payload, "text");
            if (text is null || Encoding.UTF8.GetByteCount(text) > 48 * 1024
                || speaker is not ("assistant" or "recipient" or "operator")
                || delivery is not ("generated" or "sent" or "playback_estimated" or "received" or "unknown")
                || !Safe.Integer(payload["revision"], out var revision)
                || revision < 0)
                throw new ControlException("INVALID_TRANSCRIPT", 400);
            var final = signal.Type == "transcript.final";
            var interrupted = signal.Type == "transcript.interrupted"
                || payload["interrupted"] is JsonValue interruptedValue
                    && interruptedValue.TryGetValue<bool>(out var interruptedFlag) && interruptedFlag;
            if (!await tx.RememberSegmentAsync(CallId, segment, revision, final, interrupted)) return null;
            // A final segment alone is not evidence that the complete conversation was captured.
            call.State = call.State with { TranscriptStatus = "partial" };
            var result = new JsonObject
            {
                ["segment_id"] = segment,
                ["speaker"] = speaker,
                ["text"] = text,
                ["revision"] = revision,
                ["final"] = final,
                ["interrupted"] = interrupted,
                ["delivery"] = delivery
            };
            if (Safe.Text(payload, "provider_timestamp") is { Length: <= 128 } timestamp)
                result["provider_timestamp"] = timestamp;
            return (signal.Type, result);
        });
    }

    private async Task ToolAsync(JsonObject payload)
    {
        var toolId = CommandValidation.RequiredText(payload, "tool_call_id", 512);
        var name = CommandValidation.RequiredText(payload, "name", 64);
        var arguments = payload["arguments"] as JsonObject ?? throw new ControlException("INVALID_TOOL_ARGUMENTS", 400);
        if (name == "request_approval")
        {
            await RequestApprovalAsync(toolId, arguments);
            return;
        }
        var novel = await runtime.Store.TransactionAsync(async tx =>
        {
            var call = await RequiredCallAsync(tx);
            await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
            return await tx.RememberToolAsync(CallId, toolId,
                new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() });
        });
        if (!novel) return;
        switch (name)
        {
            case "send_dtmf":
                var digits = CommandValidation.RequiredText(arguments, "digits", 32);
                if (!Safe.Dtmf().IsMatch(digits)) throw new ControlException("INVALID_DTMF", 400);
                LaunchControl(async (provider, ct) =>
                {
                    await provider.SendDtmfAsync(digits, ct);
                    await GuardAsync(true);
                    await provider.CompleteToolAsync(toolId, new JsonObject { ["sent"] = true }, ct);
                });
                break;
            case "report_result":
                var outcome = Safe.Text(arguments, "outcome") ?? Safe.Text(arguments, "task_outcome");
                if (outcome is not ("completed" or "partial" or "not_completed" or "unknown"))
                    throw new ControlException("INVALID_TASK_OUTCOME", 400);
                var summary = Safe.Text(arguments, "summary");
                if (summary is not null && Encoding.UTF8.GetByteCount(summary) > RuntimeSettings.MaxTaskBytes)
                    throw new ControlException("INVALID_SUMMARY", 400);
                await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
                    call.State = call.State with { TaskOutcome = outcome, SummaryStatus = summary is null ? "unavailable" : "pending" };
                    await tx.EmitStateAsync(call);
                    return true;
                });
                if (summary is not null)
                {
                    await runtime.Journal.AppendAsync(CallId, async (tx, call) =>
                    {
                        await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
                        call.State = call.State with { SummaryStatus = "complete" };
                        return ("summary.ready", new JsonObject
                        {
                            ["summary"] = summary,
                            ["task_outcome"] = outcome,
                            ["transcript_status"] = call.State.TranscriptStatus,
                            ["provider_mode"] = call.State.ProviderMode,
                            ["status"] = "complete"
                        });
                    });
                }
                LaunchToolResponse(toolId, new JsonObject { ["recorded"] = true });
                break;
            case "end_call":
                await runtime.StopAsync(CallId, "task_finished");
                break;
            default:
                LaunchToolResponse(toolId, new JsonObject { ["error"] = "UNSUPPORTED_TOOL" });
                break;
        }
    }

    private async Task RequestApprovalAsync(string toolId, JsonObject arguments)
    {
        var action = CommandValidation.RequiredText(arguments, "action", 128);
        var description = CommandValidation.RequiredText(arguments, "description", RuntimeSettings.MaxTaskBytes);
        var terms = arguments["material_terms"] as JsonObject ?? throw new ControlException("MATERIAL_TERMS_REQUIRED", 400);
        var invalidated = new List<StoredApproval>();
        var envelope = await runtime.Journal.AppendAsync(CallId, async (tx, call) =>
        {
            await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
            if (!await tx.RememberToolAsync(CallId, toolId,
                    new JsonObject { ["name"] = "request_approval", ["arguments"] = arguments.DeepClone() }))
                return null;
            invalidated = await tx.InvalidateApprovalsAsync(call);
            call.ConversationVersion++;
            var hash = Safe.CanonicalHash(new JsonObject
            {
                ["action"] = action,
                ["material_terms"] = terms.DeepClone(),
                ["conversation_version"] = call.ConversationVersion
            });
            var timeout = runtime.Settings.Mode == "local-fake"
                ? TimeSpan.FromMilliseconds(runtime.Settings.Fake.ApprovalTimeoutMilliseconds) : TimeSpan.FromSeconds(60);
            var expiry = tx.Now + timeout < call.State.Deadline ? tx.Now + timeout : call.State.Deadline;
            var approval = new ApprovalView(Safe.Id("apr"), CallId, hash, expiry, "pending",
                call.ConversationVersion, description, terms.DeepClone().AsObject());
            await tx.SaveApprovalAsync(new StoredApproval(approval with { Description = null, MaterialTerms = null }, Safe.Hash(toolId)));
            var content = Safe.Json(approval);
            content["action"] = action;
            return ("approval.requested", content);
        });
        if (envelope is not null) approvalTools.Add(Safe.Text(envelope.Payload, "approval_id")!, toolId);
        DenyContinuations(invalidated);
    }

    private async Task CommandAsync(Command work)
    {
        switch (work.Request.Operation)
        {
            case "calls.instruct":
                var invalidated = await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
                    call.ConversationVersion++;
                    var invalidated = await tx.InvalidateApprovalsAsync(call);
                    await tx.EmitStateAsync(call);
                    return invalidated;
                });
                DenyContinuations(invalidated);
                LaunchControl((provider, ct) => provider.InstructAsync(Safe.Text(work.Request.Payload, "text")!, ct),
                    work.Receipt.CommandId);
                break;
            case "calls.dtmf":
                LaunchControl((provider, ct) => provider.SendDtmfAsync(Safe.Text(work.Request.Payload, "digits")!, ct),
                    work.Receipt.CommandId);
                break;
            case "approvals.resolve":
                var approval = await runtime.Store.TransactionAsync(async tx =>
                {
                    var call = await RequiredCallAsync(tx);
                    await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
                    var approval = await CallRuntime.ValidateApprovalAsync(tx, call, work.Request.Payload);
                    var status = Safe.Text(work.Request.Payload, "decision") == "approve" ? "approved" : "denied";
                    approval = approval with { View = approval.View with { Status = status, Actor = work.Caller.Principal } };
                    await tx.SaveApprovalAsync(approval);
                    await tx.EmitApprovalAsync(call, approval.View);
                    return approval;
                });
                if (!approvalTools.Remove(approval.View.ApprovalId, out var toolId))
                    throw new ControlException("APPROVAL_CONTENT_UNAVAILABLE");
                LaunchControl((provider, ct) => provider.CompleteToolAsync(toolId, ApprovalResult(approval.View), ct),
                    work.Receipt.CommandId, approval.View);
                break;
        }
        runtime.Journal.Notify(sessionId);
    }

    private void DenyContinuations(IEnumerable<StoredApproval> approvals)
    {
        foreach (var approval in approvals)
            if (approvalTools.Remove(approval.View.ApprovalId, out var toolId))
                LaunchToolResponse(toolId, ApprovalResult(approval.View));
    }
    private static JsonObject ApprovalResult(ApprovalView approval) => new()
    {
        ["approval_id"] = approval.ApprovalId,
        ["action_hash"] = approval.ActionHash,
        ["approved"] = approval.Status == "approved",
        ["status"] = approval.Status,
        ["conversation_version"] = approval.ConversationVersion,
        ["expires_at"] = approval.ExpiresAt
    };
    private void LaunchToolResponse(string toolId, JsonObject result) =>
        LaunchControl((provider, ct) => provider.CompleteToolAsync(toolId, result, ct));

    private void LaunchControl(Func<ICallConnection, CancellationToken, Task> action,
        string? commandId = null, ApprovalView? approval = null)
    {
        if (Interlocked.Increment(ref pendingOperations) > runtime.Settings.ActorQueueCapacity)
        {
            Interlocked.Decrement(ref pendingOperations);
            _ = commandId is null ? runtime.StopAsync(CallId, "media_error")
                : runtime.CompleteCommandAsync(CallId, commandId, "ACTOR_QUEUE_FULL");
            return;
        }
        _ = ExecuteControlAsync(action, commandId, approval);
    }

    private async Task ExecuteControlAsync(Func<ICallConnection, CancellationToken, Task> action,
        string? commandId, ApprovalView? approval)
    {
        var entered = false;
        string? error = null;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(actions.Token);
        if (approval is not null)
        {
            var remaining = approval.ExpiresAt - runtime.Clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) operation.Cancel();
            else operation.CancelAfter(remaining);
        }
        var cancellationToken = operation.Token;
        try
        {
            await providerSerial.WaitAsync(cancellationToken);
            entered = true;
            await runtime.Store.TransactionAsync(async tx =>
            {
                var call = await RequiredCallAsync(tx);
                await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected: true);
                if (approval is not null && (call.ConversationVersion != approval.ConversationVersion
                    || approval.ExpiresAt <= tx.Now))
                    throw new ControlException("STALE_APPROVAL");
                return true;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = connection ?? throw new ControlException("PROVIDER_UNAVAILABLE");
            await action(current, cancellationToken).WaitAsync(cancellationToken);
        }
        catch (ControlException exception) { error = exception.Code; }
        catch (ProviderException exception) { error = Safe.ProviderCode(exception.Code); }
        catch (OperationCanceledException) { error = approval is not null && !actions.IsCancellationRequested ? "STALE_APPROVAL" : "CALL_TERMINATED"; }
        catch { error = "PROVIDER_FAILURE"; }
        finally
        {
            if (entered) providerSerial.Release();
            Interlocked.Decrement(ref pendingOperations);
        }
        if (commandId is not null)
        {
            try { await runtime.CompleteCommandAsync(CallId, commandId, error); }
            catch { }
        }
        if (error == "STALE_APPROVAL" && approval?.Status == "approved")
        {
            try { await runtime.StopAsync(CallId, "provider_error"); }
            catch { }
        }
    }

    private async Task TickAsync()
    {
        var (expired, complete, fenced) = await runtime.Store.TransactionAsync(async tx =>
        {
            var call = await RequiredCallAsync(tx);
            var expired = new List<StoredApproval>();
            foreach (var approval in (await tx.ApprovalsAsync(CallId))
                .Where(a => a.View.Status == "pending" && a.View.ExpiresAt <= tx.Now))
            {
                var update = approval with { View = approval.View with { Status = "expired" } };
                await tx.SaveApprovalAsync(update);
                await tx.EmitApprovalAsync(call, update.View);
                expired.Add(update);
            }
            return (expired, call.CompletedAt, call.Fence != fence
                || call.State.Lifecycle is "ending" or "termination_unknown" || Safe.Terminal(call.State));
        });
        if (fenced)
        {
            CancelActions();
            _ = DisposeConnectionAsync();
        }
        else DenyContinuations(expired);
        if (expired.Count > 0) runtime.Journal.Notify(sessionId);
        completedAt ??= complete;
        if (completedAt is { } at)
        {
            await runtime.Journal.MarkCompletedAsync(CallId, at);
            if (runtime.Clock.GetUtcNow() - at >= RuntimeSettings.ReplayRetention)
            {
                // Do not await this actor's own reader from inside its loop.
                _ = runtime.ActorFinishedAsync(CallId);
            }
        }
    }

    private Task GuardAsync(bool connected = false) => runtime.Store.TransactionAsync(async tx =>
    {
        var call = await RequiredCallAsync(tx);
        await tx.GuardActionAsync(call, runtime.WorkerId, fence, connected);
        return true;
    }, actions.Token);
    private async Task<CallRecord> RequiredCallAsync(ControlTransaction tx) =>
        await tx.CallAsync(CallId) ?? throw new ControlException("NOT_FOUND", 404);
    private async Task PostInternalAsync(Work work)
    {
        if (!queue.Writer.TryWrite(work)) await runtime.StopAsync(CallId, "media_error", containLateConnection: work is Dialed);
    }
    public async Task<TerminationEvidence> HangupAsync(CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref connection, null);
        if (current is null) return TerminationEvidence.Unknown;
        try { return await current.HangupAsync(cancellationToken); }
        finally { await current.DisposeAsync(); }
    }
    private async Task DisposeConnectionAsync()
    {
        var current = Interlocked.Exchange(ref connection, null);
        if (current is null) return;
        try { await current.DisposeAsync(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        actions.Cancel();
        lifetime.Cancel();
        queue.Writer.TryComplete();
        await DisposeConnectionAsync();
        context = null;
        approvalTools.Clear();
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }
    }
}
