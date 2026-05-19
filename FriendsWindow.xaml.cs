using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vt2ModManager.Services;
using Vt2ModManager.ViewModels;

namespace Vt2ModManager;

/// <summary>
/// Modeless friends-manager window opened from the Mods tab. Edits the shared friends list and
/// raises <see cref="FriendsChanged"/> whenever the set or per-friend state changes so the parent
/// MainWindow can rebuild its friend columns + virtual rows.
/// </summary>
public partial class FriendsWindow : Window
{
    private readonly FriendsService _service;
    private readonly ObservableCollection<FriendRowViewModel> _rows = new();

    public event EventHandler? FriendsChanged;

    public FriendsWindow(FriendsService service)
    {
        InitializeComponent();
        _service = service;
        FriendsGrid.ItemsSource = _rows;
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        // Unsubscribe from previous rows so old refs don't keep firing into a stale handler.
        foreach (var r in _rows) r.PropertyChanged -= OnRowPropertyChanged;
        _rows.Clear();
        foreach (var f in _service.List())
        {
            var row = new FriendRowViewModel(f);
            if (_service.SessionCache.TryGetValue(f.SteamId64, out var cached))
                row.AttachResult(cached);
            row.PropertyChanged += OnRowPropertyChanged;
            _rows.Add(row);
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FriendRowViewModel.Favorite) && sender is FriendRowViewModel row)
        {
            try { _service.SetFavorite(row.SteamId64, row.Favorite); } catch { /* best effort */ }
            FriendsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetStatus(string s) => StatusLabel.Text = s;

    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var input = (AddBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(input)) { SetStatus("Type a SteamID, vanity, or profile URL."); return; }
        SetStatus($"Resolving '{input}'…");
        try
        {
            var friend = await _service.AddAsync(input);
            if (friend is null) { SetStatus($"Couldn't resolve '{input}'."); return; }
            AddBox.Text = "";
            Reload();
            FriendsChanged?.Invoke(this, EventArgs.Empty);
            SetStatus($"Added {friend.SteamId64}.");
        }
        catch (Exception ex) { SetStatus("Add failed: " + ex.Message); }
    }

    private void OnAddBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAddClicked(sender, new RoutedEventArgs());
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        var ok = MessageBox.Show(this, $"Remove {row.SteamId64}?", "Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _service.Remove(row.SteamId64);
        Reload();
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        _service.ToggleFavorite(row.SteamId64);
        Reload();
        FriendsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (FriendsGrid.SelectedItem is not FriendRowViewModel row) { SetStatus("Pick a friend."); return; }
        SetStatus($"Refreshing {row.SteamId64}…");
        try
        {
            var result = await _service.RefreshAsync(row.SteamId64);
            row.AttachResult(result);
            FriendsChanged?.Invoke(this, EventArgs.Empty);
            SetStatus($"{row.SteamId64}: {result.Visibility}, {result.Mods.Count} mod(s).");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private async void OnRefreshAllClicked(object sender, RoutedEventArgs e)
    {
        SetStatus($"Refreshing {_rows.Count} friends…");
        try
        {
            foreach (var row in _rows)
            {
                var result = await _service.RefreshAsync(row.SteamId64);
                row.AttachResult(result);
            }
            FriendsChanged?.Invoke(this, EventArgs.Empty);
            SetStatus($"Refreshed {_rows.Count} friends.");
        }
        catch (Exception ex) { SetStatus("Refresh failed: " + ex.Message); }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
