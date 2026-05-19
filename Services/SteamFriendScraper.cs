using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

public enum ProfileVisibility { Public, Private, Unknown }

public sealed record FriendModListing(
    string Id,
    string Title);

public sealed record FriendSubscriptionResult(
    string SteamId64,
    ProfileVisibility Visibility,
    IReadOnlyList<FriendModListing> Mods,
    int PagesFetched,
    string? Error);

/// <summary>
/// Scrapes a Steam profile's <c>?browsefilter=mysubscriptions</c> Workshop page for an app's
/// subscribed items. Auto-paginates via <c>&amp;p=&lt;n&gt;</c> until a page yields zero items.
///
/// Private profiles are detected heuristically (Steam serves a generic shell with a privacy
/// message instead of items). Malformed responses yield an empty list with <c>Error</c> set —
/// callers should never see this method throw.
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

    private readonly HttpClient _http;

    public SteamFriendScraper(HttpClient http)
    {
        _http = http;
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
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
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
