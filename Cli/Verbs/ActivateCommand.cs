using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class ActivateCommand
{
    public static int Run(string[] args, SettingsStore settingsStore, bool enable)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine($"{(enable ? "activate" : "deactivate")}: pass one or more mod IDs or substrings of mod names.");
            return 2;
        }

        var settings = settingsStore.Load();
        var locator = new UserSettingsConfigLocator(settings);
        var path = locator.Resolve();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"user_settings.config not found at {path}.");
            return 3;
        }

        var reader = new UserSettingsConfigReader();
        var block = reader.ReadFile(path);

        var changed = new List<ModEntry>();
        foreach (var query in args)
        {
            var matches = ResolveMatches(block, query).ToList();
            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"No mod matches '{query}'.");
                continue;
            }
            foreach (var m in matches)
            {
                if (m.Enabled != enable)
                {
                    m.Enabled = enable;
                    changed.Add(m);
                }
            }
        }

        if (changed.Count == 0)
        {
            Console.WriteLine("No changes.");
            return 0;
        }

        new UserSettingsConfigWriter().WriteFile(path, block);
        Console.WriteLine($"{(enable ? "Activated" : "Deactivated")} {changed.Count} mod(s):");
        foreach (var m in changed) Console.WriteLine($"  - {m.Name} ({m.Id})");
        return 0;
    }

    internal static IEnumerable<ModEntry> ResolveMatches(ModListBlock block, string query)
    {
        // Exact ID first.
        var byId = block.Entries.Where(e => e.Id == query).ToList();
        if (byId.Count > 0) return byId;

        // Case-insensitive substring on name.
        return block.Entries.Where(e =>
            e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
