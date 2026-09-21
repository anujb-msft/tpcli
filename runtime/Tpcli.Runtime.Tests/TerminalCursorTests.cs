using System.Text.Json;
using System.Text.Json.Nodes;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class TerminalCursorTests
{
    [Theory]
    [InlineData("preflight")]
    [InlineData("dial-failure")]
    [InlineData("stop")]
    [InlineData("disconnect")]
    [InlineData("owner-lost")]
    [InlineData("deadline")]
    [InlineData("unknown")]
    public async Task TerminalSnapshotsPointThroughAllEventsToTheFinalResult(string scenario)
    {
        await using var h = new Harness(settings =>
        {
            settings.Fake.FailPreflight = scenario == "preflight";
            settings.Fake.FailDialBeforeDispatch = scenario == "dial-failure";
            settings.Fake.HangupUnknown = scenario == "unknown";
            if (scenario == "deadline") settings.Fake.TestDeadlineMilliseconds = 1000;
        });
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        if (scenario is not ("preflight" or "dial-failure"))
        {
            await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
            switch (scenario)
            {
                case "stop":
                case "unknown": await h.CommandAsync("calls.stop", receipt.CallId!); break;
                case "disconnect": await h.SignalAsync(receipt.CallId!, "disconnected"); break;
                case "owner-lost": await h.Runtime.RevokeAsync(h.Owner, h.Session.SessionId); break;
                case "deadline":
                    h.Clock.Advance(TimeSpan.FromSeconds(2));
                    await h.Runtime.TickAsync();
                    break;
            }
        }
        var state = await h.WaitStateAsync(receipt.CallId!, Safe.HasResult);
        var batch = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!);
        Assert.False(batch.HasMore);
        var result = batch.Events[^1];
        Assert.Equal("call.result", result.Type);
        Assert.Equal(state.LastSequence, result.Sequence);
        Assert.Equal(state.LastSequence, batch.CallState!.LastSequence);
        foreach (var snapshot in batch.Events.Where(e => e.Payload["state"] is JsonObject)
            .Select(e => e.Payload["state"]!.Deserialize<CallState>(Protocol.Json)!).Where(Safe.HasResult))
            Assert.Equal(result.Sequence, snapshot.LastSequence);
        if (scenario is "preflight" or "dial-failure")
            Assert.True(Assert.Single(batch.Events, e => e.Type == "command.failed").Sequence < result.Sequence);
        if (scenario == "unknown")
            Assert.Single(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    [Fact]
    public async Task LaterTerminalCommandReceiptsStillHaveADrainableFinalResult()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        await h.CommandAsync("calls.stop", receipt.CallId!);
        var previous = await h.WaitStateAsync(receipt.CallId!, Safe.Terminal);
        var stop = await h.CommandAsync("calls.stop", receipt.CallId!);
        await Harness.EventuallyAsync(async () => (await h.Runtime.CommandAsync(h.Owner, stop.CommandId)).Status == "succeeded");
        var batch = await h.Runtime.EventsAsync(h.Owner, receipt.CallId!, previous.LastSequence);
        Assert.Equal("call.result", batch.Events[^1].Type);
        Assert.Equal(batch.Events[^1].Sequence, batch.CallState!.LastSequence);
        Assert.Contains(batch.Events, e => e.Type == "command.succeeded" && e.CommandId == stop.CommandId);
    }

    [Theory]
    [InlineData("pstn:+12025550123\n")]
    [InlineData("teams:00000000-0000-4000-8000-000000000001 ")]
    [InlineData("teams: 00000000-0000-4000-8000-000000000001")]
    [InlineData("teams:------------------------------------")]
    public void TaggedTargetsMatchTheStrictSharedPattern(string target)
    {
        var request = new CommandRequest(Protocol.Version, "session", "calls.start", "key",
            new JsonObject { ["target"] = target, ["task"] = "Read public information." });
        Assert.Equal("INVALID_TARGET", Assert.Throws<ControlException>(() => CommandValidation.Validate(request)).Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void DtmfAcceptsTheSharedLengthBoundaries(int length)
    {
        var digits = new string('1', length - 1) + "#";
        var request = new CommandRequest(Protocol.Version, "session", "calls.dtmf", "key",
            new JsonObject { ["digits"] = digits }, "call");
        Assert.Equal(digits, Safe.Text(CommandValidation.Validate(request).Request.Payload, "digits"));
    }

    [Fact]
    public void DtmfRejectsAnythingBeyondTheSharedLengthLimit()
    {
        var request = new CommandRequest(Protocol.Version, "session", "calls.dtmf", "key",
            new JsonObject { ["digits"] = new string('1', 33) }, "call");
        Assert.Equal(400, Assert.Throws<ControlException>(() => CommandValidation.Validate(request)).HttpStatus);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("12D")]
    [InlineData("12#\n")]
    [InlineData("12 ")]
    public void DtmfMatchesTheStrictSharedMutationPayload(string digits)
    {
        var request = new CommandRequest(Protocol.Version, "session", "calls.dtmf", "key",
            new JsonObject { ["digits"] = digits }, "call");
        Assert.Equal("INVALID_DTMF", Assert.Throws<ControlException>(() => CommandValidation.Validate(request)).Code);
    }
}
