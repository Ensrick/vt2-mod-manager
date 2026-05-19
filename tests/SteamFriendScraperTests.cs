using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class SteamFriendScraperTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "friends", name);

    private static string ReadFixture(string name) => File.ReadAllText(FixturePath(name));

    /// <summary>Returns a response for each request based on the <c>p=</c> page parameter.</summary>
    private sealed class PagedHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _pages;
        public int CallCount { get; private set; }
        public List<string> RequestedUrls { get; } = new();

        public PagedHandler(Dictionary<int, string> pages) { _pages = pages; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            RequestedUrls.Add(request.RequestUri!.AbsoluteUri);

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.TryParse(query["p"], out var p) ? p : 1;

            if (!_pages.TryGetValue(page, out var body))
                body = ReadFixture("mysubscriptions_empty.html");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public void ParsePage_extracts_id_and_title_from_workshop_items()
    {
        var html = ReadFixture("mysubscriptions_page1.html");

        var items = SteamFriendScraper.ParsePage(html);

        Assert.Equal(3, items.Count);
        Assert.Equal("1369573612", items[0].Id);
        Assert.Equal("Vermintide Mod Framework", items[0].Title);
        Assert.Equal("2001394219", items[1].Id);
        Assert.Equal("Crosshair Kill Confirmation & X-Ray", items[1].Title); // HTML entity decoded
        Assert.Equal("1373595361", items[2].Id);
        Assert.Equal("No Wobble", items[2].Title);
    }

    [Fact]
    public void ParsePage_returns_empty_for_no_items_page()
    {
        var html = ReadFixture("mysubscriptions_empty.html");
        var items = SteamFriendScraper.ParsePage(html);
        Assert.Empty(items);
    }

    [Fact]
    public async Task FetchAsync_returns_public_result_with_items()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("mysubscriptions_page1.html") },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(ProfileVisibility.Public, result.Visibility);
        Assert.Equal(3, result.Mods.Count);
        Assert.Null(result.Error);
        // p=1 had items, p=2 returns empty (default) → 2 pages fetched then loop exits.
        Assert.Equal(2, result.PagesFetched);
    }

    [Fact]
    public async Task FetchAsync_paginates_until_empty()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("mysubscriptions_page1.html") },
            { 2, """
                <html><body>
                <div class="workshopItem">
                  <a class="ugc" href="https://steamcommunity.com/sharedfiles/filedetails/?id=9999999999">
                    <div class="workshopItemTitle">Page Two Mod</div>
                  </a>
                </div>
                </body></html>
                """ },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(ProfileVisibility.Public, result.Visibility);
        Assert.Equal(4, result.Mods.Count);
        Assert.Contains(result.Mods, m => m.Id == "9999999999");
        Assert.Equal(3, result.PagesFetched); // page 1 + page 2 + empty page 3
        Assert.Contains(handler.RequestedUrls, u => u.Contains("p=1"));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("p=2"));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("p=3"));
    }

    [Fact]
    public async Task FetchAsync_includes_vt2_appid_in_url()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("mysubscriptions_empty.html") },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        await scraper.FetchAsync("76561197960287930");

        Assert.Contains(handler.RequestedUrls, u => u.Contains("appid=552500"));
        Assert.Contains(handler.RequestedUrls, u => u.Contains("browsefilter=mysubscriptions"));
    }

    [Fact]
    public async Task FetchAsync_detects_private_profile()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("profile_private.html") },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(ProfileVisibility.Private, result.Visibility);
        Assert.Empty(result.Mods);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task FetchAsync_returns_unknown_with_error_on_http_failure()
    {
        var handler = new ErrorHandler(HttpStatusCode.InternalServerError);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(ProfileVisibility.Unknown, result.Visibility);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Mods);
    }

    [Fact]
    public void IsLoginGate_detects_sign_in_title()
    {
        // Live capture from 2026-05-19: anonymous request to /profiles/<id>/myworkshopfiles
        // with ?browsefilter=mysubscriptions on a profile we're not a friend of gets redirected
        // to a generic "Steam Community :: Error" page that asks the user to sign in.
        var html = ReadFixture("login_gate.html");
        Assert.True(SteamFriendScraper.IsLoginGate(html));
    }

    [Fact]
    public void IsLoginGate_false_for_real_subscriptions_page()
    {
        var html = ReadFixture("mysubscriptions_page1.html");
        Assert.False(SteamFriendScraper.IsLoginGate(html));
    }

    [Fact]
    public async Task FetchAsync_returns_LoginRequired_when_response_is_login_gate()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("login_gate.html") },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(FriendScrapeAuthState.LoginRequired, result.AuthState);
        Assert.Empty(result.Mods);
        Assert.NotNull(result.Error);
        Assert.Contains("Steam", result.Error);
        Assert.Equal(1, result.PagesFetched);
    }

    [Fact]
    public async Task FetchAsync_sends_cookie_header_when_cookies_supplied()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("mysubscriptions_page1.html") },
        };
        var handler = new HeaderCapturingPagedHandler(pages);
        var cookies = new SteamSessionCookies("eyJhbGciOiJ...JWT...", "abc123sessionid");
        var scraper = new SteamFriendScraper(new HttpClient(handler), cookies);

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(3, result.Mods.Count);
        Assert.NotEmpty(handler.CookieHeaders);
        var cookieHeader = handler.CookieHeaders[0];
        Assert.Contains("sessionid=abc123sessionid", cookieHeader);
        Assert.Contains("steamLoginSecure=eyJhbGciOiJ...JWT...", cookieHeader);
        // User-Agent is set per-request (not on the shared HttpClient).
        Assert.Contains(handler.UserAgents, ua => ua.Contains("Mozilla/5.0"));
    }

    [Fact]
    public async Task FetchAsync_omits_cookie_header_when_no_cookies()
    {
        var pages = new Dictionary<int, string>
        {
            { 1, ReadFixture("mysubscriptions_page1.html") },
        };
        var handler = new HeaderCapturingPagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler), cookies: null);

        await scraper.FetchAsync("76561197960287930");

        Assert.Empty(handler.CookieHeaders); // no Cookie header attached
    }

    /// <summary>Like <see cref="PagedHandler"/> but also captures Cookie and User-Agent
    /// headers off each request so tests can assert what we actually sent on the wire.</summary>
    private sealed class HeaderCapturingPagedHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _pages;
        public List<string> CookieHeaders { get; } = new();
        public List<string> UserAgents    { get; } = new();

        public HeaderCapturingPagedHandler(Dictionary<int, string> pages) { _pages = pages; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.TryGetValues("Cookie", out var cookieVals))
                CookieHeaders.AddRange(cookieVals);
            if (request.Headers.TryGetValues("User-Agent", out var uaVals))
                UserAgents.AddRange(uaVals);

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.TryParse(query["p"], out var p) ? p : 1;
            if (!_pages.TryGetValue(page, out var body))
                body = ReadFixture("mysubscriptions_empty.html");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
        }
    }

    [Fact]
    public async Task FetchAsync_dedupes_repeated_ids_across_pages()
    {
        var sameHtml = ReadFixture("mysubscriptions_page1.html");
        // Steam sometimes serves the last real page when p exceeds the count. We should
        // stop when a page brings no NEW ids (rather than infinite-loop).
        var pages = new Dictionary<int, string>
        {
            { 1, sameHtml },
            { 2, sameHtml },
            { 3, sameHtml },
        };
        var handler = new PagedHandler(pages);
        var scraper = new SteamFriendScraper(new HttpClient(handler));

        var result = await scraper.FetchAsync("76561197960287930");

        Assert.Equal(3, result.Mods.Count);
        Assert.Equal(2, result.PagesFetched); // p1 added 3, p2 added 0 → stop
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        public ErrorHandler(HttpStatusCode code) { _code = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent("") });
    }
}
