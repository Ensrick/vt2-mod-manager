using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

/// <summary>
/// Top-level orchestrator for the Friends feature. Composes <see cref="SteamIdResolver"/>,
/// <see cref="SteamFriendScraper"/>, and <see cref="FriendsStore"/>.
///
/// Mod-list results live in <see cref="SessionCache"/> only — never persisted, because Steam's
/// HTML is the source of truth and friends' subscriptions change frequently.
/// </summary>
public sealed class FriendsService
{
    private readonly FriendsStore _store;
    private readonly SteamIdResolver _resolver;
    private readonly SteamFriendScraper _scraper;
    private readonly ConcurrentDictionary<string, FriendSubscriptionResult> _sessionCache = new(StringComparer.Ordinal);

    public FriendsService(HttpClient http, FriendsStore? store = null)
    {
        _store = store ?? new FriendsStore();
        _resolver = new SteamIdResolver(http);
        _scraper = new SteamFriendScraper(http);
    }

    public IReadOnlyDictionary<string, FriendSubscriptionResult> SessionCache => _sessionCache;

    public List<Friend> List() => _store.Load();

    /// <summary>Resolve input → SteamID64, refresh once, and persist. Returns null if unresolvable.</summary>
    public async Task<Friend?> AddAsync(string input, CancellationToken ct = default)
    {
        var sid = await _resolver.ResolveAsync(input, ct).ConfigureAwait(false);
        if (sid is null) return null;

        var friends = _store.Load();
        var existing = friends.FirstOrDefault(f => f.SteamId64 == sid);
        if (existing is not null) return existing;

        var fetch = await _scraper.FetchAsync(sid, ct: ct).ConfigureAwait(false);
        _sessionCache[sid] = fetch;

        var friend = new Friend
        {
            SteamId64 = sid,
            DisplayName = sid, // UI may update this from a separate lookup; default to the id.
            Favorite = false,
            AddedUtc = DateTime.UtcNow,
            LastFetchedUtc = fetch.Visibility == ProfileVisibility.Public ? DateTime.UtcNow : null,
        };

        friends.Add(friend);
        _store.Save(friends);
        return friend;
    }

    public void Remove(string steamId64)
    {
        var friends = _store.Load();
        var idx = friends.FindIndex(f => f.SteamId64 == steamId64);
        if (idx < 0) return;
        friends.RemoveAt(idx);
        _store.Save(friends);
        _sessionCache.TryRemove(steamId64, out _);
    }

    public void ToggleFavorite(string steamId64)
    {
        var friends = _store.Load();
        var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
        if (f is null) return;
        f.Favorite = !f.Favorite;
        _store.Save(friends);
    }

    /// <summary>Re-scrape the friend's subscriptions, refresh <see cref="SessionCache"/>, and stamp LastFetchedUtc.</summary>
    public async Task<FriendSubscriptionResult> RefreshAsync(string steamId64, CancellationToken ct = default)
    {
        var fetch = await _scraper.FetchAsync(steamId64, ct: ct).ConfigureAwait(false);
        _sessionCache[steamId64] = fetch;

        if (fetch.Visibility == ProfileVisibility.Public)
        {
            var friends = _store.Load();
            var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
            if (f is not null)
            {
                f.LastFetchedUtc = DateTime.UtcNow;
                _store.Save(friends);
            }
        }
        return fetch;
    }
}
