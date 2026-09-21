using System.Globalization;
using System.Text.RegularExpressions;
using Tpcli.Contracts;

namespace Tpcli.Azure;

public sealed class AzureOptions
{
    public AzureIdentityOptions Identity { get; set; } = new();
    public CallAutomationOptions CallAutomation { get; set; } = new();
    public VoiceLiveOptions VoiceLive { get; set; } = new();
    public AzureReadinessEvidence Evidence { get; set; } = new();
}

public sealed class AzureIdentityOptions
{
    public string Mode { get; set; } = "managed_identity";
    public string? ManagedIdentityClientId { get; set; }
    public string? DeveloperTenantId { get; set; }
}

public sealed class CallAutomationOptions
{
    public string Endpoint { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ResourceAccountObjectId { get; set; } = "";
    public string TeamsServiceNumber { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
}

public sealed class VoiceLiveOptions
{
    public string Endpoint { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Locale { get; set; } = "";
    public string TranscriptionModel { get; set; } = "";
}

// These are expiring operator attestations, not results of an automatic tenant audit.
public sealed class AzureReadinessEvidence
{
    public bool ResourceAccountBound { get; set; }
    public bool ServiceNumberAssigned { get; set; }
    public bool ResourceAccountLicensed { get; set; }
    public bool ServerCallingAuthorized { get; set; }
    public bool OutboundPstnFunded { get; set; }
    public DateTimeOffset? ValidUntilUtc { get; set; }

    internal bool IsCurrent(DateTimeOffset now) =>
        ResourceAccountBound && ServiceNumberAssigned && ResourceAccountLicensed &&
        ServerCallingAuthorized && OutboundPstnFunded && ValidUntilUtc > now &&
        ValidUntilUtc <= now.AddDays(7);
}

internal static partial class AzureValidation
{
    internal const string VoiceApiVersion = "2026-07-15";
    internal const int BytesPerSecond = 24_000 * 2;
    internal const int MaximumAudioBytes = 2 * BytesPerSecond;
    internal const int MaximumTextLength = 32_768;

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CallIdRegex();

    internal static bool IsPhone(string value) => PhoneRegex().IsMatch(value);
    internal static bool IsCallId(string value) => CallIdRegex().IsMatch(value);

    internal static string PstnTarget(string target)
    {
        if (target.StartsWith("teams:", StringComparison.Ordinal))
            throw new ProviderException("CAPABILITY_UNSUPPORTED");
        if (!target.StartsWith("pstn:", StringComparison.Ordinal) || !IsPhone(target[5..]))
            throw new ProviderException("INVALID_TARGET");
        return target[5..];
    }

    internal static Uri Endpoint(string value, params string[] suffixes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            (suffixes.Length != 0 && !suffixes.Any(s =>
                uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase))))
            throw new ProviderException("AZURE_ENDPOINT_INVALID");
        return uri;
    }

    internal static void Identity(AzureIdentityOptions value)
    {
        if (value.Mode is not ("managed_identity" or "developer_azure_cli") ||
            (value.ManagedIdentityClientId is not null && !Guid.TryParse(value.ManagedIdentityClientId, out _)) ||
            (value.Mode == "developer_azure_cli" && !Guid.TryParse(value.DeveloperTenantId, out _)))
            throw new ProviderException("AZURE_IDENTITY_INVALID");
    }

    internal static void Configuration(AzureOptions options)
    {
        Identity(options.Identity);
        Endpoint(options.CallAutomation.Endpoint, "communication.azure.com");
        Endpoint(options.CallAutomation.PublicBaseUrl);
        if (!Guid.TryParse(options.CallAutomation.ResourceId, out _) ||
            !Guid.TryParse(options.CallAutomation.ResourceAccountObjectId, out _) ||
            !IsPhone(options.CallAutomation.TeamsServiceNumber))
            throw new ProviderException("TPE_SOURCE_NOT_CONFIGURED");
        Endpoint(options.VoiceLive.Endpoint, "services.ai.azure.com", "cognitiveservices.azure.com");
        if (options.VoiceLive.ApiVersion != VoiceApiVersion ||
            string.IsNullOrWhiteSpace(options.VoiceLive.Model) ||
            string.IsNullOrWhiteSpace(options.VoiceLive.Voice) ||
            string.IsNullOrWhiteSpace(options.VoiceLive.Locale) ||
            string.IsNullOrWhiteSpace(options.VoiceLive.TranscriptionModel))
            throw new ProviderException("VOICE_CONFIGURATION_INVALID");
        try
        {
            var culture = CultureInfo.GetCultureInfo(options.VoiceLive.Locale);
            if (culture.TwoLetterISOLanguageName != "en")
                throw new ProviderException("VOICE_LOCALE_UNSUPPORTED");
        }
        catch (CultureNotFoundException)
        {
            throw new ProviderException("VOICE_CONFIGURATION_INVALID");
        }
    }

    internal static void Deadline(CallContext context, TimeProvider clock)
    {
        if (clock.GetUtcNow() >= context.Deadline)
            throw new ProviderException("DEADLINE_EXCEEDED");
    }

    internal static void Text(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ProviderException("INVALID_TEXT");
    }
}
