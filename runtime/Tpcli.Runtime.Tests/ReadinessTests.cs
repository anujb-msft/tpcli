using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tpcli.Contracts;
using Tpcli.Core;
using Xunit;

namespace Tpcli.Runtime.Tests;

public sealed class ReadinessTests
{
    [Theory]
    [InlineData("MEDIA_AUTH_UNVERIFIED", "MEDIA_AUTH_UNVERIFIED")]
    [InlineData("MEDIA_GRANT_STORE_UNCONFIGURED", "MEDIA_GRANT_STORE_UNCONFIGURED")]
    [InlineData("MEDIA_URL_LOGGING_UNVERIFIED", "MEDIA_URL_LOGGING_UNVERIFIED")]
    [InlineData("TPE_READINESS_UNVERIFIED", "TPE_READINESS_UNVERIFIED")]
    [InlineData("CALLBACK_CORRELATION_UNCONFIGURED", "CALLBACK_CORRELATION_UNCONFIGURED")]
    [InlineData("TPE_SOURCE_NOT_CONFIGURED", "TPE_SOURCE_NOT_CONFIGURED")]
    [InlineData("PRIVATE_UNKNOWN_PROVIDER_DIAGNOSTIC", "PSTN_ROUTE_UNSUPPORTED")]
    public async Task BlockedPstnSurfacesASafeSpecificPrerequisiteWithoutPreparing(string code, string expected)
    {
        await using var h = new Harness();
        var provider = new BlockedProvider(code);
        await h.InitializeAsync(services =>
        {
            services.RemoveAll<ICallProviderFactory>();
            services.AddSingleton<ICallProviderFactory>(provider);
        });
        var error = await Assert.ThrowsAsync<ControlException>(() => h.StartAsync());
        Assert.Equal(expected, error.Code);
        Assert.Equal(expected, error.Error.Message);
        Assert.False(provider.Prepared);
        Assert.Empty(await h.Store.TransactionAsync(tx => tx.ActiveCallsAsync()));
    }

    private sealed class BlockedProvider(string code) : ICallProviderFactory
    {
        public string Mode => "local-fake";
        public bool Prepared { get; private set; }
        public Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderCapabilities(Mode, false, false, false, "simulation", "simulation",
                [new("media_authentication", "blocked", code, "PRIVATE_PROVIDER_DIAGNOSTIC_NOT_FOR_ERROR_RESPONSES")]));
        public Task<ICallConnection> PrepareAsync(CallContext context, CancellationToken cancellationToken)
        {
            Prepared = true;
            throw new InvalidOperationException("PREPARATION_MUST_NOT_RUN");
        }
    }
}
