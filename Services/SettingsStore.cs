using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vt2ModManager.Services;

/// <summary>
/// Persistent settings stored at %APPDATA%\Vt2ModManager\settings.json.
/// Unknown JSON properties are ignored for forward-compat.
/// </summary>
public sealed class Settings
{
    [JsonPropertyName("steam_path_override")]
    public string? SteamPathOverride { get; set; }

    [JsonPropertyName("user_settings_config_path_override")]
    public string? UserSettingsConfigPathOverride { get; set; }

    [JsonPropertyName("last_profile")]
    public string? LastProfile { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }

    public SettingsStore(string? overridePath = null)
    {
        Path = overridePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "Vt2ModManager", "settings.json");
    }

    public Settings Load()
    {
        if (!File.Exists(Path)) return new Settings();

        try
        {
            var text = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<Settings>(text, JsonOpts) ?? new Settings();
        }
        catch (JsonException)
        {
            TryQuarantine();
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
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
