using System.Text;
using System.Text.Json.Nodes;
using Tpcli.Contracts;

namespace Tpcli.Core;

public sealed record ValidatedCommand(CommandRequest Request, string PayloadHash, int DurationSeconds = 600);

public static class CommandValidation
{
    public static ValidatedCommand Validate(CommandRequest request)
    {
        if (request.SchemaVersion != Protocol.Version) throw new ControlException("SCHEMA_VERSION_UNSUPPORTED", 400);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
            || request.Payload is null || request.CallId?.Length > 128)
            throw new ControlException("INVALID_COMMAND", 400);
        var payload = request.Payload.DeepClone().AsObject();
        var duration = 600;
        switch (request.Operation)
        {
            case "calls.start":
                Fields(payload, "target", "task", "max_duration_seconds", "allow_voicemail");
                if (request.CallId is not null) throw new ControlException("INVALID_COMMAND", 400);
                var target = RequiredText(payload, "target", 128);
                if (!Safe.PstnTarget().IsMatch(target)
                    && !(target.StartsWith("teams:", StringComparison.Ordinal)
                        && Guid.TryParseExact(target[6..], "D", out _)))
                    throw new ControlException("INVALID_TARGET", 400);
                RequiredText(payload, "task", RuntimeSettings.MaxTaskBytes);
                if (payload.TryGetPropertyValue("max_duration_seconds", out var seconds)
                    && (seconds is not JsonValue value || !value.TryGetValue<int>(out duration)))
                    throw new ControlException("INVALID_DURATION", 400);
                if (duration is < 30 or > 3600) throw new ControlException("INVALID_DURATION", 400);
                if (payload.TryGetPropertyValue("allow_voicemail", out var voicemail)
                    && (voicemail is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
                    throw new ControlException("INVALID_VOICEMAIL", 400);
                payload["max_duration_seconds"] = duration;
                payload["allow_voicemail"] ??= false;
                break;
            case "calls.instruct":
                Fields(payload, "text");
                RequiredText(payload, "text", RuntimeSettings.MaxTaskBytes);
                break;
            case "calls.dtmf":
                Fields(payload, "digits");
                if (!Safe.Dtmf().IsMatch(RequiredText(payload, "digits", 32)))
                    throw new ControlException("INVALID_DTMF", 400);
                break;
            case "calls.stop":
                Fields(payload);
                break;
            case "approvals.resolve":
                Fields(payload, "approval_id", "decision", "action_hash");
                RequiredText(payload, "approval_id", 128);
                if (RequiredText(payload, "decision", 16) is not ("approve" or "deny"))
                    throw new ControlException("INVALID_DECISION", 400);
                var hash = RequiredText(payload, "action_hash", 71);
                if (hash.Length != 71 || !hash.StartsWith("sha256:", StringComparison.Ordinal)
                    || hash[7..].Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
                    throw new ControlException("INVALID_ACTION_HASH", 400);
                break;
            default: throw new ControlException("UNSUPPORTED_OPERATION", 400);
        }
        if (request.Operation != "calls.start" && string.IsNullOrWhiteSpace(request.CallId))
            throw new ControlException("CALL_REQUIRED", 400);
        request = request with { Payload = payload };
        var binding = new JsonObject
        {
            ["operation"] = request.Operation,
            ["session_id"] = request.SessionId,
            ["call_id"] = request.CallId,
            ["payload"] = payload.DeepClone()
        };
        return new ValidatedCommand(request, Safe.CanonicalHash(binding), duration);
    }

    public static string RequiredText(JsonObject payload, string key, int maxBytes)
    {
        var text = Safe.Text(payload, key);
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > maxBytes)
            throw new ControlException("INVALID_" + key.ToUpperInvariant(), 400);
        return text;
    }
    private static void Fields(JsonObject payload, params string[] allowed)
    {
        if (payload.Any(property => !allowed.Contains(property.Key, StringComparer.Ordinal)))
            throw new ControlException("UNKNOWN_PAYLOAD_FIELD", 400);
    }
}
