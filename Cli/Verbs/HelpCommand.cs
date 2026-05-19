using System;

namespace Vt2ModManager.Cli.Verbs;

public static class HelpCommand
{
    public static int Run(string[] _)
    {
        Console.WriteLine(@"vt2-mod-manager — Vermintide 2 mod load-order + profile manager

USAGE
  vt2-mod-manager                       Launch GUI
  vt2-mod-manager --gui                 Launch GUI explicitly
  vt2-mod-manager <verb> [flags]        Headless command

VERBS
  list [--json]                         List subscribed mods in load order
  activate <id_or_name> [<id_or_name>]  Enable mods by Workshop ID or substring of name
  deactivate <id_or_name> ...           Disable mods by Workshop ID or substring of name
  profile save <name>                   Snapshot the current mod list as a profile
  profile list                          List saved profiles
  profile apply <name>                  Overlay a profile onto the live mod list and save
  profile delete <name>                 Delete a profile
  conflicts [--json]                    Static-scan enabled mods for hook/setting collisions
  sort [--dry-run]                      Topologically sort mods (VMF first, then dep order)
  collection export [--enabled-only]
       [--format json|urls|tabbed]
       [--out <path>]                   Export mod list for sharing / Steam Collection paste
  launch [--via-steam] [--official-realm]
         [--args ""...""]               Start VT2 directly. Default is modded realm.
                                        --via-steam: use steam:// (Fatshark launcher will appear).
                                        --official-realm: drop the -eac-untrusted flag.
  selfupdate [--yes] [--json]           Check GitHub for a newer build; --yes installs it.
                                        Exit 10 = update available (when --yes omitted).

GLOBAL FLAGS
  --no-banner                           Suppress the leading version banner
  --config <path>                       Read settings from a specific JSON file");
        return 0;
    }
}
