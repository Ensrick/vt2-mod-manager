# Changelog

All notable changes to vt2-mod-manager. Versioning follows [SemVer](https://semver.org/).

## [Unreleased]

## [0.1.9]

### Added
- **Bi-directional sync with the Fatshark launcher.** Changes made in either tool are now reflected in the other:
  - **Re-read on window activate.** When you bring vt2-mod-manager back to focus (alt-tab, click), it re-reads `user_settings.config` if there are no unsaved changes. Anything the Fatshark launcher wrote while you were away appears immediately. Throttled to ~once per 2 seconds.
  - **3-way merge on save.** Every save now re-reads the latest file from disk and overlays our changes on top of it before writing. Mods toggled or reordered in the launcher between our reads survive — we no longer clobber the file with stale state.

## [0.1.8]

### Fixed
- **App hung at launch on some installs.** Startup chain was fully synchronous: read user_settings.config, cross-ref appworkshop_552500.acf, parse every localconfig.vdf under userdata. On machines with multiple Steam accounts or large config files (commonly 5–10 MB) this blocked the UI thread for seconds. Now the window renders immediately with a "Loading…" status; all disk + Steam I/O runs on a background Task and marshals results back to the UI thread when ready.

### Workaround if the prior version still hangs
The CLI bypass works without touching the UI:
```
Vt2ModManager.exe selfupdate --yes
```
This force-downloads v0.1.8 and swaps the binary in place.

## [0.1.7]

### Fixed
- **Mods list showed phantom entries** — items that lived in `user_settings.config`'s `mods` array but the user had since unsubscribed from. Now cross-referenced against `appworkshop_552500.acf` (Steam's authoritative installed-items record) and filtered out of the display. Phantom entries stay in the underlying block on save, so Steam re-subbing restores their previous enabled state.

### Changed
- **Toggling a mod's "On" checkbox now auto-saves** (debounced 750ms). No more "I ticked it but nothing happened" confusion. The explicit `Save` button still works and flushes the timer immediately.
- Reorder operations (Move up/down/top/bottom), Auto-sort, and Apply profile all trigger the same debounced save.
- `Launch VT2 (bypass launcher)` now flushes any pending debounced save before starting the game so the engine reads your latest toggle state.

## [0.1.6]

### Fixed
- **Friends-only profiles were being skipped instead of scraped.** When the requesting Steam account is in the owner's friends list, the subscriptions page IS reachable for a friends-only profile — but the XML pre-flight collapsed `<visibilityState>=2` to `Private` and short-circuited the scrape. Now `ProfileVisibility.FriendsOnly` is a distinct enum value; `RefreshAsync` only short-circuits on explicit `Private`. Friends-only and Unknown both attempt the scrape.

### Added
- **Auto-refresh on first Show=true toggle.** Previously you had to click "Refresh sel" after adding a friend to populate their column. Now the toggle kicks off a fetch in the background and re-renders columns as soon as the result lands.
- Scrape errors and visibility outcomes are now reflected in the sidebar status line on each refresh.

## [0.1.5]

### Changed
- **Friends moved to a persistent left sidebar on the Mods tab** (matches Workshop Sentinel's pattern). No more "Manage friends" button — the roster is always visible alongside the mods grid, with a `GridSplitter` between them so you can resize. Toggling "Show" on a friend immediately adds/removes their column from the mods grid.
- The previously-modeless `FriendsWindow` is no longer opened from the UI; the file is kept in the repo for now but unreferenced.

## [0.1.4]

### Added
- **Auto-loaded Steam friend roster.** On startup, friends are read from `<SteamRoot>\userdata\<accountid>\config\localconfig.vdf` and merged into `friends.json` with `origin=steam`. No more typing SteamID64s for friends already on your Steam list.
- **XML profile pre-flight.** Each friend refresh hits `https://steamcommunity.com/profiles/<sid>?xml=1` first to detect Public / Friends-only / Private + the canonical persona name. Friends-only and Private profiles short-circuit the heavier HTML subscription scrape (which never works for them).
- **In-app Subscribe / Unsubscribe** via Steam's CEF session cookies. `SteamCefCookieReader` DPAPI-decrypts the Chromium cookie store at `%LOCALAPPDATA%\Steam\htmlcache\Default\Network\Cookies`; `SteamSubscribeClient` POSTs to `steamcommunity.com/sharedfiles/{subscribe,unsubscribe}` with the live `steamLoginSecure` + `sessionid`. Falls back to opening the Workshop page in the Steam overlay if cookies are unavailable.
- **"Show" checkbox column** on the Manage Friends window. Friends become Mods-tab columns only when checked — keeps a 50-friend Steam roster from exploding into 50 columns.
- **`Origin` badge** on each row (`Steam` / `Manual`) so you can see at a glance which friends came from auto-discovery vs which you added yourself.

### Migrated
- `friends.json` schema bumped 1 → 2 (wrapper object with `schema_version`). Existing v0.1.0 — v0.1.3 entries are auto-upgraded on first load: every previously-tracked friend is set to `favorite=true` so they keep showing as columns. Migration runs once and re-saves the file in the new shape.

### Notes
- The in-app subscribe path is best-effort. The most common failure is `MissingRequiredCookies` — Steam only writes `steamLoginSecure` after you've opened the community in the Steam overlay this session. When that happens the app falls back to the overlay link automatically.
- Friends-only `<visibilityState>=2` is collapsed to `Private` because our `ProfileVisibility` enum has no FriendsOnly member. Downstream effect is identical (can't scrape subs either way).

## [0.1.3]

### Fixed
- **Auto-update banner could miss new releases for up to 6 hours.** The on-disk cache TTL was 6h; when the just-installed exe wrote a cache saying "v0.1.x is latest" any subsequent release shipped within the TTL window stayed hidden. Reduced TTL to 1 minute and made the cache a pure burst-dampener.

### Added
- ETag / `If-None-Match` conditional requests to the GitHub releases API. Every launch now polls, but unchanged responses return 304 (which doesn't count against the 60/hr unauth'd rate limit) — so the short TTL doesn't cost network round-trips when nothing's new.

### Workaround for v0.1.0 / v0.1.1 / v0.1.2 users stuck on old cache
Run from a command prompt:
```
"%LOCALAPPDATA%\Programs\Vt2ModManager\Vt2ModManager.exe" selfupdate --yes
```
or from wherever the exe lives. CLI `selfupdate` already bypasses the cache via `forceRefresh: true` so it always sees the latest release.

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
