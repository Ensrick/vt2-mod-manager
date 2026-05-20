# vt2-mod-manager

Standalone load-order, profile, and friend-compare manager for Vermintide 2 mods. Bypasses the Fatshark launcher.

![](docs/screenshot.png)

Reads and rewrites `%AppData%\Fatshark\Vermintide 2\user_settings.config` directly, then starts the game with the modded-realm flag set. The Fatshark launcher is never touched.

Single-file self-contained `win-x64` build. No .NET runtime install required.

---

## What it does

### Mods tab
- Lists subscribed Workshop mods in their current load order, read straight from `user_settings.config`'s `mods = [ ... ]` array.
- Toggle a mod on/off; drag rows to reorder.
- `Refresh from Workshop` enriches each row via `ISteamRemoteStorage/GetPublishedFileDetails`:
  - Size on disk (from `appworkshop_552500.acf`) plus a size-match flag (size on disk vs API `file_size`).
  - Freshness flag (local `timeupdated` vs API `time_updated`).
  - `Folder` button opens the mod's Workshop content folder in Explorer.
- Live filter box matches name / author / Workshop ID.
- `Open page` per row opens the Workshop entry in the default browser.
- `Export collection` copies enabled-mod URLs to clipboard and opens Steam's Manage Collections page.
- Atomic save with `.bak` rollback; refuses to write when VT2 or the Fatshark launcher is running.

### Profiles tab
- Named JSON snapshots of "which mods are enabled, in what order".
- Save / Apply / Delete. Apply reports any missing or extra mods relative to the current subscription set.
- Stored at `%APPDATA%\Vt2ModManager\profiles\<name>.json`.

### Friends sidebar (left of the Mods grid)
- Auto-loads your Steam friend roster from `<SteamRoot>\userdata\<accountid>\config\localconfig.vdf`. Add additional friends by SteamID64, vanity URL, or full profile URL.
- Toggle **Show** to add a friend as a column on the Mods grid.
- Each row shows a Vis column with `Public` / `FriendsOnly` / `Private` / `Login`. Hover for an actionable hint.
- Checkbox columns on Mods grid show whether the friend is subscribed to each row.
- Virtual rows surface mods a friend has subscribed to but you haven't, with a `Subscribe` button that opens the Workshop page in the Steam overlay.

**Requires Steam to be running and signed in.** Steam routes anonymous requests for another user's `?browsefilter=mysubscriptions` page to a login interstitial — even for fully Public profiles. We pull the live `steamLoginSecure` session cookie from Steam's CEF cache, which only exists while the client is running. If Steam is closed, the Vis column shows `Login` with a tooltip explaining how to fix it. There's no Steam Web API endpoint that bypasses this — `IPublishedFileService` exposes authored files, not subscriptions.

### Conflicts tab
- Tier-1 static-scan of loose `.lua` source under each enabled mod's `source/` folder.
- Detects three collision classes:
  - `mod:hook_origin(Class, "method")` collisions (Crash severity — second mod silently shadows the first).
  - `BuffTemplates[name]` literal-key collisions (High severity — likely network desync crash).
  - VMF `setting_id` collisions (Medium severity — broken settings UI).
- Bundle-only mods (no loose source) are listed as "unanalyzable" rather than silently skipped.

---

## Install

