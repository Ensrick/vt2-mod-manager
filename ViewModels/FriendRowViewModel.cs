using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vt2ModManager.Services;

namespace Vt2ModManager.ViewModels;

/// <summary>
/// One row on the Friends tab. Backs a <see cref="Friend"/> + the most recent
/// <see cref="FriendSubscriptionResult"/> from the session cache (null until refreshed).
/// </summary>
public sealed class FriendRowViewModel : INotifyPropertyChanged
{
    private FriendSubscriptionResult? _last;

    public FriendRowViewModel(Friend friend) { Friend = friend; }

    public Friend Friend { get; }

    public string SteamId64 => Friend.SteamId64;
    public string DisplayName => Friend.DisplayName;

    public bool Favorite
    {
        get => Friend.Favorite;
        set
        {
            if (Friend.Favorite != value) { Friend.Favorite = value; OnPropertyChanged(); OnPropertyChanged(nameof(FavoriteIcon)); }
        }
    }
    public string FavoriteIcon => Friend.Favorite ? "★" : "";

    public string LastFetchedDisplay => Friend.LastFetchedUtc is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "—";

    public string VisibilityText => _last?.Visibility.ToString() ?? "—";
    public int? ModCount => _last is null ? null : _last.Mods.Count;
    public string ModCountText => _last is null ? "—" : _last.Mods.Count.ToString(CultureInfo.InvariantCulture);
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{Friend.SteamId64}";

    public void AttachResult(FriendSubscriptionResult? result)
    {
        _last = result;
        OnPropertyChanged(nameof(VisibilityText));
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(ModCountText));
        OnPropertyChanged(nameof(LastFetchedDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
