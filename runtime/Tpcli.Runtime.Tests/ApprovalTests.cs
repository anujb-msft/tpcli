using System.Text.Json.Nodes;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class ApprovalTests
{
    [Theory]
    [InlineData("recipient_objection", "recipient_objection")]
    [InlineData("disallowed_voicemail", "voicemail_not_allowed")]
    [InlineData("no_authorized_path", "no_authorized_path")]
    [InlineData("task_finished", "task_finished")]
    [InlineData("PRIVATE_MODEL_REASON_NOT_FOR_CONTROL_STORAGE", "agent_ended")]
    [InlineData(null, "task_finished")]
    public async Task EndToolPreservesOnlySafeReasonsAndNeverCompletesTheTask(string? reason, string expected)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, state => state.Lifecycle == "connected");
        await h.ToolAsync(receipt.CallId!, "end_call",
            reason is null ? [] : new JsonObject { ["reason"] = reason });
        var state = await h.WaitStateAsync(receipt.CallId!, Safe.Terminal);
        Assert.Equal(expected, state.TerminationReason);
        Assert.Equal("not_completed", state.TaskOutcome);
        Assert.Equal("confirmed", state.HangupStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSummaryIsUnavailableRatherThanACompletedSummary(string summary)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        await h.ToolAsync(receipt.CallId!, "report_result", new JsonObject { ["outcome"] = "completed", ["summary"] = summary });
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.TaskOutcome == "completed");
        Assert.Equal("unavailable", state.SummaryStatus);
        Assert.DoesNotContain((await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events, e => e.Type == "summary.ready");
    }

    [Fact]
    public async Task ExactHashOneTimeConsumptionAndPrincipalAreEnforced()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var approval = await h.ApprovalAsync(receipt.CallId!);
        var expected = Safe.CanonicalHash(new JsonObject
        {
            ["material_terms"] = new JsonObject { ["charge"] = 0, ["time"] = "10:00" },
            ["conversation_version"] = approval.ConversationVersion,
            ["action"] = "confirm_appointment"
        });
        Assert.Equal(expected, approval.ActionHash);
        var changed = h.Decision(approval);
        changed["action_hash"] = "sha256:" + new string('0', 64);
        Assert.Equal("STALE_APPROVAL", (await Assert.ThrowsAsync<ControlException>(() =>
            h.CommandAsync("approvals.resolve", receipt.CallId!, changed))).Code);
        var command = await h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval));
        await Harness.EventuallyAsync(async () => (await h.Runtime.CommandAsync(h.Owner, command.CommandId)).Status == "succeeded");
        var resolved = Assert.Single(await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!));
        Assert.Equal("approved", resolved.Status);
        Assert.Equal(h.Owner.Principal, resolved.Actor);
        Assert.Equal("STALE_APPROVAL", (await Assert.ThrowsAsync<ControlException>(() =>
            h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval)))).Code);
    }

    [Fact]
    public async Task ExpiryDeniesAndDoesNotExtendDeadline()
    {
        await using var h = new Harness(s => s.Fake.ApprovalTimeoutMilliseconds = 500);
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var approval = await h.ApprovalAsync(receipt.CallId!);
        Assert.True(approval.ExpiresAt <= state.Deadline);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.Runtime.TickAsync();
        await Harness.EventuallyAsync(async () =>
            (await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!))[0].Status == "expired");
        Assert.Equal("STALE_APPROVAL", (await Assert.ThrowsAsync<ControlException>(() =>
            h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval)))).Code);
        Assert.Equal(state.Deadline, (await h.Runtime.StateAsync(h.Owner, receipt.CallId!)).Deadline);
        Assert.Contains((await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events, e => e.Type == "approval.expired");
    }

    [Fact]
    public async Task InstructionsAndChangedTermsInvalidateOlderApprovals()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var first = await h.ApprovalAsync(receipt.CallId!, toolId: "first");
        var instruction = await h.CommandAsync("calls.instruct", receipt.CallId!, new JsonObject { ["text"] = "Do not book anything." });
        await Harness.EventuallyAsync(async () =>
            (await h.Runtime.CommandAsync(h.Owner, instruction.CommandId)).Status == "succeeded");
        Assert.Equal("invalidated", (await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!))[0].Status);
        var second = await h.ApprovalAsync(receipt.CallId!, toolId: "second");
        Assert.True(second.ConversationVersion > first.ConversationVersion);
        Assert.NotEqual(first.ActionHash, second.ActionHash);
        await h.ToolAsync(receipt.CallId!, "request_approval", new JsonObject
        {
            ["action"] = "confirm_appointment",
            ["description"] = "A changed appointment.",
            ["material_terms"] = new JsonObject { ["time"] = "11:00", ["charge"] = 0 }
        }, "third");
        await Harness.EventuallyAsync(async () => (await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!)).Count == 3);
        var all = await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!);
        Assert.Equal("invalidated", all.Single(a => a.ApprovalId == second.ApprovalId).Status);
        Assert.Single(all, a => a.Status == "pending");
    }

    [Fact]
    public async Task OwnerLossInvalidatesApprovalsAndNeverPermitsContinuation()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var approval = await h.ApprovalAsync(receipt.CallId!);
        await h.Runtime.RevokeAsync(h.Owner, h.Session.SessionId);
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("invalidated", Assert.Single(await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!)).Status);
        await Assert.ThrowsAsync<ControlException>(() => h.CommandAsync("approvals.resolve", receipt.CallId!, h.Decision(approval)));
    }

    [Fact]
    public async Task DuplicatedToolsAreDurablyHashedAndChangedToolIdPayloadFailsClosed()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        var first = await h.ApprovalAsync(receipt.CallId!);
        await h.ApprovalAsync(receipt.CallId!);
        Assert.Single(await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!));
        await h.ToolAsync(receipt.CallId!, "request_approval", new JsonObject
        {
            ["action"] = "confirm_appointment",
            ["description"] = "Changed.",
            ["material_terms"] = new JsonObject { ["charge"] = 100 }
        }, "test-approval");
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.NotEqual("approved", (await h.Runtime.ApprovalsAsync(h.Owner, receipt.CallId!))
            .Single(a => a.ApprovalId == first.ApprovalId).Status);
    }

    [Fact]
    public async Task ReportResultAndEndAreIndependentAndSummaryIsNeverFabricated()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var receipt = await h.StartAsync();
        await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "connected");
        await h.ToolAsync(receipt.CallId!, "report_result", new JsonObject { ["outcome"] = "partial" });
        await h.WaitStateAsync(receipt.CallId!, s => s.TaskOutcome == "partial");
        await h.ToolAsync(receipt.CallId!, "end_call", []);
        var state = await h.WaitStateAsync(receipt.CallId!, s => s.Lifecycle == "ended");
        Assert.Equal("partial", state.TaskOutcome);
        Assert.Equal("task_finished", state.TerminationReason);
        Assert.Equal("unavailable", state.SummaryStatus);
        Assert.DoesNotContain((await h.Runtime.EventsAsync(h.Owner, receipt.CallId!)).Events, e => e.Type == "summary.ready");
    }
}
