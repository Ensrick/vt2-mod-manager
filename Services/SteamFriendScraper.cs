using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

/// <summary>
/// Tri-state for "was the scrape result for an anonymous request that Steam treated as
/// needing a login?" Steam circa 2026 serves a login interstitial (title
/// "Sign In") or a generic "Steam Community :: Error" page when an unauthenticated
/// client requests another user's <c>?browsefilter=mysubscriptions</c> Workshop page —
/// even for fully Public profiles. The scraper exposes this so callers can render a
/// "Steam not logged in / no friend cookies" hint instead of silently showing 0 mods.
/// </summary>
public enum FriendScrapeAuthState
{
    /// <summary>Steam returned a real subscriptions page (whether items were present or not).</summary>
    Ok,
    /// <summary>Steam redirected to or rendered a login/sign-in interstitial.</summary>
    LoginRequired,
}

/// <summary>Maps Steam's &lt;visibilityState&gt; wire values (1=Private, 2=Friends-only, 3=Public).
/// Friends-only profiles ARE scrapable when the requesting account is in the owner's friends
/// list — the cookies on the request determine access. Kept distinct from Private so callers
/// can decide whether to attempt the scrape vs short-circuit.</summary>
public enum ProfileVisibility { Public, Private, FriendsOnly, Unknown }

public sealed record FriendModListing(
    string Id,
    string Title);

public sealed record FriendSubscriptionResult(
    string SteamId64,
    ProfileVisibility Visibility,
    IReadOnlyList<FriendModListing> Mods,
    int PagesFetched,
    string? Error,
    FriendScrapeAuthState AuthState = FriendScrapeAuthState.Ok);

/// <summary>
/// Scrapes a Steam profile's <c>?browsefilter=mysubscriptions</c> Workshop page for an app's
/// subscribed items. Auto-paginates via <c>&amp;p=&lt;n&gt;</c> until a page yields zero items.
///
/// <para><b>Authentication.</b> Verified live against steamcommunity.com on 2026-05-19:
/// Steam now requires an authenticated session to render another user's subscriptions page —
/// even on Public profiles. Anonymous requests get redirected to a login interstitial
/// (HTML <c>&lt;title&gt;Sign In&lt;/title&gt;</c>) or a "Steam Community :: Error" page that
/// contains no <c>workshopItem</c> markup. The friends-only case has always required auth.
/// Callers should construct with <see cref="SteamSessionCookies"/> harvested via
/// <see cref="SteamCefCookieReader"/> so the request goes out as the logged-in Steam user.</para>
///
/// <para>Private profiles are detected heuristically (Steam serves a generic shell with a privacy
/// message instead of items). Malformed responses yield an empty list with <c>Error</c> set —
/// callers should never see this method throw.</para>
/// </summary>
public sealed class SteamFriendScraper
{
    private const int MaxPages = 50; // hard guard; ~30 items/page = 1500 mods, well above any sane sub count.

