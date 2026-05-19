using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Pulls the running user's Workshop subscriptions for a given app out of Steam's local
/// userdata: <c>&lt;SteamRoot&gt;/userdata/&lt;accountid32&gt;/ugc/&lt;appid&gt;_subscriptions.vdf</c>.
///
/// This is the most reliable hermetic source we have. Crucially, it lists EVERY subscription
/// — including items Steam hasn't finished downloading yet, which <c>appworkshop_&lt;appid&gt;.acf</c>
/// (the on-disk install manifest) does not. Cross-referencing both lets the UI surface
/// "subscribed but pending download" instead of silently dropping those rows as phantoms.
///
/// Shape of the file (top-level wrapper is "subscribedfiles"; each entry is keyed by a
/// numeric ordinal and carries the actual <c>publishedfileid</c> as a nested scalar):
/// <code>
/// "subscribedfiles" {
///     "appid" "552500"
///     "time_last_updated" "1779220710"
///     "0" { "publishedfileid" "2449540920" "time_subscribed" "1779171563" "disabled_locally" "0" }
///     "1" { "publishedfileid" "2490558925" ... }
///     ...
/// }
/// </code>
///
/// Why not <c>localconfig.vdf</c>: subscriptions are NOT stored there. The Steam friends/
/// chat state lives in <c>localconfig.vdf</c> (see <see cref="SteamFriendsResolver"/>), but
/// Workshop subscription state is in a separate per-app vdf under <c>ugc/</c>.
///
/// Why not profile-page scraping: requires the user's profile to be Public (Friends-only
/// fails without scraper cookies, which not every user provides). Local file works always
/// as long as Steam has run at least once with this account signed in.
///
/// Why not Steam Web API <c>IPublishedFileService/GetUserFiles</c>: requires an API key.
/// </summary>
public sealed class SteamSubscriptionsResolver
{
    private readonly string _steamRoot;

    public SteamSubscriptionsResolver(string steamRoot) { _steamRoot = steamRoot; }

    /// <summary>
    /// Aggregate every subscribed publishedfileid across every account dir under
    /// <c>userdata/</c>. Returns an empty list if Steam isn't installed, the user has
    /// never opened the Workshop for this app, or the file is malformed — callers
    /// should treat empty as "could not determine" and skip filtering, not as
    /// "user has zero subscriptions".
    /// </summary>
    public IReadOnlyList<string> ResolveSubscribedIds(uint appId)
    {
        var userdata = Path.Combine(_steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return Array.Empty<string>();

        var accountDirs = Directory.GetDirectories(userdata)
            .Where(d => uint.TryParse(Path.GetFileName(d), out _))
            // Newest-first heuristic — matches SteamFriendsResolver so we hit the
            // currently-active account first if multiple are present.
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .ToList();

        var combined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in accountDirs)
        {
            var path = Path.Combine(dir, "ugc", $"{appId}_subscriptions.vdf");
            if (!File.Exists(path)) continue;
            foreach (var id in ParseSubscriptionsFile(path))
                combined.Add(id);
        }

        return combined.ToList();
    }

    /// <summary>
    /// Pure parser, exposed for tests. Reads one <c>&lt;appid&gt;_subscriptions.vdf</c>
    /// file and yields each <c>publishedfileid</c> string. Skips entries with
    /// <c>disabled_locally != "0"</c> — the user explicitly toggled them off in Steam,
    /// so they're effectively unsubscribed for our purposes.
    /// </summary>
    public static IEnumerable<string> ParseSubscriptionsFile(string path)
    {
        AcfNode root;
        try { root = AcfNode.ParseFile(path); }
        catch { yield break; }

        // The outer parse drops the "subscribedfiles" wrapper key (ACF tradition) and
        // returns the inner object directly. So children of root are the ordinals,
        // appid, time_last_updated, etc.
        if (!root.IsObject) yield break;

        foreach (var (key, body) in root.Children)
        {
            if (!body.IsObject) continue;             // skip "appid" / "time_last_updated" scalars
            if (!int.TryParse(key, out _)) continue;  // ordinal keys only

            var pubFileId = body["publishedfileid"]?.AsString();
            if (string.IsNullOrEmpty(pubFileId)) continue;

            // Steam stores user-disabled subs in the same file with a flag; treat those
            // as "not subscribed" so the manager doesn't surface them as pending downloads.
            var disabled = body["disabled_locally"]?.AsString();
            if (!string.IsNullOrEmpty(disabled) && disabled != "0") continue;

            yield return pubFileId;
        }
    }
}
