# Changelog

All notable changes to vt2-mod-manager. Versioning follows [SemVer](https://semver.org/).

## [Unreleased]

## [0.1.2]

### Changed
- **Friends UX reworked.** Removed the dedicated Friends tab. Friend management now lives in a modeless "Manage friends" window opened from the Mods tab toolbar — friends are columns on the Mods grid, not a separate view.
- Per-friend columns render real **read-only `CheckBox`** controls (checked = friend is subscribed) instead of ✓ / blank glyphs.
- The Subscribe action is now a button on **every** row: `Subscribe` for friend-only ghost rows, `Unsubscribe` for mods you have installed. Both open the Steam-overlay Workshop page where Steam itself renders the contextually-correct toggle.
- Friend columns auto-rebuild from cached session data at launch; a background refresh kicks off after startup and re-renders columns as each friend's live state arrives. No more "Sync columns" click required.

## [0.1.1]

### Fixed
- README incorrectly named the launched game binary `binaries\stingray_win64_release_x64.exe`. The actual path is `binaries\vermintide2.exe`.

### Changed
- `.gitignore` ignores `*.sha256` so release-build hash sidecars don't clutter `git status`.

## [0.1.0]

First public release. Replaces the Fatshark launcher's Mods tab and adds load-order, profile, conflict-detection, and game-launch automation that the official launcher lacks.

### Added
- **Mod list (Phase 1–2)** — reads VT2's `%AppData%\Fatshark\Vermintide 2\user_settings.config`, displays the `mods = [ ... ]` array with toggle + drag-reorder. Atomic write-back with `.bak` rollback. Refuses to write when VT2 or the launcher is running.
- **Profiles (Phase 3)** — named JSON snapshots in `%APPDATA%\Vt2ModManager\profiles\`. Save current state, apply a profile (reorders + toggles, reports missing/extra mods), delete.
- **Tier-1 conflict detection (Phase 4)** — static scan of loose `.lua` source under each subscribed mod's `source/` folder. Detects:
  - `mod:hook_origin(Class, "method")` collisions across enabled mods (Crash severity)
  - `BuffTemplates[name]` literal-key collisions (High severity)
  - VMF `setting_id` (keybind) collisions (Medium severity)
  - Mods that ship only bundle binaries are reported as unanalyzable.
- **Auto-sort (Phase 5)** — three-pass: pin VMF top → Kahn topological sort over each mod's `children[]` array (plus curated extra dependencies from `%APPDATA%\Vt2ModManager\load-order-rules.json`) → bottom pins. Stable ties on current index. Cycles are preserved + reported rather than crashing.
- **Filter + per-row Workshop link (Phase 6a)** — live filter box matches name/author/id; per-row "Open page" button opens the Workshop entry in the browser.
- **Collection export (Phase 6b)** — `Export collection` button copies enabled-mod URLs to clipboard and opens Steam's Manage Collections page so users can paste into a Workshop Collection for sharing.
- **Direct launch (Phase 7)** — `Launch VT2 (bypass launcher)` button starts the game without the Fatshark launcher UI:
  - Writes `binaries\steam_appid.txt` (552500) so Steamworks initializes correctly.
  - Passes `-eac-untrusted` (modded realm) by default; "Modded realm" checkbox to toggle.
  - `Launch via Steam` button as a fallback (`steam://rungameid/552500`).
- **Per-mod folder + size + freshness (Phase 8)** — `Refresh from Workshop` queries `ISteamRemoteStorage/GetPublishedFileDetails` in 100-ID batches. Each row gets:
  - Local size from `appworkshop_552500.acf`.
  - Size-match flag (✓ / ⚠ / ?) comparing local vs API `file_size`.
  - Freshness flag (✓ / ⚠ / ?) comparing local `timeupdated` vs API `time_updated`.
  - `Folder` button that opens the mod's Workshop content folder in Explorer.
- **Friends compare (Phase 9a + 9d)** — add friends by SteamID64, vanity URL, or full profile URL. Per-friend columns on the Mods tab show ✓ / blank for each row. Virtual rows surface mods a friend has subscribed but you haven't, with a `Subscribe` button that opens the Steam-overlay Workshop page. Selection + favorites persist across launches via `%APPDATA%\Vt2ModManager\friends.json`.
- **Auto-update (Phase 9b)** — startup check against the latest GitHub Release; SHA-256-verified in-place `.new`/`.old` swap on user confirmation. Dev builds (`bin\Debug\` or `0.0.0-dev` version) skip the check silently.

### CLI
- `list [--json]` — list subscribed mods in load order.
- `activate <id_or_name> ...` / `deactivate ...` — toggle by Workshop ID or substring of name.
- `profile {save|list|apply|delete} <name>` — manage named profiles.
- `conflicts [--json]` — static conflict scan.
- `sort [--dry-run]` — auto-sort load order.
- `collection export [--enabled-only] [--format json|urls|tabbed] [--out <path>]` — export current mod set.
- `launch [--via-steam] [--official-realm] [--args "..."]` — start VT2.
- `selfupdate [--yes] [--json]` — check GitHub for a newer build; `--yes` installs it. Exit 10 = update available.
- `help` / `--help` / `-h`.

Global flags: `--no-banner`, `--config <path>`.

### Settings
JSON at `%APPDATA%\Vt2ModManager\settings.json`. All optional:
- `steam_path_override`
- `user_settings_config_path_override`
- `last_profile`
