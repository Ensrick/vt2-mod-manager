using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vt2ModManager.Services;

/// <summary>
/// A tracked friend whose subscribed-Workshop list we compare against our own.
/// Persisted to <c>%APPDATA%\Vt2ModManager\friends.json</c>; mod lists are never persisted
/// (they change too often — Steam's HTML is always the source of truth).
/// </summary>
public sealed class Friend
{
    [JsonPropertyName("steam_id64")]
    public string SteamId64 { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>True if this friend should appear as a column on the Mods grid. Manually-added
    /// friends default to true; auto-discovered Steam roster entries default to false (the user
    /// opts in via the "Show" checkbox in the friends window).</summary>
    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    [JsonPropertyName("added_utc")]
    public DateTime AddedUtc { get; set; }

    [JsonPropertyName("last_fetched_utc")]
    public DateTime? LastFetchedUtc { get; set; }

    /// <summary>How this friend ended up in friends.json — "manual" (user typed it) or
    /// "steam" (auto-loaded from localconfig.vdf). Used by the UI to badge each row.</summary>
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "manual";
}

/// <summary>Versioned wrapper for friends.json. Lets us add migration logic without breaking
/// older installs. Schema 2 introduced the <c>origin</c> field on <see cref="Friend"/> and the
/// "Favorite gates Mods-tab column visibility" semantic. Schema 1 is the flat array shape used
/// by v0.1.0 — v0.1.3.</summary>
internal sealed class FriendsFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("friends")]
    public List<Friend> Friends { get; set; } = new();
}

public sealed class FriendsStore
{
    public const int CurrentSchema = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }

    public FriendsStore(string? overridePath = null)
    {
        Path = overridePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "Vt2ModManager", "friends.json");
    }

    /// <summary>Load the friends list; returns empty on missing or corrupt file (quarantines the bad file).
    /// Migrates schema 1 → 2 in-place: every legacy entry is force-favorited so existing users keep
    /// the friend columns they had before column-gating was added.</summary>
    public List<Friend> Load()
    {
        if (!File.Exists(Path)) return new List<Friend>();

        string text;
        try { text = File.ReadAllText(Path); }
        catch { return new List<Friend>(); }

        // Try schema 2 (object wrapper) first; fall back to schema 1 (flat array) and migrate.
        try
        {
            var wrapped = JsonSerializer.Deserialize<FriendsFile>(text, JsonOpts);
            if (wrapped is { SchemaVersion: >= 1, Friends: not null })
                return wrapped.Friends;
        }
        catch (JsonException) { /* fall through to legacy shape */ }

        try
        {
            var legacy = JsonSerializer.Deserialize<List<Friend>>(text, JsonOpts) ?? new();
            // Schema 1 had no Favorite-gates-column semantic — every added friend WAS a column.
            // Preserve that intent by force-favoriting on migration.
            foreach (var f in legacy)
            {
                if (string.IsNullOrEmpty(f.Origin)) f.Origin = "manual";
                f.Favorite = true;
            }
            // Re-save in the new shape so we only run migration once.
            try { Save(legacy); } catch { /* best effort */ }
            return legacy;
        }
        catch (JsonException)
        {
            TryQuarantine();
            return new List<Friend>();
        }
    }

    public void Save(List<Friend> friends)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var wrapped = new FriendsFile { SchemaVersion = CurrentSchema, Friends = friends };
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(wrapped, JsonOpts));
        File.Move(tmp, Path, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(Path, $"{Path}.corrupt-{stamp}", overwrite: false);
        }
        catch { }
    }
}
