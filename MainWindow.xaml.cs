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
    private readonly ObservableCollection<ProfileRowViewModel> _profiles = new();
    private readonly ObservableCollection<ConflictRowViewModel> _conflicts = new();
    private FriendsService? _friendsService;
    private FriendsWindow? _friendsWindow;

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

        _updateChecker   = new UpdateChecker(_updateHttp);
        _updateInstaller = new UpdateInstaller(_updateHttp);

        Loaded += (_, _) =>
        {
            InitialLoad();
            // Fire-and-forget. Dev builds short-circuit so `dotnet run` stays silent.
            if (!Program.IsDevBuild()) _ = CheckForUpdatesAsync();
        };
    }

    private static HttpClient BuildUpdateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Vt2ModManager/{Program.Version}");
        return c;
    }

    private void InitialLoad()
    {
        try
        {
            _settings = _settingsStore.Load();
            _friendsService = new FriendsService(_updateHttp);
            ReloadMods();
            ReloadProfiles();
            // Rebuild friend columns from cached results on every launch, then kick a background
            // refresh so live state replaces the cached values without blocking startup.
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
        _mods.Clear();
        for (int i = 0; i < _block.Entries.Count; i++)
            _mods.Add(new ModRowViewModel(_block.Entries[i], i + 1));

        SetStatus($"Loaded {_mods.Count} mods from {path}.");
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
        try
        {
            // The underlying ModEntry list inside _block was reordered in-place by Move*; just write.
            var locator = new UserSettingsConfigLocator(_settings);
            _writer.WriteFile(locator.Resolve(), _block);
            SetStatus($"Saved {_block.Entries.Count} mods. Backup at user_settings.config.bak.");
        }
        catch (Exception ex)
        {
            SetStatus("Save failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        _mods.Clear();
        for (int i = 0; i < _block.Entries.Count; i++)
            _mods.Add(new ModRowViewModel(_block.Entries[i], i + 1));
        var msg = $"Auto-sorted (unsaved). Rules at {LoadOrderRules.DefaultPath()}.";
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
        _block.Entries.Clear();
        var order = 0;
        for (int i = 0; i < _mods.Count; i++)
        {
            if (_mods[i].IsVirtual) { _mods[i].Order = 0; continue; }
            _mods[i].Order = ++order;
            _block.Entries.Add(_mods[i].Entry);
        }
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
        // Refresh the mods grid from the (now-reordered) block.
        _mods.Clear();
        for (int i = 0; i < _block.Entries.Count; i++)
            _mods.Add(new ModRowViewModel(_block.Entries[i], i + 1));
        SetStatus($"Applied '{row.Name}': {result.ToggledCount} toggled, {result.Missing.Count} missing, {result.Extras.Count} extras appended. Click Save to commit.");
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

    private void OnManageFriendsClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) { SetStatus("Friends not initialized."); return; }
        if (_friendsWindow is not null && _friendsWindow.IsLoaded)
        {
            _friendsWindow.Activate();
            return;
        }
        _friendsWindow = new FriendsWindow(_friendsService) { Owner = this };
        _friendsWindow.FriendsChanged += (_, _) => SyncFriendColumns();
        _friendsWindow.Closed += (_, _) => _friendsWindow = null;
        _friendsWindow.Show();
    }

    private async System.Threading.Tasks.Task BackgroundRefreshFriendsAsync()
    {
        if (_friendsService is null) return;
        try
        {
            foreach (var friend in _friendsService.List())
            {
                await _friendsService.RefreshAsync(friend.SteamId64);
                // Refresh columns incrementally as each friend lands.
                await Dispatcher.InvokeAsync(SyncFriendColumns);
            }
        }
        catch
        {
            // Background best-effort. Don't surface — the user can hit "Manage friends" → "Refresh all" if needed.
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

        var friends = _friendsService.List();

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

    private void OnSubscribeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not ModRowViewModel row) return;
        // `steam://url/CommunityFilePage/<id>` opens the Workshop page inside Steam (overlay if
        // a game is running, the client otherwise). Steam itself renders the contextually-correct
        // Subscribe/Unsubscribe button. No reliable URL scheme exists to toggle subscription
        // without that user click.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"steam://url/CommunityFilePage/{row.Id}",
                UseShellExecute = true,
            });
            var verb = row.IsVirtual ? "Subscribe" : "Unsubscribe";
            SetStatus($"Opened Workshop page for {row.Name}. Click {verb} in Steam, then reload here.");
        }
        catch (Exception ex) { SetStatus("Open failed: " + ex.Message); }
    }
}
