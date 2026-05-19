using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

/// <summary>
/// Structured result of one XML profile probe. <see cref="OnlineNow"/> is <c>null</c> when
/// Steam doesn't report a state (typically private profiles, where the <c>&lt;onlineState&gt;</c>
/// element is absent). Display name and avatar URL are null when the corresponding CDATA
/// blocks aren't present (Steam omits them on some private profiles).
/// </summary>
public sealed record SteamProfileXmlResult(
    string SteamId64,
    ProfileVisibility Visibility,
    bool? OnlineNow,
    string? DisplayName,
    string? AvatarUrl);

/// <summary>
/// Fetches structured profile metadata from Steam's public XML endpoint
/// (<c>https://steamcommunity.com/profiles/&lt;steamid64&gt;?xml=1</c>).
///
/// Used as a pre-flight before the HTML subscriptions scrape so callers can avoid hammering
/// the workshop page for friends-only / private profiles that will never expose subs anyway.
/// The endpoint is reachable without an API key and respects no rate-limit other than Steam's
/// generic anti-abuse throttle.
///
/// <para>
/// <c>&lt;visibilityState&gt;</c> mapping (matches Steam's wire protocol):
/// <list type="bullet">
///   <item><description>1 → <see cref="ProfileVisibility.Private"/></description></item>
///   <item><description>2 → <see cref="ProfileVisibility.Private"/> (friends-only — vt2 doesn't distinguish; see note)</description></item>
///   <item><description>3 → <see cref="ProfileVisibility.Public"/></description></item>
///   <item><description>anything else / missing → <see cref="ProfileVisibility.Unknown"/></description></item>
/// </list>
/// vt2-mod-manager's <see cref="ProfileVisibility"/> enum has no FriendsOnly member, so for our
/// purposes both Private and FriendsOnly collapse to Private (neither will expose subs to a
/// non-friend scraper). Callers needing the finer distinction should inspect the raw XML.
/// </para>
/// </summary>
public sealed class SteamProfileXmlClient
{
    private static readonly Regex VisibilityStateRe =
        new(@"<visibilityState>\s*(\d+)\s*</visibilityState>", RegexOptions.Compiled);
    private static readonly Regex OnlineStateRe =
        new(@"<onlineState>\s*([\w\-]+)\s*</onlineState>", RegexOptions.Compiled);
    private static readonly Regex SteamId64Re =
        new(@"<steamID64>\s*(\d{17})\s*</steamID64>", RegexOptions.Compiled);
    private static readonly Regex PersonaNameRe =
        new(@"<steamID>\s*<!\[CDATA\[(?<name>[^\]]*)\]\]>\s*</steamID>", RegexOptions.Compiled);
    // <avatarFull> if present, otherwise <avatarMedium>, otherwise <avatarIcon>. We capture all
    // three and pick at parse time so we get the best available without re-scanning.
    private static readonly Regex AvatarFullRe =
        new(@"<avatarFull>\s*<!\[CDATA\[(?<url>[^\]]+)\]\]>\s*</avatarFull>", RegexOptions.Compiled);
    private static readonly Regex AvatarMediumRe =
        new(@"<avatarMedium>\s*<!\[CDATA\[(?<url>[^\]]+)\]\]>\s*</avatarMedium>", RegexOptions.Compiled);
    private static readonly Regex AvatarIconRe =
        new(@"<avatarIcon>\s*<!\[CDATA\[(?<url>[^\]]+)\]\]>\s*</avatarIcon>", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public SteamProfileXmlClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Probe one profile. Returns <c>null</c> on transport failure or HTTP error (404 / 5xx /
    /// timeout) so the caller can distinguish "Steam said this is private" from "we never heard
    /// back". A successful HTTP response with a body Steam couldn't parse into a profile (e.g.
    /// the "profile not found" error envelope) still yields a non-null result with
    /// <see cref="ProfileVisibility.Unknown"/>.
    /// </summary>
    public async Task<SteamProfileXmlResult?> FetchAsync(string steamId64, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamId64)) return null;

        string body;
        try
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)  { return null; }
        catch (TaskCanceledException) { return null; }

