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

public sealed class SteamProfileXmlClientTests
{
    // --- fixtures ------------------------------------------------------------
    // These four fixtures cover the full visibilityState matrix (3/2/1) plus the
    // missing-profile envelope Steam returns for unknown SteamID64s.
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "steam-profile-xml", name);

    private static string ReadFixture(string name) => File.ReadAllText(FixturePath(name));

    /// <summary>
    /// Routes one steamid64 → one fixture body (or a 404 if the fixture is null).
    /// Mirrors the StubHandler pattern in workshop-sentinel's FriendSubscriptionsClientTests.
    /// </summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        // null body → 404; otherwise serve as 200 OK.
        private readonly Dictionary<string, string?> _bodyBySteamId;
        public int CallCount { get; private set; }
        public List<string> RequestedUrls { get; } = new();

        public FixtureHandler(Dictionary<string, string?> bodyBySteamId)
        {
            _bodyBySteamId = bodyBySteamId;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var uri = request.RequestUri!.AbsoluteUri;
            RequestedUrls.Add(uri);

            // Pluck the 17-digit id out of /profiles/<id>/.
            var sid = System.Text.RegularExpressions.Regex
                .Match(uri, @"/profiles/(\d{17})").Groups[1].Value;

            if (!_bodyBySteamId.TryGetValue(sid, out var body) || body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(""),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
        }
    }

    // --- pure parser ---------------------------------------------------------

    [Theory]
    [InlineData("<visibilityState>3</visibilityState>", ProfileVisibility.Public)]
    [InlineData("<visibilityState>2</visibilityState>", ProfileVisibility.FriendsOnly)]
    [InlineData("<visibilityState>1</visibilityState>", ProfileVisibility.Private)]
    [InlineData("<visibilityState>  3  </visibilityState>", ProfileVisibility.Public)]
    [InlineData("<profile>no field</profile>",          ProfileVisibility.Unknown)]
    [InlineData("<visibilityState>99</visibilityState>", ProfileVisibility.Unknown)]
    public void ParseVisibility_maps_steam_visibilityState_integer(string xml, ProfileVisibility expected)
    {
        Assert.Equal(expected, SteamProfileXmlClient.ParseVisibility(xml));
    }

    [Theory]
    [InlineData("<onlineState>online</onlineState>",  true)]
    [InlineData("<onlineState>in-game</onlineState>", true)]
    [InlineData("<onlineState>ONLINE</onlineState>",  true)]   // case-insensitive
    [InlineData("<onlineState>offline</onlineState>", false)]
    public void ParseOnlineNow_recognized_states(string xml, bool expected)
    {
        Assert.Equal(expected, SteamProfileXmlClient.ParseOnlineNow(xml));
    }

    [Theory]
    [InlineData("<profile>no field</profile>")]
    [InlineData("<onlineState>away</onlineState>")] // away/busy/snooze etc. aren't a clean true/false
    public void ParseOnlineNow_returns_null_when_unknown(string xml)
    {
        Assert.Null(SteamProfileXmlClient.ParseOnlineNow(xml));
    }

    [Fact]
    public void Parse_extracts_all_fields_from_public_profile_fixture()
    {
        var xml = ReadFixture("public.xml");

        var r = SteamProfileXmlClient.Parse("76561198000000001", xml);

        Assert.Equal("76561198000000001",   r.SteamId64);
        Assert.Equal(ProfileVisibility.Public, r.Visibility);
        Assert.True(r.OnlineNow);
        Assert.Equal("PublicUser", r.DisplayName);
        // Should prefer <avatarFull> when present.
        Assert.Equal("https://avatars.steamstatic.com/public_full.jpg", r.AvatarUrl);
    }

    [Fact]
    public void Parse_handles_friends_only_fixture()
    {
        var xml = ReadFixture("friends_only.xml");

        var r = SteamProfileXmlClient.Parse("76561198000000002", xml);

        Assert.Equal(ProfileVisibility.FriendsOnly, r.Visibility);
        Assert.False(r.OnlineNow);
        Assert.Equal("FriendsOnlyUser", r.DisplayName);
    }

    [Fact]
    public void Parse_handles_private_fixture_with_missing_online_state()
    {
        var xml = ReadFixture("private.xml");

        var r = SteamProfileXmlClient.Parse("76561198000000003", xml);

        Assert.Equal(ProfileVisibility.Private, r.Visibility);
        // Private profiles often omit <onlineState> entirely → null.
        Assert.Null(r.OnlineNow);
        Assert.Equal("PrivateUser", r.DisplayName);
    }

    [Fact]
    public void Parse_prefers_id_in_body_over_id_passed_in()
    {
        var xml = ReadFixture("public.xml");

        // Caller passes a different id (e.g. they resolved by vanity but the body has the canonical).
        var r = SteamProfileXmlClient.Parse("00000000000000000", xml);

        Assert.Equal("76561198000000001", r.SteamId64);
    }

    [Fact]
    public void Parse_falls_back_to_passed_id_when_body_has_no_steamID64()
    {
        var r = SteamProfileXmlClient.Parse("76561198000000001", "<profile></profile>");
        Assert.Equal("76561198000000001", r.SteamId64);
        Assert.Equal(ProfileVisibility.Unknown, r.Visibility);
        Assert.Null(r.OnlineNow);
        Assert.Null(r.DisplayName);
        Assert.Null(r.AvatarUrl);
    }

    // --- FetchAsync ----------------------------------------------------------

    [Fact]
    public async Task FetchAsync_returns_public_result_for_public_profile()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000001"] = ReadFixture("public.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000001");

        Assert.NotNull(r);
        Assert.Equal(ProfileVisibility.Public, r!.Visibility);
        Assert.True(r.OnlineNow);
        Assert.Equal("PublicUser", r.DisplayName);
        Assert.Single(handler.RequestedUrls);
        Assert.Contains("?xml=1",                handler.RequestedUrls[0]);
        Assert.Contains("/profiles/76561198000000001", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task FetchAsync_returns_friends_only_visibility()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000002"] = ReadFixture("friends_only.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000002");

        Assert.NotNull(r);
        Assert.Equal(ProfileVisibility.FriendsOnly, r!.Visibility);
        Assert.False(r.OnlineNow);
    }

    [Fact]
    public async Task FetchAsync_returns_private_for_private_profile()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000003"] = ReadFixture("private.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000003");

        Assert.NotNull(r);
        Assert.Equal(ProfileVisibility.Private, r!.Visibility);
        Assert.Null(r.OnlineNow);
    }

    [Fact]
    public async Task FetchAsync_returns_null_for_404()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000099"] = null,  // → 404
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000099");

        Assert.Null(r);
    }

    [Fact]
    public async Task FetchAsync_returns_unknown_for_missing_profile_envelope_with_200()
    {
        // Some "not found" responses come back as 200 OK with an <error> envelope rather than 404.
        var handler = new FixtureHandler(new()
        {
            ["76561198000000099"] = ReadFixture("missing.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000099");

        Assert.NotNull(r);
        Assert.Equal(ProfileVisibility.Unknown, r!.Visibility);
        Assert.Null(r.OnlineNow);
        Assert.Null(r.DisplayName);
        Assert.Null(r.AvatarUrl);
    }

    [Fact]
    public async Task FetchAsync_returns_null_on_transport_error()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var r = await client.FetchAsync("76561198000000001");

        Assert.Null(r);
    }

    [Fact]
    public async Task FetchAsync_returns_null_for_empty_input()
    {
        var handler = new FixtureHandler(new());
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        Assert.Null(await client.FetchAsync(""));
        Assert.Null(await client.FetchAsync("   "));
        Assert.Equal(0, handler.CallCount); // never made a request
    }

    // --- FetchManyAsync ------------------------------------------------------

    [Fact]
    public async Task FetchManyAsync_returns_one_entry_per_successful_probe()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000001"] = ReadFixture("public.xml"),
            ["76561198000000002"] = ReadFixture("friends_only.xml"),
            ["76561198000000003"] = ReadFixture("private.xml"),
            ["76561198000000099"] = null, // → 404, dropped from result
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var results = await client.FetchManyAsync(new[]
        {
            "76561198000000001",
            "76561198000000002",
            "76561198000000003",
            "76561198000000099",
        });

        Assert.Equal(3, results.Count);
        Assert.Equal(ProfileVisibility.Public,      results["76561198000000001"].Visibility);
        Assert.Equal(ProfileVisibility.FriendsOnly, results["76561198000000002"].Visibility);
        Assert.Equal(ProfileVisibility.Private,     results["76561198000000003"].Visibility);
        Assert.False(results.ContainsKey("76561198000000099"));
    }

    [Fact]
    public async Task FetchManyAsync_deduplicates_input()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000001"] = ReadFixture("public.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var results = await client.FetchManyAsync(new[]
        {
            "76561198000000001",
            "76561198000000001",
            "76561198000000001",
        });

        Assert.Single(results);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchManyAsync_skips_empty_inputs()
    {
        var handler = new FixtureHandler(new()
        {
            ["76561198000000001"] = ReadFixture("public.xml"),
        });
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var results = await client.FetchManyAsync(new[] { "", "   ", "76561198000000001" });

        Assert.Single(results);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchManyAsync_respects_max_concurrent_cap()
    {
        // We can't observe concurrency directly without flakiness; just verify the call goes
        // through with a low cap and returns expected results. The cap-clamping branch
        // (maxConcurrent < 1) is also exercised here.
        var bodies = new Dictionary<string, string?>();
        for (int i = 1; i <= 10; i++)
            bodies[$"765611980000000{i:D2}"] = ReadFixture("public.xml");

        var handler = new FixtureHandler(bodies);
        var client = new SteamProfileXmlClient(new HttpClient(handler));

        var results = await client.FetchManyAsync(bodies.Keys, maxConcurrent: 0);

        Assert.Equal(10, results.Count);
        Assert.Equal(10, handler.CallCount);
    }

    // --- helpers -------------------------------------------------------------

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) { _ex = ex; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(_ex);
    }
}
