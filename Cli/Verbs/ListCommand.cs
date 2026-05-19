using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class ListCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        var asJson = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        var settings = settingsStore.Load();
        var locator = new UserSettingsConfigLocator(settings);
        var path = locator.Resolve();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"list: user_settings.config not found at {path}.");
            return 3;
        }

        var block = new UserSettingsConfigReader().ReadFile(path);

        if (asJson)
        {
            var dto = block.Entries.Select((e, i) => new
            {
                order = i + 1,
                id = e.Id,
                name = e.Name,
                author = e.Author,
                enabled = e.Enabled,
                sanctioned = e.Sanctioned,
                out_of_date = e.OutOfDate,
                last_updated = e.LastUpdated,
                num_children = e.NumChildren,
            }).ToList();
            Console.WriteLine(JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"{"#",-4}  {"On",-3}  {"ID",-12}  {"Name",-50}  {"Author",-20}  Status");
        Console.WriteLine(new string('-', 110));
        for (int i = 0; i < block.Entries.Count; i++)
        {
            var e = block.Entries[i];
            var on = e.Enabled ? "✔" : "·";
            var status = e.Sanctioned ? "sanctioned" : (e.OutOfDate ? "out-of-date" : "modded");
            Console.WriteLine($"{i + 1,-4}  {on,-3}  {Truncate(e.Id, 12),-12}  {Truncate(e.Name, 50),-50}  {Truncate(e.Author, 20),-20}  {status}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total: {block.Entries.Count} mods ({block.Entries.Count(x => x.Enabled)} enabled).");
        return 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
