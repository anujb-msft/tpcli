using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tpcli.Azure;

internal sealed record AzureRedactedQuery(bool HadQuery);

internal sealed class AzureMediaPrivacyFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (http, downstream) =>
        {
            var sensitive = Sanitize(http);
            try { await downstream(http).ConfigureAwait(false); }
            finally { if (sensitive) ScrubActivity(); }
        });
        next(app);
    };

    internal static bool Sanitize(HttpContext http)
    {
        var query = http.Request.QueryString.Value ?? "";
        if (!http.Request.Path.StartsWithSegments("/azure") &&
            !HasCapabilityKey(query))
            return false;
        http.Features.Set(MediaGrantAuthentication.ParseCredential(query));
        http.Features.Set(new AzureRedactedQuery(query.Length != 0));
        http.Request.QueryString = QueryString.Empty;
        http.Request.Query = QueryCollection.Empty;
        if (http.Features.Get<IHttpRequestFeature>() is { } request)
            request.RawTarget = http.Request.PathBase.Add(http.Request.Path).ToUriComponent();
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        ScrubActivity();
        return true;
    }

    private static bool HasCapabilityKey(string query)
    {
        if (query.Contains(MediaGrantAuthentication.QueryName, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var field in query.TrimStart('?').Split('&'))
        {
            var separator = field.IndexOf('=');
            var key = separator < 0 ? field : field[..separator];
            if (Uri.UnescapeDataString(key).Equals(MediaGrantAuthentication.QueryName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void ScrubActivity()
    {
        if (Activity.Current is not { } activity) return;
        foreach (var tag in activity.TagObjects.ToArray())
            if (tag.Key.StartsWith("url.", StringComparison.OrdinalIgnoreCase) ||
                tag.Key.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
                tag.Key is "http.url" or "http.target" or "http.query" ||
                tag.Key.StartsWith("http.request.header", StringComparison.OrdinalIgnoreCase))
                activity.SetTag(tag.Key, null);
        activity.IsAllDataRequested = false;
    }
}

internal sealed class AzurePrivacyLogFilters : IPostConfigureOptions<LoggerFilterOptions>
{
    public void PostConfigure(string? name, LoggerFilterOptions options)
    {
        // Request-start/Kestrel logs precede middleware. Wrap every rule, including
        // provider-specific and more-specific configuration, rather than relying
        // on an easily overridden category-level setting.
        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            options.Rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                (provider, category, level) => !SensitiveCategory(category) && (rule.Filter?.Invoke(provider, category, level) ?? true));
        }
        options.Rules.Add(new LoggerFilterRule(null, null, null, (_, category, _) => !SensitiveCategory(category)));
    }

    private static bool SensitiveCategory(string? category) =>
        category?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true ||
        category?.StartsWith("Azure.", StringComparison.Ordinal) == true ||
        category?.StartsWith("System.Net.Http", StringComparison.Ordinal) == true;
}
