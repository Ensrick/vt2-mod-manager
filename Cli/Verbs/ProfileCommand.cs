using System;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class ProfileCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("profile: subcommand required (save|list|apply|delete).");
            return 2;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        var settings = settingsStore.Load();
        var store = new ProfileStore();
        var locator = new UserSettingsConfigLocator(settings);

        switch (sub)
        {
            case "list":
                foreach (var n in store.List()) Console.WriteLine(n);
                return 0;

            case "save":
                if (rest.Length == 0) { Console.Error.WriteLine("profile save: name required."); return 2; }
                {
                    var path = locator.Resolve();
                    if (!File.Exists(path)) { Console.Error.WriteLine("user_settings.config not found."); return 3; }
                    var block = new UserSettingsConfigReader().ReadFile(path);
                    var profile = ProfileStore.Capture(rest[0], block);
                    store.Save(profile);
                    Console.WriteLine($"Saved profile '{profile.Name}' ({profile.Entries.Count} mods).");
                    return 0;
                }

            case "apply":
                if (rest.Length == 0) { Console.Error.WriteLine("profile apply: name required."); return 2; }
                {
                    var profile = store.Load(rest[0]);
                    if (profile is null) { Console.Error.WriteLine($"No profile '{rest[0]}'."); return 3; }
                    var path = locator.Resolve();
                    if (!File.Exists(path)) { Console.Error.WriteLine("user_settings.config not found."); return 3; }
                    var block = new UserSettingsConfigReader().ReadFile(path);
                    var result = ProfileStore.Apply(profile, block);
                    new UserSettingsConfigWriter().WriteFile(path, block);
                    Console.WriteLine($"Applied '{profile.Name}': {result.ToggledCount} toggled, {result.Missing.Count} missing, {result.Extras.Count} extras appended.");
                    foreach (var m in result.Missing) Console.WriteLine($"  missing: {m.Name} ({m.Id})");
                    return 0;
                }

            case "delete":
                if (rest.Length == 0) { Console.Error.WriteLine("profile delete: name required."); return 2; }
                {
                    var ok = store.Delete(rest[0]);
                    Console.WriteLine(ok ? $"Deleted '{rest[0]}'." : $"No profile '{rest[0]}'.");
                    return ok ? 0 : 3;
                }

            default:
                Console.Error.WriteLine($"profile: unknown subcommand '{sub}'.");
                return 2;
        }
    }
}
