using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Vt2ModManager.Services;
using Vt2ModManager.ViewModels;

namespace Vt2ModManager;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly UserSettingsConfigReader _reader = new();
    private readonly UserSettingsConfigWriter _writer = new();
    private readonly ProfileStore _profileStore = new();

    private Settings _settings = new();
    private ModListBlock? _block;
    private readonly ObservableCollection<ModRowViewModel> _mods = new();
    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;
    private readonly ObservableCollection<ProfileRowViewModel> _profiles = new();
    private readonly ObservableCollection<ConflictRowViewModel> _conflicts = new();
    private FriendsService? _friendsService;
    private readonly ObservableCollection<ViewModels.FriendRowViewModel> _friends = new();

    // Self-update wiring. The HttpClient is long-lived; the WPF process model means we'd
    // otherwise churn through ephemeral ports across repeated checks.
    private readonly HttpClient _updateHttp = BuildUpdateHttpClient();
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateBannerDismissed;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{Program.Version}";
        ModsGrid.ItemsSource = _mods;
        ProfilesGrid.ItemsSource = _profiles;
        ConflictsGrid.ItemsSource = _conflicts;
        FriendsGrid.ItemsSource = _friends;

        _updateChecker   = new UpdateChecker(_updateHttp);
        _updateInstaller = new UpdateInstaller(_updateHttp);

        Loaded += async (_, _) =>
        {
            SetStatus("Loading…");
            await InitialLoadAsync();
            if (!Program.IsDevBuild()) _ = CheckForUpdatesAsync();
        };
        // Bi-directional sync with the Fatshark launcher: when our window gets focus, re-read
        // user_settings.config so anything the launcher wrote while we were backgrounded shows
        // up immediately. Skipped if we have unsaved changes — those would otherwise be lost.
        Activated += (_, _) => OnWindowActivated();
    }

    private DateTime _lastActivatedRefreshUtc = DateTime.MinValue;

    private void OnWindowActivated()
    {
        // Activated fires for every focus event, including alt-tab back to a still-open window.
        // Throttle so we don't re-parse a 1MB config on every focus, but still pick up external
        // changes within a few seconds of switching back.
        if (DateTime.UtcNow - _lastActivatedRefreshUtc < TimeSpan.FromSeconds(2)) return;
        if (_autoSaveTimer is { IsEnabled: true }) return;          // pending unsaved changes — don't clobber
        if (_block is null) return;                                 // not yet loaded
        _lastActivatedRefreshUtc = DateTime.UtcNow;
        _ = RefreshFromDiskAsync();
    }

    private async System.Threading.Tasks.Task RefreshFromDiskAsync()
    {
        try
        {
            var (block, resolved) = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var locator = new UserSettingsConfigLocator(_settings);
                    var p = locator.Resolve();
                    if (!System.IO.File.Exists(p)) return ((ModListBlock?)null, default(ResolvedLocalIds));
                    var b = _reader.ReadFile(p);
                    ResolvedLocalIds r = default;
                    try { r = ResolveInstalledIdsCore(_settings); } catch { }
                    return ((ModListBlock?)b, r);
                }
                catch { return ((ModListBlock?)null, default(ResolvedLocalIds)); }
            });
            if (block is null) return;
            // Detect actual change before clobbering the grid (avoids needless re-render churn).
            if (BlockSemanticallyEquals(_block!, block)) return;
            _block = block;
            RebuildModRowsFromBlock(resolved);
            SetStatus("Re-synced from user_settings.config (external change detected).");
        }
        catch { /* best effort */ }
    }

    /// <summary>Cheap structural equality on the parts a user can flip from another tool:
    /// the set of mod IDs, their order, and each Enabled flag. Other fields (timestamps, banners)
    /// aren't user-meaningful for our sync purposes.</summary>
    private static bool BlockSemanticallyEquals(ModListBlock a, ModListBlock b)
    {
        if (a.Entries.Count != b.Entries.Count) return false;
        for (int i = 0; i < a.Entries.Count; i++)
        {
            if (a.Entries[i].Id != b.Entries[i].Id) return false;
            if (a.Entries[i].Enabled != b.Entries[i].Enabled) return false;
        }
        return true;
    }

    private static HttpClient BuildUpdateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Vt2ModManager/{Program.Version}");
        return c;
    }

    /// <summary>
    /// Startup loader. Renders the window first, then performs every disk + Steam I/O on a
    /// background <see cref="Task"/> so the UI thread never blocks. Each stage marshals its
    /// results back to the dispatcher for binding. Pre-v0.1.8 startup was fully synchronous,
    /// which hung on machines where localconfig.vdf or appworkshop_552500.acf were slow to read.
    /// </summary>
    private async System.Threading.Tasks.Task InitialLoadAsync()
    {
        try
        {
            // Stage 0: tiny synchronous bootstrap (just object construction).
            _settings       = _settingsStore.Load();
            _friendsService = new FriendsService(_updateHttp);

            // Stage 1: read user_settings.config + cross-ref appworkshop ACF off the UI thread.
            SetStatus("Reading user_settings.config…");
            var modLoad = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var locator = new UserSettingsConfigLocator(_settings);
                    var p = locator.Resolve();
                    if (!System.IO.File.Exists(p)) return (Path: p, Block: (ModListBlock?)null, Resolved: default(ResolvedLocalIds), Err: (string?)null);
                    var b = _reader.ReadFile(p);
                    ResolvedLocalIds r = default;
                    try { r = ResolveInstalledIdsCore(_settings); } catch { /* swallow; we'll just not filter */ }
                    return (Path: p, Block: (ModListBlock?)b, Resolved: r, Err: (string?)null);
                }
                catch (Exception ex)
                {
                    return (Path: "(unknown)", Block: (ModListBlock?)null, Resolved: default(ResolvedLocalIds), Err: (string?)ex.Message);
                }
            });

            if (modLoad.Err is not null)
            {
                SetStatus("Failed to read mod list: " + modLoad.Err);
            }
            else if (modLoad.Block is null)
            {
                SetStatus($"user_settings.config not found at {modLoad.Path}. Set 'user_settings_config_path_override' in settings.json.");
            }
            else
            {
                _block = modLoad.Block;
                RebuildModRowsFromBlock(modLoad.Resolved);
                var visibleCount = _mods.Count;
                var pendingCount = _mods.Count(m => m.LocalState == "Pending");
                var hidden = modLoad.Resolved.Filter is null ? 0 : _block.Entries.Count - visibleCount;
                var msg = pendingCount > 0
                    ? $"Loaded {visibleCount} subscribed mods ({pendingCount} pending download)"
                    : $"Loaded {visibleCount} mods";
                if (hidden > 0) msg += $", {hidden} unsubscribed hidden";
                SetStatus(msg + ".");
            }

            // Stage 2: profiles dir listing — fast, but still don't block in case it's slow.
            await System.Threading.Tasks.Task.Run(() => { try { Dispatcher.Invoke(ReloadProfiles); } catch { } });

            // Stage 3: Steam friend roster from localconfig.vdf. THIS is what hung pre-v0.1.8;
            // some installs have ~10 MB userdata configs that take seconds to parse.
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var resolver = new SteamPathResolver(_settings);
                    if (resolver.TryResolve(out var steamRoot))
                        _friendsService.SyncSteamRoster(steamRoot);
                }
                catch { /* best effort */ }
            });

            ReloadFriends();
            SyncFriendColumns();

            _ = BackgroundRefreshFriendsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Failed to load: " + ex.Message);
        }
    }

    private void ReloadMods()
    {
        var locator = new UserSettingsConfigLocator(_settings);
        var path = locator.Resolve();
        if (!System.IO.File.Exists(path))
        {
            SetStatus($"user_settings.config not found at {path}. Set 'user_settings_config_path_override' in settings.json to point at it.");
            _mods.Clear();
            return;
        }

        _block = _reader.ReadFile(path);
        var resolved = ResolveInstalledIds();
        RebuildModRowsFromBlock(resolved);

        var visibleCount = _mods.Count;
        var pendingCount = _mods.Count(m => m.LocalState == "Pending");
        var hidden = resolved.Filter is null ? 0 : _block.Entries.Count - visibleCount;
        var msg = pendingCount > 0
            ? $"Loaded {visibleCount} subscribed mods ({pendingCount} pending download)"
            : $"Loaded {visibleCount} mods";
        if (hidden > 0) msg += $", {hidden} unsubscribed entries hidden";
        SetStatus(msg + $" from {path}.");
    }

    /// <summary>
    /// Resolved view of "what's the user's mod set locally" for the Mods tab. Three states:
    ///   <list type="bullet">
    ///     <item><c>Filter</c> = null → couldn't resolve Steam at all; show every entry in user_settings.config.</item>
    ///     <item><c>Filter</c> ⊇ <c>Downloaded</c> ⊇ items currently on disk per appworkshop_552500.acf.</item>
    ///     <item><c>Filter</c> ⊇ items subscribed per &lt;appid&gt;_subscriptions.vdf, including pending downloads.</item>
    ///   </list>
    /// A row whose ID is in <c>Filter</c> but NOT in <c>Downloaded</c> is "Pending" — subscribed but
    /// Steam hasn't finished pulling the bundle yet.
    /// </summary>
    private readonly struct ResolvedLocalIds
    {
        public HashSet<string>? Filter { get; init; }
        public HashSet<string>? Downloaded { get; init; }
    }

    /// <summary>Rebuild the visible mod-row collection from <c>_block.Entries</c>, filtering out
    /// entries that Steam neither has installed nor reports as subscribed. Phantom entries stay
    /// in <c>_block</c>. Sets each row's <see cref="ModRowViewModel.LocalState"/> based on whether
    /// the bundle is on disk yet.</summary>
    private void RebuildModRowsFromBlock(ResolvedLocalIds? resolvedOpt = null)
    {
        if (_block is null) return;
        var resolved = resolvedOpt ?? ResolveInstalledIds();
        foreach (var r in _mods) r.PropertyChanged -= OnModRowPropertyChanged;
        _mods.Clear();
        int order = 0;
        foreach (var entry in _block.Entries)
        {
            if (resolved.Filter is not null && !resolved.Filter.Contains(entry.Id)) continue;
            var row = new ModRowViewModel(entry, ++order);
            if (resolved.Downloaded is not null)
                row.LocalState = resolved.Downloaded.Contains(entry.Id) ? "Downloaded" : "Pending";
            _mods.Add(row);
        }
        WireRowAutoSave();
    }

    /// <summary>Returns the union of "downloaded per ACF" and "subscribed per ugc vdf", plus the
    /// downloaded subset so callers can label pending rows. <c>Filter</c> is null when we can't
    /// resolve Steam at all (no filtering applied).</summary>
    private ResolvedLocalIds ResolveInstalledIds() => ResolveInstalledIdsCore(_settings);

    private static ResolvedLocalIds ResolveInstalledIdsCore(Settings settings)
    {
        try
        {
            var resolver = new SteamPathResolver(settings);
            if (!resolver.TryResolve(out var steamRoot)) return default;
            var libs = new LibraryFoldersResolver().Resolve(steamRoot);

            // Downloaded set — items Steam has actually pulled to disk.
            var downloaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in new WorkshopEnumerator().EnumerateForApp(libs, Vt2Installation.AppId))
                downloaded.Add(item.PublishedFileId.ToString());

            // Subscribed set — includes downloads in progress. Empty when Steam has never
            // opened the Workshop for this user/app, in which case we fall back to ACF only.
            HashSet<string>? subscribed = null;
            try
            {
                var subs = new SteamSubscriptionsResolver(steamRoot)
                    .ResolveSubscribedIds(Vt2Installation.AppId);
                if (subs.Count > 0)
                    subscribed = new HashSet<string>(subs, StringComparer.Ordinal);
            }
            catch { /* fall back to ACF-only */ }

            // Union: show anything that's either downloaded OR known-subscribed.
            HashSet<string>? filter;
            if (subscribed is null)
                filter = downloaded.Count == 0 ? null : downloaded;
            else
            {
                filter = new HashSet<string>(downloaded, StringComparer.Ordinal);
                filter.UnionWith(subscribed);
                if (filter.Count == 0) filter = null;
            }

            return new ResolvedLocalIds
            {
                Filter     = filter,
                Downloaded = downloaded.Count == 0 ? null : downloaded,
            };
        }
        catch { return default; }
    }

    private void WireRowAutoSave()
    {
        foreach (var row in _mods)
        {
            row.PropertyChanged -= OnModRowPropertyChanged;
            row.PropertyChanged += OnModRowPropertyChanged;
        }
    }

    private void OnModRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModRowViewModel.Enabled))
            ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= OnAutoSaveTick;
        _autoSaveTimer.Tick += OnAutoSaveTick;
        _autoSaveTimer.Start();
        SetStatus("Pending save…");
    }

    private void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer?.Stop();
        SaveBlockNow();
    }

    private void SaveBlockNow()
    {
        if (_block is null) return;
        try
        {
            var locator = new UserSettingsConfigLocator(_settings);
            var path = locator.Resolve();

            // Bi-directional sync with the Fatshark launcher: pull the LATEST file from disk
            // immediately before writing, overlay our changes on top of it, then write the merged
            // result. Anything the launcher added or modified while our app was open survives.
            ModListBlock target = _block;
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var latest = _reader.ReadFile(path);
                    target = MergeOurStateOnto(latest, _block);
                }
            }
            catch
            {
                // Disk read failed (locked / corrupt) — fall back to writing our state verbatim.
                target = _block;
            }

            _writer.WriteFile(path, target);
            _block = target; // adopt the merged state so subsequent operations stay in sync
            SetStatus($"Saved {target.Entries.Count} mods. Backup at user_settings.config.bak.");
        }
        catch (Exception ex)
        {
            SetStatus("Auto-save failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 3-way merge: <paramref name="latest"/> is what's on disk right now (may include changes
    /// the Fatshark launcher made while we were running); <paramref name="ours"/> is our
    /// in-memory state with the user's toggles + reorders. Returns a new <see cref="ModListBlock"/>
    /// keyed on the disk file's prefix/suffix that:
    ///   - Uses our order for mods we know about.
    ///   - Applies our Enabled flag to mods in both.
    ///   - Preserves any mod in <paramref name="latest"/> that we didn't see (the launcher added
    ///     it after we last read) — appended at the end in its original relative order.
    /// </summary>
    private static ModListBlock MergeOurStateOnto(ModListBlock latest, ModListBlock ours)
    {
        var latestById = latest.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var ourIdsSet  = new HashSet<string>(ours.Entries.Select(e => e.Id), StringComparer.Ordinal);

        var merged = new List<ModEntry>(latest.Entries.Count);

        // Walk OUR entries in order. For each, prefer the disk entry (so launcher-side field
        // updates survive) but force-apply OUR enabled flag.
        foreach (var ourEntry in ours.Entries)
        {
            if (latestById.TryGetValue(ourEntry.Id, out var diskEntry))
            {
                diskEntry.Enabled = ourEntry.Enabled;
                merged.Add(diskEntry);
            }
            else
            {
                // Mod exists in our memory but not on disk — Fatshark may have removed it after
                // we read it. Re-introduce so the user doesn't lose their toggle state.
                merged.Add(ourEntry);
            }
        }

        // Append anything on disk we hadn't seen — newly added by the launcher.
        foreach (var diskEntry in latest.Entries)
        {
            if (!ourIdsSet.Contains(diskEntry.Id))
                merged.Add(diskEntry);
        }

        return new ModListBlock
        {
            RawPrefix  = latest.RawPrefix,
            RawSuffix  = latest.RawSuffix,
            LineEnding = latest.LineEnding,
            Entries    = merged,
        };
    }

    private void ReloadProfiles()
    {
        _profiles.Clear();
        foreach (var name in _profileStore.List())
        {
            var p = _profileStore.Load(name);
            if (p is not null) _profiles.Add(new ProfileRowViewModel(p));
        }
    }

    private void SetStatus(string s) => StatusLabel.Text = s;

    // ---------- Mods toolbar ----------

    private void OnReloadClicked(object sender, RoutedEventArgs e) => ReloadMods();

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Nothing loaded."); return; }
        _autoSaveTimer?.Stop();
        SaveBlockNow();
    }

    private void OnEnableAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in _mods) row.Enabled = true;
        SetStatus($"Enabled {_mods.Count} mods (unsaved).");
    }

    private void OnDisableAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in _mods) row.Enabled = false;
        SetStatus($"Disabled {_mods.Count} mods (unsaved).");
    }

    private void OnLaunchDirectClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Flush any pending debounced save so the game reads our latest changes.
            if (_autoSaveTimer is { IsEnabled: true })
            {
                _autoSaveTimer.Stop();
                SaveBlockNow();
            }
            var pathResolver = new SteamPathResolver(_settings);
            if (!pathResolver.TryResolve(out var steamRoot)) { SetStatus("Could not locate Steam install."); return; }
            var libs = new LibraryFoldersResolver().Resolve(steamRoot);
            var install = Vt2Installation.Resolve(steamRoot, libs);
            if (install is null) { SetStatus("VT2 install not found via appmanifest_552500.acf."); return; }

            if (GameLauncher.IsGameRunning())
            {
                MessageBox.Show(this, "Vermintide 2 is already running.", "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var modded = ModdedRealmCheck.IsChecked == true;
            var result = new GameLauncher().LaunchDirect(install, moddedRealm: modded);
            SetStatus(result.Message);
            if (!result.Started)
                MessageBox.Show(this, result.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { SetStatus("Launch failed: " + ex.Message); }
    }

    private void OnLaunchViaSteamClicked(object sender, RoutedEventArgs e)
    {
        var result = new GameLauncher().LaunchViaSteam();
        SetStatus(result.Message);
    }

    private void OnExportCollectionClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        var exporter = new CollectionExporter();
        var export = exporter.Build(_block.Entries, enabledOnly: true);
        var urls = exporter.ToUrlList(export);
        try
        {
            System.Windows.Clipboard.SetText(urls);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = CollectionExporter.ManageCollectionsUrl,
                UseShellExecute = true,
            });
            SetStatus($"Copied {export.Count} Workshop URLs to clipboard and opened Steam's Manage Collections page. Paste IDs into a Collection, then share its URL.");
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_mods);
        var query = FilterBox.Text;
        view.Filter = o => o is ModRowViewModel row && row.MatchesFilter(query);
    }

    private void OnOpenWorkshopPageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.DataContext is ModRowViewModel row)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = row.WorkshopUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { SetStatus("Open failed: " + ex.Message); }
        }
    }

    private void OnOpenModFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not ModRowViewModel row) return;
        var folder = row.LocalFolderPath;
        if (string.IsNullOrEmpty(folder))
        {
            // Compute on demand if Refresh hasn't been run.
            try
            {
                var pathResolver = new SteamPathResolver(_settings);
                if (!pathResolver.TryResolve(out var steamRoot)) { SetStatus("Steam install not found."); return; }
                var libs = new LibraryFoldersResolver().Resolve(steamRoot);
                foreach (var lib in libs)
                {
                    var candidate = System.IO.Path.Combine(lib, "steamapps", "workshop", "content", "552500", row.Id);
                    if (System.IO.Directory.Exists(candidate)) { folder = candidate; break; }
                }
            }
            catch (Exception ex) { SetStatus("Folder lookup failed: " + ex.Message); return; }
        }
        if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
        {
            SetStatus($"Folder not found for mod {row.Id} — not currently installed on disk.");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { SetStatus("Open folder failed: " + ex.Message); }
    }

    private async void OnRefreshFromWorkshopClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        SetStatus("Refreshing from Workshop…");
        try
        {
            var pathResolver = new SteamPathResolver(_settings);
            if (!pathResolver.TryResolve(out var steamRoot)) { SetStatus("Steam install not found."); return; }
            var libs = new LibraryFoldersResolver().Resolve(steamRoot);

            // Local: read appworkshop_552500.acf for size/timeupdated.
            var locals = new WorkshopEnumerator()
                .EnumerateForApp(libs, Vt2Installation.AppId)
                .ToDictionary(x => x.PublishedFileId);

            // Locate the workshop content root for folder paths.
            string? contentRoot = null;
            foreach (var lib in libs)
            {
                var candidate = System.IO.Path.Combine(lib, "steamapps", "workshop", "content", "552500");
                if (System.IO.Directory.Exists(candidate)) { contentRoot = candidate; break; }
            }

            // Remote: Steam Web API in 100-ID batches.
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new SteamWebApiClient(http);
            var ids = _block.Entries
                .Select(e => ulong.TryParse(e.Id, out var n) ? n : 0UL)
                .Where(n => n != 0)
                .ToList();
            var remotes = await client.GetPublishedFileDetailsAsync(ids);

            foreach (var row in _mods)
            {
                if (!ulong.TryParse(row.Id, out var id)) continue;
                locals.TryGetValue(id, out var local);
                remotes.TryGetValue(id, out var remote);
                var folder = contentRoot is null ? null : System.IO.Path.Combine(contentRoot, row.Id);
                if (folder is not null && !System.IO.Directory.Exists(folder)) folder = null;
                row.AttachWorkshopData(local, remote, folder);
            }
            var stale = _mods.Count(m => m.FreshnessIcon == "⚠");
            var mismatch = _mods.Count(m => m.SizeMatchIcon == "⚠");
            SetStatus($"Refresh complete. {stale} stale, {mismatch} size mismatch.");
        }
        catch (Exception ex)
        {
            SetStatus("Refresh failed: " + ex.Message);
        }
    }

    private void OnAutoSortClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        var rules = LoadOrderRules.LoadOrCreate();
        var result = new LoadOrderSorter().Sort(_block.Entries, rules);
        _block.Entries.Clear();
        _block.Entries.AddRange(result.Sorted);
        RebuildModRowsFromBlock();
        ScheduleAutoSave();
        var msg = $"Auto-sorted. Rules at {LoadOrderRules.DefaultPath()}.";
        if (result.CycleMemberIds.Count > 0)
            msg += $" Cycle warning: {result.CycleMemberIds.Count} mod(s).";
        if (result.UnknownPinIds.Count > 0)
            msg += $" {result.UnknownPinIds.Count} pin(s) not installed.";
        SetStatus(msg);
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e)    => MoveSelected(-1);
    private void OnMoveDownClicked(object sender, RoutedEventArgs e)  => MoveSelected(+1);
    private void OnMoveTopClicked(object sender, RoutedEventArgs e)   => MoveSelectedTo(top: true);
    private void OnMoveBottomClicked(object sender, RoutedEventArgs e)=> MoveSelectedTo(top: false);

    private void MoveSelected(int delta)
    {
        if (_block is null) return;
        var selected = ModsGrid.SelectedItems.Cast<ModRowViewModel>().ToList();
        if (selected.Count == 0) return;
        var indices = selected.Select(s => _mods.IndexOf(s)).OrderBy(i => i).ToList();
        if (delta < 0)
        {
            if (indices[0] == 0) return;
            foreach (var idx in indices)
            {
                _mods.Move(idx, idx + delta);
            }
        }
        else
        {
            if (indices[^1] == _mods.Count - 1) return;
            foreach (var idx in indices.AsEnumerable().Reverse())
            {
                _mods.Move(idx, idx + delta);
            }
        }
        SyncBlockOrderFromUi();
    }

    private void MoveSelectedTo(bool top)
    {
        if (_block is null) return;
        var selected = ModsGrid.SelectedItems.Cast<ModRowViewModel>().ToList();
        if (selected.Count == 0) return;
        // Preserve relative order among selected rows.
        var ordered = selected
            .Select(s => (Row: s, Index: _mods.IndexOf(s)))
            .OrderBy(t => t.Index)
            .Select(t => t.Row)
            .ToList();
        foreach (var row in ordered) _mods.Remove(row);
        var insertAt = top ? 0 : _mods.Count;
        foreach (var row in ordered)
        {
            _mods.Insert(insertAt, row);
            insertAt++;
        }
        SyncBlockOrderFromUi();
    }

    private void SyncBlockOrderFromUi()
    {
        if (_block is null) return;
        // Preserve any phantom entries (subscribed-then-unsubscribed mods that we hide in the
        // grid but keep in _block so Steam-resub restores their settings). They go at the end
        // in their original relative order so the visible-mod order is what the engine sees.
        var visibleEntries = _mods.Where(m => !m.IsVirtual).Select(m => m.Entry).ToList();
        var visibleSet = new HashSet<ModEntry>(visibleEntries);
        var phantoms = _block.Entries.Where(e => !visibleSet.Contains(e)).ToList();

        _block.Entries.Clear();
        var order = 0;
        foreach (var entry in visibleEntries)
        {
            _block.Entries.Add(entry);
            order++;
        }
        for (int i = 0; i < _mods.Count; i++)
        {
            if (_mods[i].IsVirtual) { _mods[i].Order = 0; continue; }
            _mods[i].Order = i + 1; // 1-based among visible rows
        }
        // Re-append phantoms verbatim so their settings survive a Steam re-sub.
        foreach (var p in phantoms) _block.Entries.Add(p);

        ScheduleAutoSave();
    }

    // ---------- Profiles ----------

    private void OnSaveProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        var name = (NewProfileNameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) { SetStatus("Type a profile name first."); return; }
        var profile = ProfileStore.Capture(name, _block);
        _profileStore.Save(profile);
        NewProfileNameBox.Text = "";
        ReloadProfiles();
        SetStatus($"Saved profile '{name}' ({profile.Entries.Count} mods).");
    }

    private void OnApplyProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        if (ProfilesGrid.SelectedItem is not ProfileRowViewModel row) { SetStatus("Pick a profile."); return; }
        var result = ProfileStore.Apply(row.Profile, _block);
        RebuildModRowsFromBlock();
        ScheduleAutoSave();
        SetStatus($"Applied '{row.Name}': {result.ToggledCount} toggled, {result.Missing.Count} missing, {result.Extras.Count} extras appended.");
    }

    private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not ProfileRowViewModel row) { SetStatus("Pick a profile."); return; }
        var ok = MessageBox.Show(this, $"Delete profile '{row.Name}'?", "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _profileStore.Delete(row.Name);
        ReloadProfiles();
        SetStatus($"Deleted '{row.Name}'.");
    }

    // ---------- Conflicts ----------

    private void OnScanConflictsClicked(object sender, RoutedEventArgs e)
    {
        if (_block is null) { SetStatus("Load mods first."); return; }
        try
        {
            var pathResolver = new SteamPathResolver(_settings);
            if (!pathResolver.TryResolve(out var steamRoot))
            {
                SetStatus("Could not locate Steam install.");
                return;
            }
            var libs = new LibraryFoldersResolver().Resolve(steamRoot);
            // VT2 app id = 552500. The workshop content folder lives in one of the libraries.
            string? workshopRoot = null;
            foreach (var lib in libs)
            {
                var candidate = System.IO.Path.Combine(lib, "steamapps", "workshop", "content", "552500");
                if (System.IO.Directory.Exists(candidate)) { workshopRoot = candidate; break; }
            }
            if (workshopRoot is null)
            {
                SetStatus("No VT2 Workshop content folder found across Steam libraries.");
                return;
            }
            var cache = new ModSourceCache(workshopRoot);
            var detector = new ConflictDetector(cache);
            var findings = detector.Detect(_block);

            var nameById = _block.Entries.ToDictionary(e2 => e2.Id, e2 => e2.Name, StringComparer.Ordinal);
            _conflicts.Clear();
            foreach (var c in findings) _conflicts.Add(new ConflictRowViewModel(c, nameById));
            SetStatus($"Conflict scan: {findings.Count} finding(s).");
        }
        catch (Exception ex)
        {
            SetStatus("Conflict scan failed: " + ex.Message);
        }
    }

    // ---------- Self-update ----------

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await _updateChecker.CheckAsync(Program.Version);
            await Dispatcher.InvokeAsync(() => ApplyUpdateCheck(result));
        }
        catch
        {
            // The checker swallows expected exceptions; anything reaching here is unexpected.
            // Don't take down the GUI — silently leave the banner hidden.
        }
    }

    private void ApplyUpdateCheck(UpdateCheckResult result)
    {
        if (result.Status == UpdateStatus.UpdateAvailable
            && !string.IsNullOrEmpty(result.LatestVersion)
            && !string.IsNullOrEmpty(result.DownloadUrl))
        {
            _pendingUpdate = result;
            UpdateBannerText.Text = $"VT2 Mod Manager v{result.LatestVersion} is available.";
            UpdateBanner.Visibility = _updateBannerDismissed ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            _pendingUpdate = null;
            UpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUpdateBannerLaterClicked(object sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || _pendingUpdate.DownloadUrl is null) return;
        var target = _pendingUpdate;

        var confirm = MessageBox.Show(this,
            $"Update to v{target.LatestVersion}? The app will restart.",
            "VT2 Mod Manager", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            MessageBox.Show(this, "Couldn't resolve the running executable path. Update aborted.",
                "VT2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        UpdateBannerInstallButton.IsEnabled = false;
        SetStatus($"Downloading v{target.LatestVersion}…");
        var progress = new Progress<double>(p =>
            SetStatus($"Downloading v{target.LatestVersion}… {(int)(p * 100)}%"));

        var result = await _updateInstaller.DownloadAndSwapAsync(
            exePath, target.DownloadUrl, target.AssetSha256, progress);

        if (!result.Success)
        {
            UpdateBannerInstallButton.IsEnabled = true;
            SetStatus("Update failed.");
            MessageBox.Show(this,
                $"Update failed: {result.Error}\n\nThe running app is unchanged. You can try again, or download v{target.LatestVersion} manually from the Releases page.",
                "VT2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetStatus("Update installed — restarting…");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = exePath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Update installed but relaunch failed: {ex.Message}\n\nClose this window and start Vt2ModManager.exe manually.",
                "VT2 Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Application.Current.Shutdown();
    }

    // ---------- Friends ----------

    private void TryAutoLoadSteamFriends()
    {
        if (_friendsService is null) return;
        try
        {
            var resolver = new SteamPathResolver(_settings);
            if (!resolver.TryResolve(out var steamRoot)) return;
            var added = _friendsService.SyncSteamRoster(steamRoot);
            if (added > 0)
                SetStatus($"Discovered {added} new Steam friend(s) from your roster. Open 'Manage friends' to pick which to compare.");
        }
        catch
        {
            // Best effort; users can still manually-add friends via the window.
        }
    }

    private void ReloadFriends()
    {
        if (_friendsService is null) return;
        // Unsubscribe from prior rows before clearing so stale handlers don't fire.
        foreach (var r in _friends) r.PropertyChanged -= OnFriendRowPropertyChanged;
        _friends.Clear();
        foreach (var f in _friendsService.List().OrderByDescending(f => f.Favorite).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ViewModels.FriendRowViewModel(f);
            if (_friendsService.SessionCache.TryGetValue(f.SteamId64, out var cached))
                row.AttachResult(cached);
            row.PropertyChanged += OnFriendRowPropertyChanged;
            _friends.Add(row);
        }
    }

    private void OnFriendRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_friendsService is null) return;
        if (e.PropertyName == nameof(ViewModels.FriendRowViewModel.Favorite) && sender is ViewModels.FriendRowViewModel row)
        {
            try { _friendsService.SetFavorite(row.SteamId64, row.Favorite); } catch { }
            SyncFriendColumns();
            // Auto-refresh on first Show=true so the column populates immediately instead of
            // waiting for the user to click "Refresh sel".
            if (row.Favorite && !_friendsService.SessionCache.ContainsKey(row.SteamId64))
                _ = AutoRefreshFriendAsync(row);
        }
    }

    private async System.Threading.Tasks.Task AutoRefreshFriendAsync(ViewModels.FriendRowViewModel row)
    {
        if (_friendsService is null) return;
        SetStatus($"Fetching {row.DisplayName}'s subscriptions…");
        try
        {
            var result = await _friendsService.RefreshAsync(row.SteamId64);
            row.AttachResult(result);
            SyncFriendColumns();
            if (!string.IsNullOrEmpty(result.Error))
                SetStatus($"{row.DisplayName}: {result.Error}");
            else
                SetStatus($"{row.DisplayName}: {result.Visibility}, {result.Mods.Count} mod(s).");
        }
        catch (Exception ex) { SetStatus($"Auto-refresh of {row.DisplayName} failed: {ex.Message}"); }
    }

    private async void OnAddFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        var input = (AddFriendBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) { SetStatus("Type a SteamID64, vanity, or profile URL."); return; }
        SetStatus($"Resolving '{input}'…");
        try
        {
            var friend = await _friendsService.AddAsync(input);
            if (friend is null) { SetStatus($"Couldn't resolve '{input}'."); return; }
            AddFriendBox.Text = "";
            ReloadFriends();
            SyncFriendColumns();
            SetStatus($"Added {friend.DisplayName} ({friend.SteamId64}). Toggle 'Show' to make them a column.");
        }
        catch (Exception ex) { SetStatus("Add failed: " + ex.Message); }
    }

    private void OnAddFriendKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnAddFriendClicked(sender, new RoutedEventArgs());
    }

    private void OnRemoveFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        if (FriendsGrid.SelectedItem is not ViewModels.FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        var ok = MessageBox.Show(this, $"Remove {row.DisplayName} ({row.SteamId64})?", "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _friendsService.Remove(row.SteamId64);
        ReloadFriends();
        SyncFriendColumns();
    }

    private async void OnRefreshFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        if (FriendsGrid.SelectedItem is not ViewModels.FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        SetStatus($"Refreshing {row.DisplayName}…");
        try
        {
            var result = await _friendsService.RefreshAsync(row.SteamId64);
            row.AttachResult(result);
            SyncFriendColumns();
            SetStatus($"{row.DisplayName}: {result.Visibility}, {result.Mods.Count} mod(s).");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private async void OnRefreshAllFriendsClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        SetStatus($"Refreshing {_friends.Count} friends…");
        try
        {
            foreach (var row in _friends.ToList())
            {
                var result = await _friendsService.RefreshAsync(row.SteamId64);
                row.AttachResult(result);
            }
            SyncFriendColumns();
            SetStatus($"Refreshed {_friends.Count} friends.");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private void OnRescanSteamRosterClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        try
        {
            var resolver = new SteamPathResolver(_settings);
            if (!resolver.TryResolve(out var steamRoot)) { SetStatus("Steam install not found."); return; }
            var added = _friendsService.SyncSteamRoster(steamRoot);
            ReloadFriends();
            SetStatus(added > 0 ? $"Discovered {added} new Steam friend(s)." : "No new Steam friends found.");
        }
        catch (Exception ex) { SetStatus("Re-scan failed: " + ex.Message); }
    }

    private async System.Threading.Tasks.Task BackgroundRefreshFriendsAsync()
    {
        if (_friendsService is null) return;
        try
        {
            foreach (var friend in _friendsService.List().Where(f => f.Favorite))
            {
                await _friendsService.RefreshAsync(friend.SteamId64);
                await Dispatcher.InvokeAsync(() =>
                {
                    // Push the new SessionCache entry into the matching row so it shows live.
                    var row = _friends.FirstOrDefault(r => r.SteamId64 == friend.SteamId64);
                    if (row is not null && _friendsService.SessionCache.TryGetValue(friend.SteamId64, out var cached))
                        row.AttachResult(cached);
                    SyncFriendColumns();
                });
            }
        }
        catch
        {
            // Background best-effort. User can manually refresh from the sidebar.
        }
    }

    /// <summary>
    /// Rebuild the per-friend columns on the Mods grid and the virtual rows underneath. Called
    /// at startup (from cache), whenever the friends window mutates the friend set, and after
    /// each background refresh lands a result.
    /// </summary>
    private void SyncFriendColumns()
    {
        if (_friendsService is null || _block is null) return;

        // Only "show as column" friends populate the grid — everyone else stays in the roster
        // for opt-in via the Manage Friends window.
        var friends = _friendsService.List().Where(f => f.Favorite).ToList();

        // 1. Strip prior friend columns. Each one carries a recognisable header prefix.
        for (int i = ModsGrid.Columns.Count - 1; i >= 0; i--)
        {
            if (ModsGrid.Columns[i].Header is string s && s.StartsWith("👤 ", StringComparison.Ordinal))
                ModsGrid.Columns.RemoveAt(i);
        }

        // 2. Drop prior virtual rows; we re-derive them.
        for (int i = _mods.Count - 1; i >= 0; i--)
            if (_mods[i].IsVirtual) _mods.RemoveAt(i);

        foreach (var row in _mods) row.ClearFriendHas();

        if (friends.Count == 0) return;

        // 3. One DataGridTemplateColumn per friend, each containing a read-only CheckBox bound
        //    via the FriendHasMod indexer.
        foreach (var f in friends)
        {
            var col = new System.Windows.Controls.DataGridTemplateColumn
            {
                Header = "👤 " + (string.IsNullOrWhiteSpace(f.DisplayName) ? f.SteamId64 : f.DisplayName),
                Width = new System.Windows.Controls.DataGridLength(80),
            };
            var dt = new DataTemplate();
            var cb = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
            cb.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,
                new System.Windows.Data.Binding($"FriendHasMod[{f.SteamId64}]") { Mode = System.Windows.Data.BindingMode.OneWay });
            cb.SetValue(System.Windows.Controls.CheckBox.IsHitTestVisibleProperty, false);
            cb.SetValue(System.Windows.Controls.CheckBox.FocusableProperty, false);
            cb.SetValue(System.Windows.Controls.CheckBox.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            dt.VisualTree = cb;
            col.CellTemplate = dt;
            ModsGrid.Columns.Add(col);
        }

        // 4. Populate per-row friend flags from cached scrape results + collect virtual rows.
        var ownedIds = new HashSet<string>(_mods.Where(m => !m.IsVirtual).Select(m => m.Id), StringComparer.Ordinal);
        var virtualByMod = new Dictionary<string, (string Title, string FirstFriend)>(StringComparer.Ordinal);

        foreach (var f in friends)
        {
            if (!_friendsService.SessionCache.TryGetValue(f.SteamId64, out var sub)) continue;
            var modSet = new HashSet<string>(sub.Mods.Select(m => m.Id), StringComparer.Ordinal);

            foreach (var row in _mods.Where(m => !m.IsVirtual))
                row.SetFriendHas(f.SteamId64, modSet.Contains(row.Id));

            foreach (var m in sub.Mods)
            {
                if (ownedIds.Contains(m.Id)) continue;
                if (!virtualByMod.ContainsKey(m.Id))
                    virtualByMod[m.Id] = (m.Title, f.SteamId64);
            }
        }

        // 5. Append virtual rows; populate their friend flags too.
        foreach (var (id, info) in virtualByMod.OrderBy(kv => kv.Value.Title, StringComparer.OrdinalIgnoreCase))
        {
            var row = ModRowViewModel.Virtual(id, info.Title, info.FirstFriend);
            foreach (var f in friends)
            {
                if (_friendsService.SessionCache.TryGetValue(f.SteamId64, out var sub))
                    row.SetFriendHas(f.SteamId64, sub.Mods.Any(m => m.Id == id));
            }
            _mods.Add(row);
        }

        var ghostCount = virtualByMod.Count;
        if (ghostCount > 0)
            SetStatus($"{friends.Count} friend column(s); {ghostCount} mod(s) available from friends that you don't have.");
        else
            SetStatus($"{friends.Count} friend column(s). You have everything they're subscribed to.");
    }

    private async void OnSubscribeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not ModRowViewModel row) return;
        if (!ulong.TryParse(row.Id, out var workshopId))
        {
            SetStatus($"Mod {row.Id}: not a numeric Workshop ID; can't subscribe.");
            return;
        }
        var subscribing = row.IsVirtual;
        b.IsEnabled = false;
        try
        {
            // Prefer the in-app authenticated path (Steam CEF cookies). Falls back to the
            // steam:// overlay URL when cookies are unavailable (Steam not running, user never
            // opened the community in-overlay, App-Bound Encryption etc.).
            var cookieRead = new SteamCefCookieReader().Read();
            if (cookieRead.Outcome == SteamCefCookieOutcome.Ok && cookieRead.Cookies is not null)
            {
                var client = new SteamSubscribeClient(_updateHttp, cookieRead.Cookies);
                var task = subscribing
                    ? client.SubscribeAsync(Vt2Installation.AppId, workshopId)
                    : client.UnsubscribeAsync(Vt2Installation.AppId, workshopId);
                var result = await task;
                if (result.Success)
                {
                    SetStatus($"{(subscribing ? "Subscribed to" : "Unsubscribed from")} {row.Name} via Steam. Reload Mods to sync.");
                    return;
                }
                SetStatus($"In-app {(subscribing ? "subscribe" : "unsubscribe")} failed ({result.Outcome}); opening Steam overlay.");
            }
            else
            {
                SetStatus($"Steam cookies unavailable ({cookieRead.Outcome}); opening Steam overlay.");
            }

            // Fallback: open the Workshop page so the user can click Subscribe themselves.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"steam://url/CommunityFilePage/{row.Id}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Subscribe failed: " + ex.Message);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}
