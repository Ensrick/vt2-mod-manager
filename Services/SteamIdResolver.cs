using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

/// <summary>
/// Resolves any of: raw 17-digit SteamID64, vanity slug, profile URL — to a SteamID64 string.
/// Hits <c>steamcommunity.com/id/&lt;vanity&gt;?xml=1</c> for vanity lookup (no API key required).
/// Returns null when input cannot be resolved.
/// </summary>
public sealed class SteamIdResolver
{
    private static readonly Regex SteamId64Re = new(@"^\d{17}$", RegexOptions.Compiled);
    private static readonly Regex ProfilesUrlRe = new(
        @"steamcommunity\.com/profiles/(\d{17})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IdUrlRe = new(
        @"steamcommunity\.com/id/([^/?#\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XmlSidRe = new(
        @"<steamID64>\s*(\d{17})\s*</steamID64>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;

    public SteamIdResolver(HttpClient http)
    {
        _http = http;
    }

    public async Task<string?> ResolveAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();

        // Raw SteamID64.
        if (SteamId64Re.IsMatch(s)) return s;

        // /profiles/<sid64> URL — extract directly, no network call needed.
        var pm = ProfilesUrlRe.Match(s);
        if (pm.Success) return pm.Groups[1].Value;

        // /id/<vanity> URL — extract slug, then resolve.
        var im = IdUrlRe.Match(s);
        string vanity;
        if (im.Success)
        {
            vanity = im.Groups[1].Value;
        }
        else
        {
            // Bare vanity slug.
            vanity = s.Trim('/');
            if (vanity.Length == 0) return null;
            // If it still looks URL-like but we didn't match, bail.
            if (vanity.Contains('/') || vanity.Contains(' ')) return null;
        }

        vanity = vanity.Trim('/').ToLowerInvariant();
        if (vanity.Length == 0) return null;

        return await ResolveVanityAsync(vanity, ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveVanityAsync(string vanity, CancellationToken ct)
    {
        var url = $"https://steamcommunity.com/id/{Uri.EscapeDataString(vanity)}?xml=1";
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = XmlSidRe.Match(xml);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
