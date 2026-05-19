using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class SteamIdResolverTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "friends", name);

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _responder;
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public FixtureHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            var (code, body) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
        }
    }

    private static SteamIdResolver MakeResolver(FixtureHandler handler) =>
        new(new HttpClient(handler));

    [Fact]
    public async Task Passes_through_17_digit_steamid64()
    {
        var handler = new FixtureHandler(_ => (HttpStatusCode.OK, ""));
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("76561197960287930");

        Assert.Equal("76561197960287930", result);
        Assert.Equal(0, handler.CallCount); // no network call needed
    }

    [Fact]
    public async Task Extracts_sid_from_profiles_url()
    {
        var handler = new FixtureHandler(_ => (HttpStatusCode.OK, ""));
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("https://steamcommunity.com/profiles/76561197960287930/");
        Assert.Equal("76561197960287930", result);

        var result2 = await r.ResolveAsync("steamcommunity.com/profiles/76561197960287930");
        Assert.Equal("76561197960287930", result2);

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Resolves_vanity_url_via_xml_endpoint()
    {
        var xml = File.ReadAllText(FixturePath("vanity_resolve.xml"));
        var handler = new FixtureHandler(req =>
        {
            Assert.Contains("/id/gabelogannewell", req.RequestUri!.AbsoluteUri);
            Assert.Contains("xml=1", req.RequestUri!.Query);
            return (HttpStatusCode.OK, xml);
        });
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("https://steamcommunity.com/id/gabelogannewell/");

        Assert.Equal("76561197960287930", result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Resolves_bare_vanity_slug()
    {
        var xml = File.ReadAllText(FixturePath("vanity_resolve.xml"));
        var handler = new FixtureHandler(_ => (HttpStatusCode.OK, xml));
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("gabelogannewell");

        Assert.Equal("76561197960287930", result);
    }

    [Fact]
    public async Task Lowercases_and_trims_vanity()
    {
        var xml = File.ReadAllText(FixturePath("vanity_resolve.xml"));
        var handler = new FixtureHandler(req =>
        {
            // Lowercased before being sent.
            Assert.Contains("/id/gabelogannewell", req.RequestUri!.AbsoluteUri);
            return (HttpStatusCode.OK, xml);
        });
        var r = MakeResolver(handler);

        await r.ResolveAsync("  GabeLoganNewell  ");
    }

    [Fact]
    public async Task Returns_null_for_unresolvable_vanity()
    {
        var handler = new FixtureHandler(_ => (HttpStatusCode.OK,
            "<response><error>The specified profile could not be found.</error></response>"));
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("definitely_not_a_real_vanity_slug");

        Assert.Null(result);
    }

    [Fact]
    public async Task Returns_null_for_empty_or_whitespace()
    {
        var handler = new FixtureHandler(_ => (HttpStatusCode.OK, ""));
        var r = MakeResolver(handler);

        Assert.Null(await r.ResolveAsync(""));
        Assert.Null(await r.ResolveAsync("   "));
    }

    [Fact]
    public async Task Returns_null_on_http_error()
    {
        var handler = new FixtureHandler(_ => (HttpStatusCode.InternalServerError, ""));
        var r = MakeResolver(handler);

        var result = await r.ResolveAsync("gabe");
        Assert.Null(result);
    }
}
