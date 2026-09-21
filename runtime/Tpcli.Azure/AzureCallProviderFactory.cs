using Azure.Core;
using Tpcli.Contracts;

namespace Tpcli.Azure;

internal sealed class AzureCallProviderFactory(
    AzureOptions options,
    AzureCredentials credentials,
    ICallAutomationTransport telephony,
    IVoiceTransportFactory voice,
    IMediaAuthentication mediaAuthentication,
    CallbackAuthentication callbackAuthentication,
    IProviderEventSink sink,
    IAzureCallCorrelation correlation,
    AzureCallRegistry registry,
    TimeProvider clock) : ICallProviderFactory
{
    public string Mode => "azure";

    public async Task<ProviderCapabilities> CheckReadinessAsync(bool online, CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheck>();
        var configured = false;
        try
        {
            AzureValidation.Configuration(options);
            configured = true;
            checks.Add(new("configuration", "pass", "AZURE_CONFIGURED", "Explicit TPE source and Voice Live settings are syntactically valid."));
        }
        catch (ProviderException ex)
        {
            checks.Add(new("configuration", "blocked", ex.Code, "Configure the Azure section exactly as documented in docs/platform.md."));
        }

        var evidenceCurrent = options.Evidence.IsCurrent(clock.GetUtcNow());
        checks.Add(new("tpe_prerequisites", evidenceCurrent ? "attested" : "blocked",
            evidenceCurrent ? "TPE_OPERATOR_ATTESTATION" : "TPE_READINESS_UNVERIFIED",
            evidenceCurrent
                ? "Unexpired operator attestation supplied; this command has not audited tenant binding, number, licensing, permissions, or funding."
                : "Supply current, independently verified resource-account binding, service number, resource-account license, server access, and outbound funding evidence."));
        checks.Add(new("media_authentication", mediaAuthentication.IsSupported ? "pass" : "blocked",
            mediaAuthentication.IsSupported ? "MEDIA_AUTH_CONFIGURED" : "MEDIA_AUTH_UNVERIFIED",
            "Public ACS streaming documentation specifies correlation headers, not an established service-authenticated WebSocket handshake. Live mode is closed until this is resolved in code."));
        checks.Add(new("teams_direct", "blocked", "CAPABILITY_UNSUPPORTED",
            "Direct Teams calling plus Voice Live, source identity, supported clients, and tenant permissions remain an unproven preview combination. No PSTN substitution."));
        checks.Add(new("callback_correlation", correlation is UnavailableAzureCallCorrelation ? "blocked" : "configured",
            correlation is UnavailableAzureCallCorrelation ? "CALLBACK_CORRELATION_UNCONFIGURED" : "CALLBACK_CORRELATION_CONFIGURED",
            "The host must implement IAzureCallCorrelation using its durable dispatch/binding records; worker memory is not authoritative."));
        checks.Add(new("voice_access", "unknown", "VOICE_ACCESS_UNVERIFIED",
            "Model, voice, region, service access, text summaries, and tool continuations require a separately authorized integration test. Doctor never opens a Voice Live session."));
        checks.Add(new("live_validation", "unknown", "LIVE_VALIDATION_NOT_RUN",
            "Compilation and offline protocol tests do not establish a working deployment, G1-G4, or recipient-perceived latency."));

        if (online)
        {
            await OnlineCheckAsync(checks, "callback_discovery", "CALLBACK_DISCOVERY_AVAILABLE",
                "Public ACS issuer/signing-key discovery is reachable; this does not authenticate a media WebSocket.",
                ct => callbackAuthentication.CheckDiscoveryAsync(ct), cancellationToken).ConfigureAwait(false);
            if (configured)
            {
                await OnlineCheckAsync(checks, "acs_token", "ACS_TOKEN_ACQUIRED",
                    "A token was acquired, not proof of Call Automation RBAC or Teams access assignment.",
                    async ct => { await credentials.Get().GetTokenAsync(new TokenRequestContext(["https://communication.azure.com/.default"]), ct).ConfigureAwait(false); },
                    cancellationToken).ConfigureAwait(false);
                await OnlineCheckAsync(checks, "voice_token", "VOICE_TOKEN_ACQUIRED",
                    "A token was acquired, not proof of Voice Live model/voice access.",
                    async ct => { await credentials.Get().GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), ct).ConfigureAwait(false); },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            checks.Add(new("online_checks", "not_run", "OFFLINE_ONLY", "No network or credential acquisition was attempted."));
        }
        return new ProviderCapabilities(Mode,
            configured && evidenceCurrent && mediaAuthentication.IsSupported && correlation is not UnavailableAzureCallCorrelation, false,
            configured && mediaAuthentication.IsSupported,
            string.IsNullOrEmpty(options.CallAutomation.ResourceAccountObjectId)
                ? "unconfigured"
                : $"teams-resource-account:{options.CallAutomation.ResourceAccountObjectId}",
            "tpe-pstn", checks);
    }

    private static async Task OnlineCheckAsync(List<ReadinessCheck> checks, string name, string code, string message,
        Func<CancellationToken, Task> check, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await check(timeout.Token).ConfigureAwait(false);
            checks.Add(new(name, "pass", code, message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            checks.Add(new(name, "unknown", "AZURE_READONLY_CHECK_UNAVAILABLE", "Read-only authorization/connectivity could not be established. No call or session was created."));
        }
    }

    public async Task<ICallConnection> PrepareAsync(CallContext context, CancellationToken cancellationToken)
    {
        AzureValidation.PstnTarget(context.Target);
        AzureValidation.Deadline(context, clock);
        AzureValidation.Text(context.Task);
        if (!AzureValidation.IsCallId(context.CallId))
            throw new ProviderException("INVALID_CALL_ID");
        AzureValidation.Configuration(options);
        // Check before token acquisition or starting the potentially billable voice session.
        if (!mediaAuthentication.IsSupported)
            throw new ProviderException("MEDIA_AUTH_UNVERIFIED");
        if (correlation is UnavailableAzureCallCorrelation)
            throw new ProviderException("CALLBACK_CORRELATION_UNCONFIGURED");
        if (!options.Evidence.IsCurrent(clock.GetUtcNow()))
            throw new ProviderException("TPE_READINESS_UNVERIFIED");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp((context.Deadline - clock.GetUtcNow()).TotalSeconds, 0, 30)));
        AzureValidation.Deadline(context, clock);
        var transport = await voice.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var connection = new AzureCallConnection(context, options, telephony, transport, sink, correlation.TryBindAsync, registry, clock);
        try
        {
            registry.Add(context.CallId, connection);
            await connection.InitializeAsync(timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (ex is ProviderException) throw;
            throw new ProviderException("VOICE_INITIALIZATION_FAILED");
        }
    }
}
