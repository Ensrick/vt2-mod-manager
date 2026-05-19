using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vt2ModManager.Services;

public enum ConflictSeverity { Low, Medium, High, Crash }

/// <summary>
/// One detected conflict. <see cref="ModIds"/> lists every enabled mod that contributed to the
/// collision; <see cref="Key"/> is the colliding identifier (e.g. <c>"BuffUI._align_widgets"</c>).
/// </summary>
public sealed record Conflict(
    string Kind,
    string Key,
    ConflictSeverity Severity,
    IReadOnlyList<string> ModIds,
    string Detail);

/// <summary>
/// Tier-1 static-grep conflict scanner. Detects:
///   1. <c>mod:hook_origin(Class, "method")</c> collisions — second writer silently shadows the first
///      (CRASH-LEVEL impact: the loser's behaviour just vanishes).
///   2. VMF <c>setting_id = "..."</c> collisions on keybind widgets — broken settings UI (medium).
///   3. <c>BuffTemplates.foo</c> / <c>BuffTemplates["foo"]</c> assignment collisions — network desync (high).
///
/// All three rules are intentionally line-oriented and skip <c>--</c>-prefixed comments.
/// </summary>
public sealed class ConflictDetector
{
    private readonly ModSourceCache _cache;

    private static readonly Regex HookOriginRx = new(
        @"mod:hook_origin\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*,\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex SettingIdRx = new(
        @"setting_id\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex KeybindWidgetRx = new(
        @"widget_type\s*=\s*""keybind""",
        RegexOptions.Compiled);

    private static readonly Regex BuffTemplateDotRx = new(
        @"BuffTemplates\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.Compiled);

    private static readonly Regex BuffTemplateIndexRx = new(
        @"BuffTemplates\s*\[\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\]\s*=",
        RegexOptions.Compiled);

    public ConflictDetector(ModSourceCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Scan every enabled mod in <paramref name="liveMods"/> and return the findings.</summary>
    public IReadOnlyList<Conflict> Detect(ModListBlock liveMods)
    {
        ArgumentNullException.ThrowIfNull(liveMods);

        // Per-key map from key → set of mod IDs that touch it.
        var hookOriginHits = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var keybindSettingHits = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var buffTemplateHits = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var mod in liveMods.Entries)
        {
            if (!mod.Enabled) continue;
            var modId = mod.Id;
            if (string.IsNullOrWhiteSpace(modId)) continue;

            foreach (var path in _cache.GetLuaFiles(modId))
            {
                var text = _cache.ReadFile(path);
                if (string.IsNullOrEmpty(text)) continue;

                var isDataFile = path.EndsWith("_data.lua", StringComparison.OrdinalIgnoreCase);

                ScanFile(text, modId, isDataFile,
                    hookOriginHits, keybindSettingHits, buffTemplateHits);
            }
        }

        var result = new List<Conflict>();

        foreach (var (key, mods) in hookOriginHits)
        {
            if (mods.Count < 2) continue;
            result.Add(new Conflict(
                Kind: "hook_origin",
                Key: key,
                Severity: ConflictSeverity.Crash,
                ModIds: mods.ToArray(),
                Detail: $"{mods.Count} mods call mod:hook_origin on {key}; only one wins, the rest are silently shadowed."));
        }

        foreach (var (key, mods) in keybindSettingHits)
        {
            if (mods.Count < 2) continue;
            result.Add(new Conflict(
                Kind: "setting_id",
                Key: key,
                Severity: ConflictSeverity.Medium,
                ModIds: mods.ToArray(),
                Detail: $"{mods.Count} mods declare a keybind widget with setting_id \"{key}\"; one or both bindings will fail to register."));
        }

        foreach (var (key, mods) in buffTemplateHits)
        {
            if (mods.Count < 2) continue;
            result.Add(new Conflict(
                Kind: "buff_template",
                Key: key,
                Severity: ConflictSeverity.High,
                ModIds: mods.ToArray(),
                Detail: $"{mods.Count} mods write BuffTemplates[\"{key}\"]; later writer overwrites earlier, peers with different load order desync on rpc_add_buff."));
        }

        return result
            .OrderByDescending(c => c.Severity)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ScanFile(
        string text,
        string modId,
        bool isDataFile,
        Dictionary<string, SortedSet<string>> hookOriginHits,
        Dictionary<string, SortedSet<string>> keybindSettingHits,
        Dictionary<string, SortedSet<string>> buffTemplateHits)
    {
        // We split on raw lines so we can cheaply drop full-line comments. We don't try to track
        // block comments (--[[ ... ]]) — they're rare in mod source and a false positive here is
        // far cheaper than a false negative (it just nags the user about a string that isn't live).
        var lines = text.Split('\n');

        // Pre-find every line index that contains a keybind widget marker so the setting_id check
        // can demand "within 10 lines of such a marker". This is the conservative scoping the spec
        // calls for; an entire data file usually contains <100 lines so the cost is negligible.
        List<int>? keybindLines = null;
        if (isDataFile)
        {
            keybindLines = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (KeybindWidgetRx.IsMatch(lines[i]))
                    keybindLines.Add(i);
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsCommentLine(line)) continue;

            // Rule 1 — hook_origin
            foreach (Match m in HookOriginRx.Matches(line))
            {
                var key = $"{m.Groups[1].Value}.{m.Groups[2].Value}";
                AddHit(hookOriginHits, key, modId);
            }

            // Rule 2 — setting_id (only when a keybind widget lives nearby).
            if (isDataFile && keybindLines is { Count: > 0 })
            {
                foreach (Match m in SettingIdRx.Matches(line))
                {
                    var key = m.Groups[1].Value;
                    if (IsNoisyKey(key)) continue;
                    if (!HasNeighbour(keybindLines, i, 10)) continue;
                    AddHit(keybindSettingHits, key, modId);
                }
            }

            // Rule 3 — BuffTemplates.<key> = / BuffTemplates["<key>"] =
            foreach (Match m in BuffTemplateDotRx.Matches(line))
                AddHit(buffTemplateHits, m.Groups[1].Value, modId);
            foreach (Match m in BuffTemplateIndexRx.Matches(line))
                AddHit(buffTemplateHits, m.Groups[1].Value, modId);
        }
    }

    private static void AddHit(Dictionary<string, SortedSet<string>> map, string key, string modId)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new SortedSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }
        set.Add(modId);
    }

    private static bool IsCommentLine(string line)
    {
        // A line counts as a comment if the first non-whitespace characters are `--`. We don't try
        // to detect trailing inline comments — the regex captures take precedence and a hit inside
        // an inline `-- ...` tail is rare enough to leave to a later pass.
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t' || line[i] == '\r')) i++;
        return i + 1 < line.Length && line[i] == '-' && line[i + 1] == '-';
    }

    private static bool IsNoisyKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return true;
        // Pure-numeric setting_ids ("0", "1", "42") are placeholder/index strings, never real
        // binding actions.
        foreach (var c in key) if (c < '0' || c > '9') return false;
        return true;
    }

    private static bool HasNeighbour(List<int> sortedLines, int target, int window)
    {
        // Binary search would be tidier but the lists are tiny; a linear sweep is plenty.
        for (int j = 0; j < sortedLines.Count; j++)
        {
            int diff = sortedLines[j] - target;
            if (diff >= -window && diff <= window) return true;
            if (diff > window) break;
        }
        return false;
    }
}
