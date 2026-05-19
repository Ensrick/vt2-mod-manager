using System;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class CollectionCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        if (args.Length == 0 || !string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("collection: subcommand 'export' required.");
            return 2;
        }

        var enabledOnly = args.Any(a => string.Equals(a, "--enabled-only", StringComparison.OrdinalIgnoreCase));
        string? outPath = null;
        var format = "json";
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                outPath = args[++i];
            else if (string.Equals(args[i], "--format", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                format = args[++i].ToLowerInvariant();
        }

        var settings = settingsStore.Load();
        var locator = new UserSettingsConfigLocator(settings);
        var path = locator.Resolve();
        if (!File.Exists(path)) { Console.Error.WriteLine($"user_settings.config not found at {path}."); return 3; }

        var block = new UserSettingsConfigReader().ReadFile(path);
        var exporter = new CollectionExporter();
        var export = exporter.Build(block.Entries, enabledOnly);

        string payload = format switch
        {
            "urls"   => exporter.ToUrlList(export),
            "tabbed" => exporter.ToTabbedList(export),
            _        => exporter.ToJson(export),
        };

        if (outPath is null)
        {
            Console.WriteLine(payload);
        }
        else
        {
            File.WriteAllText(outPath, payload);
            Console.WriteLine($"Wrote {export.Count} mod(s) to {outPath}.");
        }
        return 0;
    }
}
