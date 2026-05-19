using System;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class SortCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));

        var settings = settingsStore.Load();
        var locator = new UserSettingsConfigLocator(settings);
        var path = locator.Resolve();
        if (!File.Exists(path)) { Console.Error.WriteLine($"user_settings.config not found at {path}."); return 3; }

        var block = new UserSettingsConfigReader().ReadFile(path);
        var beforeIds = block.Entries.Select(e => e.Id).ToList();

        var rules = LoadOrderRules.LoadOrCreate();
        var result = new LoadOrderSorter().Sort(block.Entries, rules);

        // Apply to the block.
        block.Entries.Clear();
        block.Entries.AddRange(result.Sorted);

        // Report changes.
        var afterIds = block.Entries.Select(e => e.Id).ToList();
        var moved = 0;
        for (int i = 0; i < afterIds.Count; i++) if (beforeIds.IndexOf(afterIds[i]) != i) moved++;

        Console.WriteLine($"Sorted {block.Entries.Count} mods: {moved} moved.");
        if (result.UnknownPinIds.Count > 0)
        {
            Console.WriteLine($"  {result.UnknownPinIds.Count} pin(s) not installed (rules file references missing mods): {string.Join(", ", result.UnknownPinIds)}");
        }
        if (result.CycleMemberIds.Count > 0)
        {
            Console.WriteLine($"  WARNING: dependency cycle among {result.CycleMemberIds.Count} mod(s): {string.Join(", ", result.CycleMemberIds)}");
            Console.WriteLine("  Kept in current order. Edit children[] in the .mod files or the rules file to break the cycle.");
        }

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("New order:");
            for (int i = 0; i < block.Entries.Count; i++)
                Console.WriteLine($"  {i + 1,3}  {block.Entries[i].Id,-12}  {block.Entries[i].Name}");
            Console.WriteLine();
            Console.WriteLine("--dry-run: not saved.");
            return 0;
        }

        new UserSettingsConfigWriter().WriteFile(path, block);
        Console.WriteLine($"Saved. Backup at {path}.bak.");
        return 0;
    }
}