        return Parse(steamId64, body);
    }

    /// <summary>
    /// Probe many profiles in parallel. Returned dictionary is keyed by the input SteamID64
    /// and only contains entries for IDs we got a parseable XML response for — failed probes
    /// are silently dropped (callers iterate the input list to find missing entries).
    ///
    /// <para><paramref name="maxConcurrent"/> caps the simultaneous request fan-out so we don't
    /// trip Steam's anti-abuse throttle. 8 is conservative; Steam happily serves the XML
    /// endpoint at much higher rates, but the slow path (HTML scrape) is what we're saving
    /// here and the marginal speedup of 16+ isn't worth tempting fate.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SteamProfileXmlResult>> FetchManyAsync(
        IEnumerable<string> steamId64s,
        int maxConcurrent = 8,
        CancellationToken ct = default)
    {
        if (steamId64s is null) throw new ArgumentNullException(nameof(steamId64s));
        if (maxConcurrent < 1) maxConcurrent = 1;

        var ids = steamId64s
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var results = new ConcurrentDictionary<string, SteamProfileXmlResult>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await FetchAsync(id, ct).ConfigureAwait(false);
                if (r is not null) results[id] = r;
            }
            finally { gate.Release(); }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Pure parser exposed for tests. Reads visibility, online state, display name, and avatar
    /// URL from a Steam profile XML body. Missing fields collapse to safe defaults (Unknown
    /// visibility, null online state, null name/avatar).
    /// </summary>
    public static SteamProfileXmlResult Parse(string steamId64, string xml)
    {
        // Prefer the SteamID64 from the body (handles the case where the caller passed a
        // vanity but we want the canonical id) — fall back to whatever the caller gave us.
        var sidMatch = SteamId64Re.Match(xml);
        var sid = sidMatch.Success ? sidMatch.Groups[1].Value : steamId64;

        return new SteamProfileXmlResult(
            SteamId64:   sid,
            Visibility:  ParseVisibility(xml),
            OnlineNow:   ParseOnlineNow(xml),
            DisplayName: ParseDisplayName(xml),
            AvatarUrl:   ParseAvatarUrl(xml));
    }

    /// <summary>
    /// Parses <c>&lt;visibilityState&gt;</c>. 1=Private and 2=FriendsOnly both collapse to
    /// <see cref="ProfileVisibility.Private"/> (vt2's enum doesn't distinguish, and the
    /// downstream effect — no scrapeable subs — is the same). 3=Public. Anything else
    /// (including missing element) is <see cref="ProfileVisibility.Unknown"/>.
    /// </summary>
    public static ProfileVisibility ParseVisibility(string xml)
    {
        var m = VisibilityStateRe.Match(xml);
        if (!m.Success) return ProfileVisibility.Unknown;
        return m.Groups[1].Value switch
        {
            "1" => ProfileVisibility.Private,
            "2" => ProfileVisibility.Private,   // friends-only — not exposed to non-friends
            "3" => ProfileVisibility.Public,
            _   => ProfileVisibility.Unknown,
        };
    }

    /// <summary>
    /// Parses <c>&lt;onlineState&gt;</c>. <c>online</c>/<c>in-game</c> → true,
    /// <c>offline</c> → false, missing or unrecognized → null.
    /// </summary>
    public static bool? ParseOnlineNow(string xml)
    {
        var m = OnlineStateRe.Match(xml);
        if (!m.Success) return null;
        return m.Groups[1].Value.ToLowerInvariant() switch
        {
            "online"  => true,
            "in-game" => true,
            "offline" => false,
            _         => null,
        };
    }

    private static string? ParseDisplayName(string xml)
    {
        var m = PersonaNameRe.Match(xml);
        return m.Success ? m.Groups["name"].Value : null;
    }

    private static string? ParseAvatarUrl(string xml)
    {
        var full = AvatarFullRe.Match(xml);
        if (full.Success) return full.Groups["url"].Value;
        var medium = AvatarMediumRe.Match(xml);
        if (medium.Success) return medium.Groups["url"].Value;
        var icon = AvatarIconRe.Match(xml);
        return icon.Success ? icon.Groups["url"].Value : null;
    }
}
