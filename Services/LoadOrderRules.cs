using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vt2ModManager.Services;

/// <summary>
/// Curated overrides layered on top of mod-declared `children[]` dependencies.
/// Stored at %APPDATA%\Vt2ModManager\load-order-rules.json — created with sensible
/// defaults on first run if missing.
/// </summary>
public sealed class LoadOrderRules
{
    /// <summary>Workshop IDs forced to the top of the list, in declared order.</summary>
    [JsonPropertyName("pin_top_ids")]
    public List<string> PinTopIds { get; set; } = new();

    /// <summary>Workshop IDs forced to the bottom, in declared order.</summary>
    [JsonPropertyName("pin_bottom_ids")]
    public List<string> PinBottomIds { get; set; } = new();

    /// <summary>
    /// Extra dependency edges. <c>ChildId</c> must load before <c>ParentId</c>. Useful when a
    /// mod author forgot to declare a library in its `.mod` children list.
    /// </summary>
    [JsonPropertyName("extra_dependencies")]
    public List<DependencyRule> ExtraDependencies { get; set; } = new();

    /// <summary>Reasonable starting rules — VMF pinned to top is the only universal truth.</summary>
    public static LoadOrderRules Default() => new()
    {
        PinTopIds = new() { "1369573612" }, // Vermintide Mod Framework
        PinBottomIds = new(),
        ExtraDependencies = new(),
    };

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Vt2ModManager", "load-order-rules.json");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LoadOrderRules LoadOrCreate(string? overridePath = null)
    {
        var path = overridePath ?? DefaultPath();
        if (!File.Exists(path))
        {
            var rules = Default();
            Save(rules, path);
            return rules;
        }
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LoadOrderRules>(text, JsonOpts) ?? Default();
        }
        catch (JsonException)
        {
            // Corrupt file — return defaults; don't clobber, user can hand-fix.
            return Default();
        }
    }

    public static void Save(LoadOrderRules rules, string? overridePath = null)
    {
        var path = overridePath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(rules, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class DependencyRule
{
    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = "";

    [JsonPropertyName("child_id")]
    public string ChildId { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
