using System;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli.Verbs;

public static class LaunchCommand
{
    public static int Run(string[] args, SettingsStore settingsStore)
    {
        var viaSteam = args.Any(a => string.Equals(a, "--via-steam", StringComparison.OrdinalIgnoreCase));
        var officialRealm = args.Any(a => string.Equals(a, "--official-realm", StringComparison.OrdinalIgnoreCase));
        string? extraArgs = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--args", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                extraArgs = args[++i];
        }

        var launcher = new GameLauncher();

        if (viaSteam)
        {
            var r = launcher.LaunchViaSteam();
            Console.WriteLine(r.Message);
            return r.Started ? 0 : 1;
        }

        var settings = settingsStore.Load();
        var resolver = new SteamPathResolver(settings);
        if (!resolver.TryResolve(out var steamRoot))
        {
            Console.Error.WriteLine("Could not locate Steam install.");
            return 3;
        }
        var libs = new LibraryFoldersResolver().Resolve(steamRoot);
        var install = Vt2Installation.Resolve(steamRoot, libs);
        if (install is null)
        {
            Console.Error.WriteLine("VT2 install not found via appmanifest_552500.acf in any library.");
            return 3;
        }

        var result = launcher.LaunchDirect(install, moddedRealm: !officialRealm, extraArgs: extraArgs);
        Console.WriteLine(result.Message);
        return result.Started ? 0 : 1;
    }
}