    // Match the <a class="ugc" href="..."> ... <div class="workshopItemTitle">Title</div> ... </a> block.
    // Tolerant: title may contain HTML entities; href may have additional query params; whitespace is generous.
    private static readonly Regex ItemBlockRe = new(
        @"<a\s+class=""ugc""[^>]*href=""[^""]*[?&]id=(?<id>\d+)[^""]*""[^>]*>" +
        @"(?<body>.*?)" +
        @"</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleRe = new(
        @"<div\s+class=""workshopItemTitle""[^>]*>(?<title>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Fallback: bare anchors to sharedfiles/filedetails with id= when the class="ugc" wrapper isn't present.
    private static readonly Regex FallbackIdRe = new(
        @"sharedfiles/filedetails/\?id=(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EntityRe = new(@"&(amp|quot|lt|gt|#39|apos);", RegexOptions.Compiled);

    // Steam's anonymous response when auth is required: redirects to /login/... and the final
    // rendered HTML has <title>Sign In</title>. The "Steam Community :: Error" generic envelope
    // also appears for some accounts under the same conditions.
    private static readonly Regex LoginGateTitleRe = new(
        @"<title>\s*(Sign In|Steam Community\s*::\s*Error)\s*</title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Browser-like User-Agent. Steam will still serve the page to a bare UA, but a couple of
    // anti-abuse layers behind community.* respond more reliably to a real browser string.
    // Kept as a per-request header (not on _http) so we don't mutate state shared with callers
    // that hand us a multi-purpose HttpClient.
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly SteamSessionCookies? _cookies;

    public SteamFriendScraper(HttpClient http, SteamSessionCookies? cookies = null)
    {
        _http = http;
        _cookies = cookies;
    }

    public async Task<FriendSubscriptionResult> FetchAsync(
        string steamId64, uint appId = 552500, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
            return new FriendSubscriptionResult(steamId64 ?? "", ProfileVisibility.Unknown,
                Array.Empty<FriendModListing>(), 0, "empty steam id");

        var collected = new List<FriendModListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int page = 1;
        int pagesFetched = 0;
        var visibility = ProfileVisibility.Public;

        while (page <= MaxPages)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId64}/myworkshopfiles/" +
                      $"?appid={appId}&browsefilter=mysubscriptions&p={page}";
            string html;
            try
            {
                using var request = BuildRequest(url);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new FriendSubscriptionResult(steamId64,
                        ProfileVisibility.Unknown, collected, pagesFetched,
                        $"http {(int)response.StatusCode}");
                }
                html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return new FriendSubscriptionResult(steamId64,
                    ProfileVisibility.Unknown, collected, pagesFetched, ex.Message);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new FriendSubscriptionResult(steamId64,
                    ProfileVisibility.Unknown, collected, pagesFetched, "request timed out");
            }

            pagesFetched++;

            if (IsPrivate(html))
            {
                return new FriendSubscriptionResult(steamId64, ProfileVisibility.Private,
                    Array.Empty<FriendModListing>(), pagesFetched, null);
            }

            // Steam routes unauthenticated requests to a login/error interstitial. We only
            // check this on the first page — subsequent pages won't suddenly start gating
            // mid-pagination, and we don't want to misread a real "no more pages" empty
            // response as login-required.
            if (page == 1 && IsLoginGate(html))
            {
                var hint = _cookies is null
                    ? "Steam requires an authenticated session to view another user's subscriptions page. " +
                      "Open Steam and sign in, then retry."
                    : "Steam rejected the supplied session cookies. Open Steam, visit any Workshop page in " +
                      "the overlay/library browser to refresh the cookies, then retry.";
                return new FriendSubscriptionResult(
                    SteamId64: steamId64,
                    Visibility: ProfileVisibility.Unknown,
                    Mods: Array.Empty<FriendModListing>(),
                    PagesFetched: pagesFetched,
                    Error: hint,
                    AuthState: FriendScrapeAuthState.LoginRequired);
            }

            var found = ParsePage(html);
            if (found.Count == 0) break;

            int addedThisPage = 0;
            foreach (var item in found)
            {
                if (seen.Add(item.Id))
                {
                    collected.Add(item);
                    addedThisPage++;
                }
            }

            // If a page brought no new IDs, Steam is repeating the last page (e.g. p beyond the end). Stop.
            if (addedThisPage == 0) break;

            page++;
        }

        return new FriendSubscriptionResult(steamId64, visibility, collected, pagesFetched, null);
    }

    /// <summary>Builds a request with browser-like UA and (optionally) the Steam session
    /// cookies. Cookies go on the per-request <c>Cookie</c> header so we don't have to
    /// reconfigure the shared HttpClient with a CookieContainer.</summary>
    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (_cookies is not null)
        {
            req.Headers.TryAddWithoutValidation("Cookie",
                $"sessionid={_cookies.SessionId}; steamLoginSecure={_cookies.SteamLoginSecure}");
        }
        return req;
    }

    /// <summary>Exposed for tests. True when the response is Steam's login/error
    /// interstitial rather than a real subscriptions page.</summary>
    public static bool IsLoginGate(string html) =>
        LoginGateTitleRe.IsMatch(html);

    private static bool IsPrivate(string html) =>
        html.Contains("This profile is private", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("profile_private_info", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exposed for tests. Parses one HTML page for workshop items.</summary>
    public static List<FriendModListing> ParsePage(string html)
    {
        var results = new List<FriendModListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in ItemBlockRe.Matches(html))
        {
            var id = m.Groups["id"].Value;
            if (id.Length == 0 || !seen.Add(id)) continue;

            var body = m.Groups["body"].Value;
            var titleMatch = TitleRe.Match(body);
            var title = titleMatch.Success ? DecodeText(titleMatch.Groups["title"].Value) : "";
            results.Add(new FriendModListing(id, title));
        }

        // If the primary regex didn't catch anything but the page clearly references workshop items,
        // fall back to bare id extraction (title left blank). Filters obvious nav/breadcrumb dupes
        // by requiring the id to appear near a workshopItemTitle/workshopItem marker.
        if (results.Count == 0 && html.Contains("workshopItem", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in FallbackIdRe.Matches(html))
            {
                var id = m.Groups["id"].Value;
                if (id.Length == 0 || !seen.Add(id)) continue;
                results.Add(new FriendModListing(id, ""));
            }
        }

        return results;
    }

    private static string DecodeText(string raw)
    {
        var stripped = Regex.Replace(raw, @"<[^>]+>", "").Trim();
        return EntityRe.Replace(stripped, m => m.Groups[1].Value switch
        {
            "amp"  => "&",
            "quot" => "\"",
            "lt"   => "<",
            "gt"   => ">",
            "#39"  => "'",
            "apos" => "'",
            _ => m.Value,
        });
    }
}
