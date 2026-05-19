using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vt2ModManager.Services;

/// <summary>
/// Produces a payload describing the current (or enabled) mod set in three shapes:
///  - JSON (for tooling / sharing)
///  - newline-separated Workshop URLs (paste-friendly for Discord / Steam Collection edit)
///  - newline-separated `&lt;id&gt;&lt;tab&gt;&lt;name&gt;` (greppable)
///
/// Steam doesn't expose a "subscribe to many IDs" URL scheme — see
/// `reference_steam_workshop_subscribe.md` in author memory. The working flow is to paste these
/// IDs into a Steam Workshop Collection's edit page, then share the Collection URL so others
/// can use "Subscribe to All".
/// </summary>
public sealed class CollectionExporter
{
    public const string ManageCollectionsUrl =
        "https://steamcommunity.com/sharedfiles/managecollections";

    public sealed record ExportEntry(
        [property: JsonPropertyName("id")]      string Id,
        [property: JsonPropertyName("name")]    string Name,
        [property: JsonPropertyName("author")]  string Author,
        [property: JsonPropertyName("enabled")] bool   Enabled,
        [property: JsonPropertyName("url")]     string Url);

    public sealed record Export(
        [property: JsonPropertyName("appid")]   uint AppId,
        [property: JsonPropertyName("count")]   int Count,
        [property: JsonPropertyName("entries")] List<ExportEntry> Entries);

    public Export Build(IEnumerable<ModEntry> entries, bool enabledOnly)
    {
        var list = entries
            .Where(e => !enabledOnly || e.Enabled)
            .Select(e => new ExportEntry(
                e.Id,
                e.Name,
                e.Author,
                e.Enabled,
                $"https://steamcommunity.com/sharedfiles/filedetails/?id={e.Id}"))
            .ToList();
        return new Export(552500, list.Count, list);
    }

    public string ToJson(Export export) =>
        JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });

    public string ToUrlList(Export export)
    {
        var sb = new StringBuilder();
        foreach (var e in export.Entries) sb.AppendLine(e.Url);
        return sb.ToString();
    }

    public string ToTabbedList(Export export)
    {
        var sb = new StringBuilder();
        foreach (var e in export.Entries)
        {
            sb.Append(e.Id);
            sb.Append('\t');
            sb.Append(e.Name);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void WriteJsonFile(Export export, string path) =>
        File.WriteAllText(path, ToJson(export));
}
