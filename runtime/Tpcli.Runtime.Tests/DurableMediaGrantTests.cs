using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tpcli.Azure;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class DurableMediaGrantTests
{
    private const string Origin = "https://media.example.invalid";
    private static string PathFor(MediaGrantScope scope) => "/azure/media/" + scope.CallId;
    private static string Digest() => Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
    private static DurableMediaGrants Grants(Harness harness) => harness.Services.GetRequiredService<DurableMediaGrants>();

    private static async Task<MediaGrantScope> SeedAsync(Harness harness, int deadlineSeconds = 30)
    {
        var id = await harness.Store.TransactionAsync(async tx =>
        {
            var id = Safe.Id("call");
            var owner = (await tx.SessionAsync(harness.Session.SessionId))!;
            await tx.SaveCallAsync(new CallRecord
            {
                Tenant = owner.Tenant,
                Principal = owner.Principal,
                Profile = owner.Profile,
                WorkerId = harness.Runtime.WorkerId,
                DispatchStatus = "intent",
                State = new CallState(id, owner.Id, "pstn:+12025550123", "fixture-source", "fixture-route", "azure",
                    tx.Now, tx.Now, tx.Now.AddSeconds(deadlineSeconds), "dialing", "in_progress",
                    null, "pending", "unknown", "pending", 0)
            });
            return id;
        });
        return (await Grants(harness).CaptureScopeAsync(id, harness.Session.SessionId))!;
    }

    private static async Task<long?> TimestampAsync(Harness harness, string callId, bool consumed)
    {
        await using var connection = new SqliteConnection(harness.Settings.Store.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = consumed
            ? "SELECT consumed_ms FROM media_grants WHERE call_id=$id"
            : "SELECT revoked_ms FROM media_grants WHERE call_id=$id";
        command.Parameters.AddWithValue("$id", callId);
        var value = await command.ExecuteScalarAsync();
        return value is long timestamp ? timestamp : null;
    }

    [Fact]
    public async Task ProductionRegistrationSelectsTheDurableStoreWithoutNetworkOrDatabaseAccess()
    {
        await using var app = RuntimeApplication.Build([], builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tpcli:Mode"] = "azure",
                ["Tpcli:Store:Provider"] = "postgres",
                ["Tpcli:Store:ConnectionString"] = "Host=database.example.invalid;Database=tpcli",
                ["Tpcli:TenantId"] = "11111111-1111-1111-1111-111111111111",
                ["Tpcli:Audience"] = "api://tpcli-example",
                ["urls"] = "https://127.0.0.1:0"
            }));
        var store = app.Services.GetRequiredService<IAzureMediaGrantStore>();
        Assert.Equal("AzureDurableMediaGrants", store.GetType().Name);
        Assert.Same(store, app.Services.GetRequiredService<IAzureMediaGrantStore>());
    }

    [Fact]
    public async Task OneGrantPerCallSurvivesStoreChurnAndAllowsExactlyOneConcurrentConsumer()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.False(await Grants(h).TryIssueAsync(scope, Digest(), Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        using var independent = new ControlStore(h.Settings, h.Clock);
        var otherClient = new DurableMediaGrants(independent, () => h.Runtime.WorkerId);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            (index % 2 == 0 ? Grants(h) : otherClient).TryConsumeAsync(scope, digest, Origin, PathFor(scope)))));
        Assert.Single(results, accepted => accepted);
        Assert.NotNull(await TimestampAsync(h, scope.CallId, consumed: true));
        Assert.False(await otherClient.TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
        Assert.False(await otherClient.TryIssueAsync(scope, Digest(), Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
    }

    [Fact]
    public async Task EveryAuthorityFieldAndEndpointMustMatchWithoutSpendingTheValidGrant()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        foreach (var wrong in new[]
        {
            scope with { CallId = "different-call" }, scope with { SessionId = "different-session" },
            scope with { TenantId = "different-tenant" }, scope with { PrincipalId = "different-principal" },
            scope with { WorkerId = "different-worker" }, scope with { WorkerFence = scope.WorkerFence + 1 },
            scope with { OwnerGeneration = scope.OwnerGeneration + 1 }
        })
        {
            Assert.False(await Grants(h).TryConsumeAsync(wrong, digest, Origin, PathFor(scope)));
        }
        Assert.False(await Grants(h).TryConsumeAsync(scope, Digest(), Origin, PathFor(scope)));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, "https://wrong.example.invalid", PathFor(scope)));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope) + "/"));
        Assert.Null(await TimestampAsync(h, scope.CallId, consumed: true));
        Assert.True(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("worker")]
    [InlineData("fence")]
    [InlineData("ending")]
    [InlineData("ended")]
    public async Task RevocationAndFencingInvalidateUnusedGrantsDurably(string cause)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        await h.Store.TransactionAsync(async tx =>
        {
            var owner = (await tx.SessionAsync(scope.SessionId))!;
            var call = (await tx.CallAsync(scope.CallId))!;
            switch (cause)
            {
                case "owner":
                    await tx.SaveSessionAsync(owner with { Status = "revoked", ConnectionId = null });
                    break;
                case "generation":
                    await tx.SaveSessionAsync(owner with { ConnectionId = "different-owner-connection" });
                    break;
                case "worker":
                    await tx.ExpireWorkerAsync(scope.WorkerId);
                    break;
                case "fence":
                    call.Fence++;
                    await tx.SaveCallAsync(call);
                    break;
                default:
                    call.State = call.State with { Lifecycle = cause };
                    await tx.SaveCallAsync(call);
                    break;
            }
            return true;
        });
        Assert.NotNull(await TimestampAsync(h, scope.CallId, consumed: false));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
        Assert.Null(await TimestampAsync(h, scope.CallId, consumed: true));
    }

    [Fact]
    public async Task CaptureCannotAdoptAnotherWorkersAuthorityOrANonAzureCall()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var otherWorker = new DurableMediaGrants(h.Store, () => "different-worker");
        Assert.Null(await otherWorker.CaptureScopeAsync(scope.CallId, scope.SessionId));
        Assert.False(await otherWorker.TryIssueAsync(scope, Digest(), Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.Null(await Grants(h).CaptureScopeAsync(scope.CallId, "different-session"));
        await h.Store.TransactionAsync(async tx =>
        {
            var call = (await tx.CallAsync(scope.CallId))!;
            call.State = call.State with { ProviderMode = "local-fake" };
            return await tx.SaveCallAsync(call);
        });
        Assert.Null(await Grants(h).CaptureScopeAsync(scope.CallId, scope.SessionId));
    }

    [Fact]
    public async Task NormalHeartbeatsPreserveOwnerGenerationButLeaseExpiryNeverRevivesIt()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        await h.Runtime.HeartbeatAsync(h.Owner, scope.SessionId, h.ConnectionId, CancellationToken.None);
        Assert.Equal(scope, await Grants(h).CaptureScopeAsync(scope.CallId, scope.SessionId));
        h.Clock.Advance(RuntimeSettings.OwnerLease);
        await h.Store.TransactionAsync(tx => tx.HeartbeatWorkerAsync("different-worker"));
        Assert.Null(await Grants(h).CaptureScopeAsync(scope.CallId, scope.SessionId));
        Assert.NotNull(await TimestampAsync(h, scope.CallId, consumed: false));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("worker")]
    public async Task EitherLeaseExpiringAloneRevokesTheUnusedGrant(string expired)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        if (expired == "owner")
        {
            for (var elapsed = TimeSpan.Zero; elapsed < RuntimeSettings.OwnerLease; elapsed += TimeSpan.FromSeconds(3))
            {
                h.Clock.Advance(TimeSpan.FromSeconds(3));
                await h.Store.TransactionAsync(tx => tx.HeartbeatWorkerAsync(scope.WorkerId));
            }
        }
        else h.Clock.Advance(RuntimeSettings.WorkerLease);
        await h.Store.TransactionAsync(async tx =>
        {
            Assert.Equal(expired == "worker", (await tx.SessionAsync(scope.SessionId))!.Live(tx.Now));
            Assert.Equal(expired == "owner", await tx.WorkerAliveAsync(scope.WorkerId));
            return true;
        });
        Assert.NotNull(await TimestampAsync(h, scope.CallId, consumed: false));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
    }

    [Fact]
    public async Task ExistingControlDatabaseAddsGrantTablesWithoutLosingCallAuthority()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var original = await SeedAsync(h);
        await using (var connection = new SqliteConnection(h.Settings.Store.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE media_grants; DROP TABLE owner_generations;";
            await command.ExecuteNonQueryAsync();
        }
        using var upgraded = new ControlStore(h.Settings, h.Clock);
        var grants = new DurableMediaGrants(upgraded, () => h.Runtime.WorkerId);
        var scope = await grants.CaptureScopeAsync(original.CallId, original.SessionId);
        Assert.NotNull(scope);
        Assert.Equal(original.WorkerId, scope.WorkerId);
        Assert.Equal(original.WorkerFence, scope.WorkerFence);
        var digest = Digest();
        Assert.True(await grants.TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.True(await grants.TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryAndHardDeadlineAreExclusiveAndCannotBeRenewed(bool atDeadline)
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h, atDeadline ? 1 : 30);
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(1)));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
        Assert.NotNull(await TimestampAsync(h, scope.CallId, consumed: false));
        Assert.False(await Grants(h).TryIssueAsync(scope, Digest(), Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(1)));
    }

    [Fact]
    public async Task IssueRejectsInvalidLifetimeAndUriWithoutLeavingReusableGrants()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var digest = Digest();
        foreach (var expiry in new[] { h.Clock.GetUtcNow(), h.Clock.GetUtcNow().AddSeconds(31), h.Clock.GetUtcNow().AddSeconds(121) })
            Assert.False(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), expiry));
        foreach (var origin in new[] { "http://media.example.invalid", Origin + "/path", Origin + "?secret=redacted", "https://user@media.example.invalid" })
            Assert.False(await Grants(h).TryIssueAsync(scope, digest, origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.False(await Grants(h).TryIssueAsync(scope, "not-a-digest", Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.False(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope) + "/wrong", h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
    }

    [Fact]
    public async Task IssueMayPrecedeDispatchButMediaCannotBeClaimedUntilARecordedDispatchIntent()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        await h.Store.TransactionAsync(async tx =>
        {
            var call = (await tx.CallAsync(scope.CallId))!;
            call.DispatchStatus = "none";
            call.State = call.State with { Lifecycle = "preflight" };
            return await tx.SaveCallAsync(call);
        });
        var digest = Digest();
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        Assert.False(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
        await h.Store.TransactionAsync(async tx =>
        {
            var call = (await tx.CallAsync(scope.CallId))!;
            call.DispatchStatus = "intent";
            call.State = call.State with { Lifecycle = "dialing" };
            return await tx.SaveCallAsync(call);
        });
        Assert.True(await Grants(h).TryConsumeAsync(scope, digest, Origin, PathFor(scope)));
    }

    [Fact]
    public async Task OnlyDigestsAndMinimalAuthorityMetadataArePersisted()
    {
        await using var h = new Harness();
        await h.InitializeAsync();
        var scope = await SeedAsync(h);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Convert.ToBase64String(bytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(await Grants(h).TryIssueAsync(scope, digest, Origin, PathFor(scope), h.Clock.GetUtcNow().AddSeconds(20)));
        await using var connection = new SqliteConnection(h.Settings.Store.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT digest,origin,path,session_id FROM media_grants WHERE call_id=$id";
        command.Parameters.AddWithValue("$id", scope.CallId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(digest, reader.GetString(0));
        Assert.Equal(Origin, reader.GetString(1));
        Assert.Equal(PathFor(scope), reader.GetString(2));
        Assert.Equal(scope.SessionId, reader.GetString(3));
        foreach (var file in Directory.GetFiles(h.DirectoryPath, "control.db*"))
            Assert.DoesNotContain(raw, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
    }
}
