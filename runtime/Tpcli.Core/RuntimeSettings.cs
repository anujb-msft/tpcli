using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed class RuntimeSettings
{
    public string Mode { get; set; } = "";
    public string Profile { get; set; } = "default";
    public string? FakeToken { get; set; }
    public string? TenantId { get; set; }
    public string? Audience { get; set; }
    public string Scope { get; set; } = "tpcli.control";
    public string[] TrustedProxies { get; set; } = [];
    public StoreSettings Store { get; set; } = new();
    public FakeSettings Fake { get; set; } = new();
    public int ActorQueueCapacity { get; set; } = 64;
    public int SubscriberQueueCapacity { get; set; } = 128;
    public int MaxResidentCalls { get; set; } = 1024;
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxTaskBytes = 16 * 1024;
    public const int ReplayBytesPerCall = 4 * 1024 * 1024;
    public static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OwnerLease = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan WorkerLease = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WatchdogCadence = TimeSpan.FromSeconds(1);

    public static RuntimeSettings Load(IConfiguration configuration)
    {
        var result = new RuntimeSettings();
        configuration.GetSection("Tpcli").Bind(result);
        return result;
    }

    public void Validate(bool httpServer)
    {
        if (Mode is not ("local-fake" or "azure"))
            throw new InvalidOperationException("TPCLI_MODE_REQUIRED");
        if (string.IsNullOrWhiteSpace(Profile) || Profile.Length > 128)
            throw new InvalidOperationException("INVALID_PROFILE");
        if (Store.Provider is not ("sqlite" or "postgres") || string.IsNullOrWhiteSpace(Store.ConnectionString))
            throw new InvalidOperationException("CONTROL_STORE_REQUIRED");
        if (Mode == "azure" && Store.Provider != "postgres")
            throw new InvalidOperationException("AZURE_REQUIRES_POSTGRES_CONTROL_STORE");
        if (Mode == "azure" && (FakeToken is not null || Fake.TestDeadlineMilliseconds is not null
            || Fake.ApprovalTimeoutMilliseconds != 60_000))
            throw new InvalidOperationException("FAKE_CONFIGURATION_FORBIDDEN_IN_AZURE");
        if (Mode == "local-fake" && httpServer
            && ((FakeToken?.Length ?? 0) < 32 || FakeToken!.Length > 4096))
            throw new InvalidOperationException("FAKE_TOKEN_MINIMUM_32_BYTES");
        if (Mode == "azure" && httpServer &&
            (!Guid.TryParse(TenantId, out _) || string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(Scope)))
            throw new InvalidOperationException("ENTRA_CONFIGURATION_REQUIRED");
        if (ActorQueueCapacity is < 4 or > 1024 || SubscriberQueueCapacity is < 4 or > 1024
            || MaxResidentCalls is < 1 or > 10_000)
            throw new InvalidOperationException("INVALID_QUEUE_LIMIT");
        if (Fake.ConnectDelayMilliseconds is < 0 or > 60_000
            || Fake.PrepareDelayMilliseconds is < 0 or > 60_000
            || Fake.ApprovalTimeoutMilliseconds is < 1 or > 60_000
            || Fake.TestDeadlineMilliseconds is < 1 or > 3_600_000)
            throw new InvalidOperationException("INVALID_FAKE_TIMING");
    }
}

public sealed class StoreSettings
{
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "";
}

public sealed class FakeSettings
{
    public int ConnectDelayMilliseconds { get; set; } = 100;
    public int PrepareDelayMilliseconds { get; set; }
    public int ApprovalTimeoutMilliseconds { get; set; } = 60_000;
    public int? TestDeadlineMilliseconds { get; set; }
    public bool CreateAmbiguous { get; set; }
    public bool HangupUnknown { get; set; }
    public bool FailPreflight { get; set; }
    public bool FailDialBeforeDispatch { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool EmitTranscript { get; set; } = true;
    public bool TeamsEnabled { get; set; }
}

public sealed record Caller(string Tenant, string Principal);

public sealed class ControlException(string code, int httpStatus = 409, bool retryable = false) : Exception(code)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
    public WireError Error => new(Code, Code, retryable);
}

public static partial class Safe
{
    public static string Id(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    public static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string CanonicalHash(JsonNode? value) => Hash(Canonical(value));

    public static string Canonical(JsonNode? node)
    {
        if (node is JsonObject obj)
            return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}";
        if (node is JsonArray array)
            return "[" + string.Join(",", array.Select(Canonical)) + "]";
        return node?.ToJsonString(Protocol.Json) ?? "null";
    }

    public static JsonObject Json<T>(T value) => (JsonSerializer.SerializeToNode(value, Protocol.Json) as JsonObject)!;
    public static string? Text(JsonObject value, string property) =>
        value[property] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
    public static bool Integer(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (!value.TryGetValue<int>(out var small)) return false;
        number = small;
        return true;
    }
    public static bool Terminal(CallState state) => state.Lifecycle is "ended" or "failed_before_connect";
    public static bool HasResult(CallState state) => Terminal(state) || state.Lifecycle == "termination_unknown";
    public static string ModelEndReason(string? reason) => reason switch
    {
        null or "task_finished" => "task_finished",
        "objection" or "recipient_objection" => "recipient_objection",
        "disallowed_voicemail" or "voicemail_disallowed" or "voicemail_not_allowed" => "voicemail_not_allowed",
        "no_authorized_path" => "no_authorized_path",
        _ => "agent_ended"
    };
    public static string ProviderCode(string? value) => value switch
    {
        "FAKE_PREFLIGHT_FAILED" or "FAKE_DIAL_FAILED" or "CREATE_AMBIGUOUS" or "MEDIA_ERROR"
        or "MEDIA_OVERFLOW" or "MEDIA_DISCONNECTED" or "VOICE_LIVE_FAILED"
        or "VOICE_LIVE_UNAVAILABLE" or "ACS_CREATE_FAILED" or "ACS_CREATE_AMBIGUOUS"
        or "CALL_BUSY" or "NO_ANSWER" or "PROVIDER_DISCONNECTED" or "PROVIDER_TIMEOUT"
        or "TEAMS_ROUTE_UNSUPPORTED" or "TPE_CONFIGURATION_REQUIRED" or "MEDIA_AUTH_UNVERIFIED"
        or "MEDIA_GRANT_STORE_UNCONFIGURED" or "MEDIA_URL_LOGGING_UNVERIFIED"
        or "CALLBACK_CORRELATION_UNCONFIGURED" or "TPE_READINESS_UNVERIFIED"
        or "AZURE_ENDPOINT_INVALID" or "AZURE_IDENTITY_INVALID" or "MEDIA_GRANT_TTL_INVALID"
        or "TPE_SOURCE_NOT_CONFIGURED" or "VOICE_CONFIGURATION_INVALID" or "VOICE_LOCALE_UNSUPPORTED" => value,
        _ => "PROVIDER_FAILURE"
    };

    [GeneratedRegex(@"\Apstn:\+[1-9][0-9]{6,14}\z", RegexOptions.CultureInvariant)]
    public static partial Regex PstnTarget();
    [GeneratedRegex(@"\A[0-9*#]{1,32}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Dtmf();
}
