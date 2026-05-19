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
    private readonly ObservableCollection<FriendRowViewModel> _friends = new();
    private FriendsService? _friendsService;

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
            ReloadFriends();
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

    private void ReloadFriends()
    {
        if (_friendsService is null) return;
        _friends.Clear();
        foreach (var f in _friendsService.List())
            _friends.Add(new FriendRowViewModel(f));
    }

    private async void OnAddFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        var input = (AddFriendBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) { SetStatus("Type a SteamID, vanity, or profile URL first."); return; }
        SetStatus($"Resolving '{input}'…");
        try
        {
            var friend = await _friendsService.AddAsync(input);
            if (friend is null) { SetStatus($"Couldn't resolve '{input}' to a Steam profile."); return; }
            AddFriendBox.Text = "";
            ReloadFriends();
            SetStatus($"Added {friend.SteamId64}. Click 'Sync columns' to surface their mods.");
        }
        catch (Exception ex) { SetStatus("Add failed: " + ex.Message); }
    }

    private void OnRemoveFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        var ok = MessageBox.Show(this, $"Remove {row.SteamId64}?", "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _friendsService.Remove(row.SteamId64);
        ReloadFriends();
        SyncFriendColumns(); // also drops the friend's column from the Mods tab
    }

    private void OnToggleFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        _friendsService.ToggleFavorite(row.SteamId64);
        ReloadFriends();
    }

    private async void OnRefreshFriendClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        SetStatus($"Refreshing {row.SteamId64}…");
        try
        {
            var result = await _friendsService.RefreshAsync(row.SteamId64);
            row.AttachResult(result);
            SetStatus($"{row.SteamId64}: {result.Visibility}, {result.Mods.Count} mod(s).");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private async void OnRefreshAllFriendsClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) return;
        SetStatus($"Refreshing {_friends.Count} friends…");
        try
        {
            foreach (var row in _friends)
            {
                var result = await _friendsService.RefreshAsync(row.SteamId64);
                row.AttachResult(result);
            }
            SetStatus($"Refreshed {_friends.Count} friends.");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private async void OnSyncFriendColumnsClicked(object sender, RoutedEventArgs e)
    {
        if (_friendsService is null) { SetStatus("Friends not initialized."); return; }
        if (_block is null) { SetStatus("Load mods first."); return; }

        // Refresh any friend who hasn't been pulled in this session yet, so columns reflect
        // live state on the first Sync click after launch.
        try
        {
            foreach (var row in _friends)
            {
                if (!_friendsService.SessionCache.ContainsKey(row.SteamId64))
                {
                    var result = await _friendsService.RefreshAsync(row.SteamId64);
                    row.AttachResult(result);
                }
            }
        }
        catch (Exception ex) { SetStatus("Refresh-during-sync failed: " + ex.Message); return; }

        SyncFriendColumns();
    }

    /// <summary>
    /// Programmatically rebuild the per-friend columns on the Mods grid and append virtual rows
    /// for mods any friend has but we don't. Called when the friend set or fetch results change.
    /// </summary>
    private void SyncFriendColumns()
    {
        if (_friendsService is null || _block is null) return;

        // 1. Strip prior friend columns (everything beyond the static set).
        // The static layout from XAML is fixed; we drop anything we tagged below.
        for (int i = ModsGrid.Columns.Count - 1; i >= 0; i--)
        {
            if (ModsGrid.Columns[i].Header is string s && s.StartsWith("👤 ", StringComparison.Ordinal))
                ModsGrid.Columns.RemoveAt(i);
        }

        // 2. Remove any prior virtual rows (we'll re-add fresh ones below).
        for (int i = _mods.Count - 1; i >= 0; i--)
            if (_mods[i].IsVirtual) _mods.RemoveAt(i);

        // 3. Clear per-row friend flags so a removed friend's column doesn't leak data.
        foreach (var row in _mods) row.ClearFriendHas();

        if (_friends.Count == 0)
        {
            SetStatus("No friends added.");
            return;
        }

        // 4. Add one new column per friend, bound via the FriendHasMod indexer.
        foreach (var f in _friends)
        {
            var col = new System.Windows.Controls.DataGridTemplateColumn
            {
                Header = "👤 " + (string.IsNullOrWhiteSpace(f.DisplayName) ? f.SteamId64 : f.DisplayName),
                Width = new System.Windows.Controls.DataGridLength(80),
            };
            var dt = new DataTemplate();
            var tb = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
            tb.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
                new System.Windows.Data.Binding($"FriendHasMod[{f.SteamId64}]")
                {
                    Converter = new BoolToCheckConverter(),
                });
            tb.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            tb.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, System.Windows.FontWeights.Bold);
            dt.VisualTree = tb;
            col.CellTemplate = dt;
            ModsGrid.Columns.Add(col);
        }

        // 5. Populate per-row friend flags + collect "friend has but I don't" IDs.
        var ownedIds = new HashSet<string>(_mods.Where(m => !m.IsVirtual).Select(m => m.Id), StringComparer.Ordinal);
        var virtualByMod = new Dictionary<string, (string Title, string FirstFriend)>(StringComparer.Ordinal);

        foreach (var f in _friends)
        {
            if (!_friendsService.SessionCache.TryGetValue(f.SteamId64, out var sub)) continue;
            var modSet = sub.Mods.ToDictionary(m => m.Id, m => m.Title);

            foreach (var row in _mods.Where(m => !m.IsVirtual))
                row.SetFriendHas(f.SteamId64, modSet.ContainsKey(row.Id));

            foreach (var m in sub.Mods)
            {
                if (ownedIds.Contains(m.Id)) continue;
                if (!virtualByMod.ContainsKey(m.Id))
                    virtualByMod[m.Id] = (m.Title, f.SteamId64);
            }
        }

        // 6. Append virtual rows for mods we don't have.
        foreach (var (id, info) in virtualByMod.OrderBy(kv => kv.Value.Title, StringComparer.OrdinalIgnoreCase))
        {
            var row = ModRowViewModel.Virtual(id, info.Title, info.FirstFriend);
            // Friend flags for this virtual row too — show every friend that has it.
            foreach (var f in _friends)
            {
                if (_friendsService.SessionCache.TryGetValue(f.SteamId64, out var sub))
                    row.SetFriendHas(f.SteamId64, sub.Mods.Any(m => m.Id == id));
            }
            _mods.Add(row);
        }

        var stale = _mods.Count(m => m.FreshnessIcon == "⚠");
        SetStatus($"Synced {_friends.Count} friend column(s). {virtualByMod.Count} mod(s) you don't have available to subscribe. {stale} stale among yours.");
    }

    private void OnSubscribeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.DataContext is not ModRowViewModel row) return;
        // Steam overlay URL — opens the Workshop page in-client if Steam's running, browser otherwise.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"steam://url/CommunityFilePage/{row.Id}",
                UseShellExecute = true,
            });
            SetStatus($"Opened Workshop page for {row.Name} in Steam. Click Subscribe there, then 'Sync columns' to refresh.");
        }
        catch (Exception ex) { SetStatus("Open failed: " + ex.Message); }
    }
}

/// <summary>WPF binding helper — turns a bool into the ✓ / blank glyph used by per-friend columns.</summary>
public sealed class BoolToCheckConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is bool b && b ? "✓" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
