using System;
using System.Collections.Generic;
using System.Linq;
using Vt2ModManager.Cli.Verbs;
using Vt2ModManager.Services;

namespace Vt2ModManager.Cli;

/// <summary>
/// Verb dispatcher. Exit codes: 0 success, 1 runtime error, 2 bad usage, 3 preflight failed.
/// </summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        var (cleaned, flags) = ExtractGlobalFlags(args);
        var jsonRequested = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        if (!flags.NoBanner && !jsonRequested) PrintBanner();

        if (cleaned.Count == 0)
        {
            Console.Error.WriteLine("vt2-mod-manager: missing verb. Try `vt2-mod-manager help`.");
            return 2;
        }

        var verb = cleaned[0].ToLowerInvariant();
        var rest = cleaned.Skip(1).ToArray();

        return verb switch
        {
            "help" or "--help" or "-h" => HelpCommand.Run(rest),
            "list"       => ListCommand.Run(rest, ResolveSettingsStore(flags)),
            "activate"   => ActivateCommand.Run(rest, ResolveSettingsStore(flags), enable: true),
            "deactivate" => ActivateCommand.Run(rest, ResolveSettingsStore(flags), enable: false),
            "profile"    => ProfileCommand.Run(rest, ResolveSettingsStore(flags)),
            "conflicts"  => ConflictsCommand.Run(rest, ResolveSettingsStore(flags)),
            "sort"       => SortCommand.Run(rest, ResolveSettingsStore(flags)),
            "collection" => CollectionCommand.Run(rest, ResolveSettingsStore(flags)),
            "launch"     => LaunchCommand.Run(rest, ResolveSettingsStore(flags)),
            "selfupdate" => SelfUpdateCommand.Run(rest),
            _ => UnknownVerb(verb),
        };
    }

    private static SettingsStore ResolveSettingsStore(GlobalFlags flags) => new(flags.ConfigPath);

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"vt2-mod-manager: unknown verb '{verb}'. Try `vt2-mod-manager help`.");
        return 2;
    }

    private static void PrintBanner() =>
        Console.WriteLine($"vt2-mod-manager {Program.Version} (headless)");

    public sealed record GlobalFlags(bool NoBanner, string? ConfigPath);

    public static (List<string> rest, GlobalFlags flags) ExtractGlobalFlags(string[] args)
    {
        var rest = new List<string>(args.Length);
        var noBanner = false;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--no-banner", StringComparison.OrdinalIgnoreCase)) { noBanner = true; continue; }
            if (string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[++i];
                continue;
            }
            rest.Add(a);
        }

        return (rest, new GlobalFlags(noBanner, configPath));
    }
}
