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
    private readonly SteamProfileXmlClient _xmlClient;
    private readonly ConcurrentDictionary<string, FriendSubscriptionResult> _sessionCache = new(StringComparer.Ordinal);

    public FriendsService(HttpClient http, FriendsStore? store = null)
    {
        _store = store ?? new FriendsStore();
        _resolver = new SteamIdResolver(http);
        _scraper = new SteamFriendScraper(http);
        _xmlClient = new SteamProfileXmlClient(http);
    }

    /// <summary>
    /// Auto-populate friends.json from Steam's local userdata. Newly-discovered friends are
    /// added with <c>Origin="steam"</c> and <c>Favorite=false</c> — the user opts them in to
    /// the Mods-tab columns via the "Show" checkbox in the friends window. Existing entries
    /// are updated with the canonical persona name from Steam but otherwise left alone.
    /// </summary>
    public int SyncSteamRoster(string steamRoot)
    {
        IReadOnlyList<SteamFriendListing> listings;
        try { listings = new SteamFriendsResolver(steamRoot).Resolve(); }
        catch { return 0; }

        if (listings.Count == 0) return 0;

        var friends = _store.Load();
        var bySid = friends.ToDictionary(f => f.SteamId64, StringComparer.Ordinal);
        var added = 0;

        foreach (var l in listings)
        {
            if (bySid.TryGetValue(l.SteamId64, out var existing))
            {
                // Refresh the persona name (Steam is authoritative) but never alter favorite/origin.
                if (!string.IsNullOrEmpty(l.PersonaName) && existing.DisplayName != l.PersonaName)
                    existing.DisplayName = l.PersonaName;
                continue;
            }
            friends.Add(new Friend
            {
                SteamId64 = l.SteamId64,
                DisplayName = l.PersonaName,
                Favorite = false,
                Origin = "steam",
                AddedUtc = DateTime.UtcNow,
            });
            added++;
        }

        _store.Save(friends);
        return added;
    }

    /// <summary>
    /// XML-pre-flight one friend's profile to populate visibility + canonical display name
    /// without scraping the heavier subscriptions page. Persisted to friends.json on success.
    /// </summary>
    public async Task<SteamProfileXmlResult?> ProbeProfileAsync(string steamId64, CancellationToken ct = default)
    {
        var result = await _xmlClient.FetchAsync(steamId64, ct).ConfigureAwait(false);
        if (result is null) return null;

        var friends = _store.Load();
        var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
        if (f is not null && !string.IsNullOrEmpty(result.DisplayName) && f.DisplayName != result.DisplayName)
        {
            f.DisplayName = result.DisplayName;
            _store.Save(friends);
        }
        return result;
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

    /// <summary>Persist a specific friend's <see cref="Friend.Favorite"/>. Used by the Friends
    /// window's "Show" checkbox column to make changes durable as soon as the user toggles.</summary>
    public void SetFavorite(string steamId64, bool favorite)
    {
        var friends = _store.Load();
        var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
        if (f is null || f.Favorite == favorite) return;
        f.Favorite = favorite;
        _store.Save(friends);
    }

    /// <summary>Re-scrape the friend's subscriptions, refresh <see cref="SessionCache"/>, and stamp LastFetchedUtc.
    /// XML-pre-flights the profile first to avoid scraping pages that will never expose subs
    /// (private/friends-only). Updates the persisted display name from the XML when present.</summary>
    public async Task<FriendSubscriptionResult> RefreshAsync(string steamId64, CancellationToken ct = default)
    {
        var probe = await _xmlClient.FetchAsync(steamId64, ct).ConfigureAwait(false);
        if (probe is not null && probe.Visibility != ProfileVisibility.Public)
        {
            // Skip the scrape; record visibility + empty mod list in the cache.
            var skipped = new FriendSubscriptionResult(
                SteamId64: steamId64,
                Visibility: probe.Visibility,
                Mods: Array.Empty<FriendModListing>(),
                PagesFetched: 0,
                Error: null);
            _sessionCache[steamId64] = skipped;
            UpdateDisplayName(steamId64, probe.DisplayName);
            return skipped;
        }

        var fetch = await _scraper.FetchAsync(steamId64, ct: ct).ConfigureAwait(false);
        _sessionCache[steamId64] = fetch;

        if (fetch.Visibility == ProfileVisibility.Public)
        {
            var friends = _store.Load();
            var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
            if (f is not null)
            {
                f.LastFetchedUtc = DateTime.UtcNow;
                if (probe is not null && !string.IsNullOrEmpty(probe.DisplayName))
                    f.DisplayName = probe.DisplayName;
                _store.Save(friends);
            }
        }
        return fetch;
    }

    private void UpdateDisplayName(string steamId64, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var friends = _store.Load();
        var f = friends.FirstOrDefault(x => x.SteamId64 == steamId64);
        if (f is null || f.DisplayName == name) return;
        f.DisplayName = name;
        _store.Save(friends);
    }
}
