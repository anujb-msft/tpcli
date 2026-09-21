using Microsoft.Extensions.Options;

namespace Tpcli.Runtime;

internal sealed class TransportPrivacyLogFilters : IPostConfigureOptions<LoggerFilterOptions>
{
    public void PostConfigure(string? name, LoggerFilterOptions options)
    {
        // Transport logging starts before query sanitization. Provider-specific rules
        // must not override suppression of URLs, SDK request bodies, or exceptions.
        for (var index = 0; index < options.Rules.Count; index++)
        {
            var rule = options.Rules[index];
            options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                (provider, category, level) => !Sensitive(category) && (rule.Filter?.Invoke(provider, category, level) ?? true));
        }
        options.Rules.Add(new LoggerFilterRule(null, null, null, (_, category, _) => !Sensitive(category)));
    }

    private static bool Sensitive(string? category) =>
        category?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true ||
        category?.StartsWith("System.Net", StringComparison.Ordinal) == true ||
        category?.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal) == true ||
        category?.StartsWith("Azure.", StringComparison.Ordinal) == true ||
        category?.StartsWith("Microsoft.ApplicationInsights", StringComparison.Ordinal) == true ||
        category?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true;
}
