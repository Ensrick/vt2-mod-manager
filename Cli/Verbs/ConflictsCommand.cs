using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class ConflictsCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        var asJson = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));

        var settings = settingsStore.Load();
        var locator = new UserSettingsConfigLocator(settings);
        var path = locator.Resolve();
        if (!File.Exists(path)) { Console.Error.WriteLine($"user_settings.config not found at {path}."); return 3; }
        var block = new UserSettingsConfigReader().ReadFile(path);

        var pathResolver = new SteamPathResolver(settings);
        if (!pathResolver.TryResolve(out var steamRoot))
        {
            Console.Error.WriteLine("Could not locate Steam install.");
            return 3;
        }
        var libs = new LibraryFoldersResolver().Resolve(steamRoot);
        string? workshopRoot = null;
        foreach (var lib in libs)
        {
            var candidate = Path.Combine(lib, "steamapps", "workshop", "content", "552500");
            if (Directory.Exists(candidate)) { workshopRoot = candidate; break; }
        }
        if (workshopRoot is null)
        {
            Console.Error.WriteLine("No VT2 Workshop content folder found.");
            return 3;
        }

        var cache = new ModSourceCache(workshopRoot);
        var detector = new ConflictDetector(cache);
        var findings = detector.Detect(block);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (findings.Count == 0)
        {
            Console.WriteLine("No Tier-1 conflicts detected among enabled mods.");
            return 0;
        }

        Console.WriteLine($"{"Severity",-10} {"Kind",-14} {"Key",-40} Mods");
        Console.WriteLine(new string('-', 100));
        var nameById = block.Entries.ToDictionary(e => e.Id, e => e.Name);
        foreach (var c in findings)
        {
            var mods = string.Join(", ", c.ModIds.Select(id => nameById.TryGetValue(id, out var n) ? n : id));
            Console.WriteLine($"{c.Severity,-10} {c.Kind,-14} {Truncate(c.Key, 40),-40} {mods}");
        }
        Console.WriteLine();
        Console.WriteLine($"Total: {findings.Count} finding(s).");
        return 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