Download `Vt2ModManager.exe` from the [latest release](https://github.com/Ensrick/vt2-mod-manager/releases/latest) and run it. No installer, no dependencies — the binary is self-contained `win-x64` with `PublishSingleFile=true`.

Optional: verify the download against `Vt2ModManager.exe.sha256`:

```powershell
Get-FileHash Vt2ModManager.exe -Algorithm SHA256
```

---

## First-run checklist

1. Close Vermintide 2 and the Fatshark launcher. Saves will refuse to run while either is open.
2. Subscribe to a few mods on the Workshop normally.
3. Launch `Vt2ModManager.exe`. The Mods tab should populate from `user_settings.config`.
4. Click `Refresh from Workshop` to pull size + freshness data. (Cold call, takes a second.)
5. Toggle / reorder, then click `Save`.
6. Add friends on the Friends tab — paste any of: 17-digit SteamID64, `steamcommunity.com/id/<vanity>`, or `steamcommunity.com/profiles/<id>`. Star a few; their columns appear on Mods tab.
7. When ready, hit `Launch VT2 (bypass launcher)` on the Mods tab.

---

## Settings reference

| Path | Purpose |
|---|---|
| `%APPDATA%\Vt2ModManager\settings.json` | App settings (see below). |
| `%APPDATA%\Vt2ModManager\profiles\<name>.json` | One file per profile. |
| `%APPDATA%\Vt2ModManager\friends.json` | Friends list + favorites + last selection. |
| `%APPDATA%\Vt2ModManager\load-order-rules.json` | Curated extra dependencies fed into auto-sort (see below). |
| `%AppData%\Fatshark\Vermintide 2\user_settings.config` | Game-side mod list. App reads + rewrites the `mods = [ ... ]` array. |

`settings.json` fields (all optional):

```json
{
  "steam_path_override": "C:\\Program Files (x86)\\Steam",
  "user_settings_config_path_override": "C:\\path\\to\\user_settings.config",
  "last_profile": "modded-realm"
}
```

Defaults probe the Steam registry key and the default Fatshark `%AppData%` path.

---

## CLI reference

The same exe is both GUI and CLI. Launching with no args (or `--gui`) opens the WPF window; passing a verb runs headless.

```
vt2-mod-manager                       Launch GUI
vt2-mod-manager --gui                 Launch GUI explicitly
vt2-mod-manager <verb> [flags]        Headless command
```

### Verbs

```
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
       [--args "..."]                 Start VT2 directly. Default is modded realm.
                                      --via-steam: use steam:// (Fatshark launcher will appear).
                                      --official-realm: drop the -eac-untrusted flag.
selfupdate [--yes] [--json]           Check GitHub for a newer build; --yes installs it.
                                      Exit 10 = update available (when --yes omitted).
```

### Global flags

```
--no-banner                           Suppress the leading version banner
--config <path>                       Read settings from a specific JSON file
```

---

## Launching the game

The `Launch VT2 (bypass launcher)` button (and `vt2-mod-manager launch`) starts the game **without** the Fatshark launcher UI. Steps it performs:

1. Writes `binaries\steam_appid.txt` containing `552500` next to the game exe. Steamworks reads this on init when the game isn't started by Steam itself, so authentication still works.
2. Spawns `binaries\vermintide2.exe` with arguments:
   - `-eac-untrusted` — modded realm (default; clear the checkbox or pass `--official-realm` to use the official realm with EAC).
   - Any extra args from `--args "..."`.
3. Returns immediately. The game window opens in 5–10 seconds without going through `vermintide2_launcher.exe`.

Why this works: the Fatshark launcher's only job is to set the realm flag, write the `steam_appid.txt`, and exec the same binary. Skipping it shaves the boot animation, avoids the occasional launcher crash on Win11, and prevents the launcher's own modlist UI from re-ordering anything.

If something goes wrong, the `Launch via Steam` button falls back to `steam://rungameid/552500`, which goes through the launcher normally.

---

## Conflict detection scope

The Tier-1 scanner only reads loose `.lua` files under each enabled mod's `source/` subtree. It catches:

- **`mod:hook_origin(Class, "method")`** — VMF's "I own this method, no one else may hook" call. Two mods doing this on the same Class+method silently shadow.
- **`BuffTemplates[name] = ...`** — same-key writes to the global buff table. Network desync crashes when the second mod's template overwrites the first mid-run.
- **VMF `setting_id`** — duplicate widget IDs in a single mod's options tree break that mod's entire settings page (see VMF memory note). Cross-mod collisions on the same keybind ID also surface here.

What it does **not** catch:

- Conflicts inside compiled `.bundle` blobs. Bundle-only mods are listed as "unanalyzable" and need a Tier-2 pass via `vt2_bundle_unpacker` + an LJD decompiler — not shipped here.
- Semantic conflicts (two mods both rebalancing the same weapon) — out of scope for static scan.
- Runtime ordering issues that depend on which mod loads first — partially covered by `sort` / `load-order-rules.json`.

---

## Auto-update

On every GUI startup the app makes one HTTPS call to `https://api.github.com/repos/Ensrick/vt2-mod-manager/releases/latest`. If a newer `tag_name` is published and the running build is a release (not a dev build with no version tag), an `Update available` banner appears.

`selfupdate --yes` (or the GUI update button):

1. Downloads `Vt2ModManager.exe` from the release.
2. Verifies SHA-256 against the sibling `Vt2ModManager.exe.sha256` asset.
3. Renames the running exe to `Vt2ModManager.exe.old`, drops the new one in as `Vt2ModManager.exe.new`, then swaps it in.
4. Restarts.

Dev builds (debug configuration, no embedded version) skip the check entirely so iterating locally doesn't trigger update prompts.

---

## Building from source

Prereqs: .NET 9 SDK, Windows 10/11.

```powershell
git clone https://github.com/Ensrick/vt2-mod-manager.git
cd vt2-mod-manager
.\publish.ps1
# -> bin\Release\net9.0-windows\win-x64\publish\Vt2ModManager.exe
```

`-SkipTests` to skip the 87-case xUnit run; `-SkipOpen` to suppress the post-build Explorer launch.

### Project layout

```
Vt2ModManager.csproj           Single project; GUI + CLI in one exe.
Program.cs                     Entry point. Dispatches to CliRunner or App.
App.xaml / MainWindow.xaml     WPF shell.

Cli/
  CliRunner.cs                 Verb dispatcher + global flag parsing.
  Verbs/*.cs                   One file per CLI verb.

Services/
  UserSettingsConfigReader.cs  Parses VT2's mods array.
  UserSettingsConfigWriter.cs  Atomic write with .bak rollback.
  ProfileStore.cs              Named profile JSON store.
  ConflictDetector.cs          Tier-1 static scan.
  LoadOrderSorter.cs           VMF-first + topo sort + bottom pins.
  GameLauncher.cs              Direct stingray spawn with steam_appid.txt.
  WorkshopEnumerator.cs        ACF parser + content-folder enumeration.
  SteamWebApiClient.cs         ISteamRemoteStorage batch enrichment.
  FriendsService.cs            Friends store + scraper orchestration.
  SteamFriendScraper.cs        Workshop-subscription scrape per friend.
  UpdateChecker.cs             GitHub Releases API client.
  UpdateInstaller.cs           .new / .old swap dance.

ViewModels/                    INotifyPropertyChanged row VMs.

tests/
  *Tests.cs                    87 xUnit tests.
  fixtures/                    Captured user_settings.config + ACF samples.
```

---

## Contributing

PRs welcome. A few conventions:

- New `Services/` types get matching tests in `tests/`. Fixtures (sample `user_settings.config` blobs, ACF excerpts, Workshop API responses) live in `tests/fixtures/`. Prefer fixture-driven tests over inline string literals — both for readability and because the real-world inputs have surprising whitespace.
- Don't add a runtime dependency unless it's a Microsoft-owned NuGet. The whole point of self-contained `win-x64` is users get one exe.
- Match the terse comment style of the existing code. The codebase documents *why*, not *what*.
- Run `.\publish.ps1` before opening a PR. All 87 tests must pass.

---

## License

[MIT](LICENSE). Copyright 2026 danjo / Ensrick.
