using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vt2ModManager.Services;

/// <summary>
/// Snapshot of a mod-list state — what's installed, which are enabled, and their order.
/// Stored as JSON at %APPDATA%\Vt2ModManager\profiles\&lt;name&gt;.json.
///
/// On Apply, the profile is overlaid onto the live mod list: known mods are reordered and
/// toggled; mods present in the profile but not currently installed are skipped (we never
/// fabricate mod entries — those have to come from a Workshop subscription).
/// </summary>
public sealed class Profile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("entries")]
    public List<ProfileEntry> Entries { get; set; } = new();
}

public sealed class ProfileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; }

    public ProfileStore(string? overrideDir = null)
    {
        Directory = overrideDir ?? DefaultDirectory();
    }

    public static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Vt2ModManager", "profiles");
    }

    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<string>();
        return System.IO.Directory
            .EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Profile? Load(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Profile>(text, JsonOpts);
    }

    public void Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(profile.Name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string name) => Path.Combine(Directory, SanitizeName(name) + ".json");

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>
    /// Capture the current mod list as a profile snapshot.
    /// </summary>
    public static Profile Capture(string name, ModListBlock block, string? description = null) =>
        new()
        {
            Name = name,
            Description = description,
            CreatedUtc = DateTime.UtcNow,
            Entries = block.Entries.Select(e => new ProfileEntry
            {
                Id = e.Id,
                Name = e.Name,
                Enabled = e.Enabled,
            }).ToList(),
        };

    /// <summary>
    /// Apply a profile to the given live mod list. Returns a result describing what changed,
    /// what was skipped, and what was retained.
    ///
    /// Rules:
    ///  - For each ID in the profile, find the live mod with that ID. If present, set its enabled
    ///    flag to the profile's value and reorder it to the profile's position.
    ///  - Live mods not mentioned in the profile retain their current enabled state and are
    ///    appended after all profile-known mods, preserving their original relative order.
    ///  - Profile entries whose ID isn't installed are reported as "missing" and ignored.
    /// </summary>
    public static ApplyResult Apply(Profile profile, ModListBlock block)
    {
        var byId = block.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var ordered = new List<ModEntry>(block.Entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<ProfileEntry>();
        var toggled = 0;

        foreach (var pe in profile.Entries)
        {
            if (!byId.TryGetValue(pe.Id, out var entry))
            {
                missing.Add(pe);
                continue;
            }
            if (entry.Enabled != pe.Enabled) toggled++;
            entry.Enabled = pe.Enabled;
            ordered.Add(entry);
            seen.Add(pe.Id);
        }

        // Append leftover live mods (subscribed but unknown to the profile).
        var extras = new List<ModEntry>();
        foreach (var e in block.Entries)
        {
            if (!seen.Contains(e.Id))
            {
                ordered.Add(e);
                extras.Add(e);
            }
        }

        block.Entries.Clear();
        block.Entries.AddRange(ordered);

        return new ApplyResult(toggled, missing, extras);
    }

    public sealed record ApplyResult(int ToggledCount, List<ProfileEntry> Missing, List<ModEntry> Extras);
}
