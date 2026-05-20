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
    public string OriginBadge => Friend.Origin switch
    {
        "steam"  => "Steam",
        "manual" => "Manual",
        _        => Friend.Origin,
    };

    public string LastFetchedDisplay => Friend.LastFetchedUtc is { } t
        ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "—";

    public string VisibilityText
    {
        get
        {
            if (_last is null) return "—";
            // LoginRequired is more actionable than "Public"/"Private"/etc — surface it first
            // so the sidebar # column doesn't read as "this friend has zero subs" when the real
            // problem is that Steam isn't signed in on this machine.
            if (_last.AuthState == FriendScrapeAuthState.LoginRequired) return "Login";
            return _last.Visibility.ToString();
        }
    }
    public string VisibilityTooltip => _last switch
    {
        null => "Click Refresh to fetch this friend's subscriptions.",
        { AuthState: FriendScrapeAuthState.LoginRequired } =>
            "Steam requires an authenticated session to read another user's subscriptions. Open the Steam client and sign in, then click Refresh.",
        { Visibility: ProfileVisibility.Private } =>
            "This friend's Steam profile is private — subscriptions aren't readable.",
        { Visibility: ProfileVisibility.FriendsOnly, Mods.Count: 0 } =>
            "Friends-only profile. If you're in their friend list and Steam is signed in, click Refresh; otherwise the list will stay empty.",
        { Error: { } err } => err,
        _ => "OK",
    };
    public int? ModCount => _last is null ? null : _last.Mods.Count;
    public string ModCountText => _last is null ? "—" : _last.Mods.Count.ToString(CultureInfo.InvariantCulture);
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{Friend.SteamId64}";

    public void AttachResult(FriendSubscriptionResult? result)
    {
        _last = result;
        OnPropertyChanged(nameof(VisibilityText));
        OnPropertyChanged(nameof(VisibilityTooltip));
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(ModCountText));
        OnPropertyChanged(nameof(LastFetchedDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
