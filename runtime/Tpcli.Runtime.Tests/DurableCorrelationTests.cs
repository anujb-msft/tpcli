using Microsoft.Extensions.DependencyInjection;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class DurableCorrelationTests
{
    private static Task<string> SeedAsync(Harness harness, string mode = "azure", string dispatch = "intent",
        string lifecycle = "dialing", bool revoked = false) =>
        harness.Store.TransactionAsync(async tx =>
        {
            var id = Safe.Id("call");
            var session = new SessionRecord(Safe.Id("sess"), "fixture-tenant", id, "fixture",
                revoked ? "revoked" : "active", revoked ? tx.Now.AddSeconds(-1) : tx.Now.AddSeconds(15),
                revoked ? null : "fixture-owner");
            await tx.SaveSessionAsync(session);
            await tx.SaveCallAsync(new CallRecord
            {
                Tenant = session.Tenant,
                Principal = session.Principal,
                Profile = session.Profile,
                WorkerId = "absent-fixture-worker",
                Fence = 7,
                DispatchStatus = dispatch,
                State = new CallState(id, session.Id, "pstn:+12025550123", "fixture-source", "fixture-route",
                    mode, tx.Now, tx.Now, tx.Now.AddSeconds(30), lifecycle, "unknown",
                    revoked ? "owner_disconnected" : null, "unknown", "unknown", "unavailable", 0)
            });
            return id;
        });

    [Theory]
    [InlineData("azure", "none")]
    [InlineData("local-fake", "intent")]
    [InlineData("azure", "unrecognized")]
    public async Task RejectsMissingIntentAndNonAzureCalls(string mode, string dispatch)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var correlation = h.Services.GetRequiredService<DurableCallCorrelation>();
        Assert.False(await correlation.TryBindAsync("missing", "connection", null));
        var id = await SeedAsync(h, mode, dispatch);
        Assert.False(await correlation.TryBindAsync(id, "connection", null));
        Assert.Null((await h.Store.TransactionAsync(tx => tx.CallAsync(id)))!.Handle);
    }

    [Theory]
    [InlineData("intent", "ending")]
    [InlineData("ambiguous", "termination_unknown")]
    [InlineData("returned", "ended")]
    public async Task RevokedFencedAndCompletedCallsRemainBindableWithoutBeingRevived(string dispatch, string lifecycle)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var id = await SeedAsync(h, dispatch: dispatch, lifecycle: lifecycle, revoked: true);
        var before = (await h.Store.TransactionAsync(tx => tx.CallAsync(id)))!;
        var correlation = h.Services.GetRequiredService<DurableCallCorrelation>();
        Assert.True(await correlation.TryBindAsync(id, "opaque/connection:AbC==+", "opaque-server:XYZ/="));
        var after = (await h.Store.TransactionAsync(tx => tx.CallAsync(id)))!;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Fence, after.Fence);
        Assert.Equal(dispatch, after.DispatchStatus);
        Assert.Equal("opaque/connection:AbC==+", after.Handle!.ConnectionId);
        using var independent = new ControlStore(h.Settings, h.Clock);
        var restarted = new DurableCallCorrelation(independent);
        Assert.True(await restarted.TryBindAsync(id, "opaque/connection:AbC==+", "opaque-server:XYZ/="));
        Assert.Equal(lifecycle, (await independent.TransactionAsync(tx => tx.CallAsync(id)))!.State.Lifecycle);
    }

    [Fact]
    public async Task EnrichesAnAbsentServerIdButRejectsReplacementAndCrossCallIdentities()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var first = await SeedAsync(h);
        var second = await SeedAsync(h);
        var correlation = h.Services.GetRequiredService<DurableCallCorrelation>();
        Assert.True(await correlation.TryBindAsync(first, "connection-A", null));
        Assert.True(await correlation.TryBindAsync(first, "connection-A", "server-A"));
        Assert.True(await correlation.TryBindAsync(first, "connection-A", null));
        Assert.False(await correlation.TryBindAsync(first, "replacement-connection", "server-A"));
        Assert.False(await correlation.TryBindAsync(first, "connection-A", "replacement-server"));
        Assert.False(await correlation.TryBindAsync(second, "connection-A", null));
        Assert.False(await correlation.TryBindAsync(second, "connection-B", "server-A"));
        var original = (await h.Store.TransactionAsync(tx => tx.CallAsync(first)))!.Handle;
        Assert.Equal(new ProviderHandle("connection-A", "server-A"), original);
        Assert.Null((await h.Store.TransactionAsync(tx => tx.CallAsync(second)))!.Handle);
    }

    [Fact]
    public async Task TwoIndependentStoreClientsCannotClaimTheSameProviderConnection()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var first = await SeedAsync(h);
        var second = await SeedAsync(h);
        using var independent = new ControlStore(h.Settings, h.Clock);
        await independent.InitializeAsync();
        var local = h.Services.GetRequiredService<DurableCallCorrelation>();
        var remote = new DurableCallCorrelation(independent);
        var results = await Task.WhenAll(
            Task.Run(() => local.TryBindAsync(first, "shared-opaque-id", null)),
            Task.Run(() => remote.TryBindAsync(second, "shared-opaque-id", null)));
        Assert.Single(results, accepted => accepted);
        var bound = await h.Store.TransactionAsync(async tx =>
            new[] { (await tx.CallAsync(first))!.Handle, (await tx.CallAsync(second))!.Handle });
        Assert.Single(bound, handle => handle is not null);
    }

    [Fact]
    public async Task InvalidIdentityLengthsAndEmptyValuesNeverModifyMetadata()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var id = await SeedAsync(h);
        var correlation = h.Services.GetRequiredService<DurableCallCorrelation>();
        Assert.False(await correlation.TryBindAsync(id, "", null));
        Assert.False(await correlation.TryBindAsync(id, new string('x', 4097), null));
        Assert.False(await correlation.TryBindAsync(id, "connection", ""));
        Assert.False(await correlation.TryBindAsync(id, "connection", new string('x', 4097)));
        Assert.Null((await h.Store.TransactionAsync(tx => tx.CallAsync(id)))!.Handle);
    }
}
