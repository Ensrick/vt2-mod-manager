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

    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    [JsonPropertyName("added_utc")]
    public DateTime AddedUtc { get; set; }

    [JsonPropertyName("last_fetched_utc")]
    public DateTime? LastFetchedUtc { get; set; }
}

public sealed class FriendsStore
{
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

    /// <summary>Load the friends list; returns empty on missing or corrupt file (quarantines the bad file).</summary>
    public List<Friend> Load()
    {
        if (!File.Exists(Path)) return new List<Friend>();

        try
        {
            var text = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<List<Friend>>(text, JsonOpts) ?? new List<Friend>();
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

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(friends, JsonOpts));
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
