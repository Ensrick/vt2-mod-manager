using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vt2ModManager.Services;

namespace Vt2ModManager.ViewModels;

/// <summary>
/// One row in the mods DataGrid. Backs a single <see cref="ModEntry"/> — the Enabled toggle
/// flips the underlying entry directly so a Save writes the live state without any extra
/// reconciliation step.
/// </summary>
public sealed class ModRowViewModel : INotifyPropertyChanged
{
    private readonly ModEntry _entry;
    private int _order;

    public ModRowViewModel(ModEntry entry, int order)
    {
        _entry = entry;
        _order = order;
    }

    /// <summary>
    /// Factory for a "ghost" row representing a mod a friend has but the user does not. Backed
    /// by a synthetic <see cref="ModEntry"/> that never gets written back to user_settings.config —
    /// the Save handler filters on <see cref="IsVirtual"/>.
    /// </summary>
    public static ModRowViewModel Virtual(string workshopId, string title, string sourceFriendSid)
    {
        var entry = new ModEntry();
        entry.Fields.Add(new("id",      new RawValue.StringValue(workshopId)));
        entry.Fields.Add(new("name",    new RawValue.StringValue(title)));
        entry.Fields.Add(new("enabled", new RawValue.BoolValue(false)));
        var row = new ModRowViewModel(entry, 0) { IsVirtual = true, SourceFriendSid = sourceFriendSid };
        return row;
    }

    public bool IsVirtual { get; private init; }
    public string? SourceFriendSid { get; private init; }
    /// <summary>"Subscribe" for friend-only ghost rows, "Unsubscribe" for mods you have installed.</summary>
    public string SubscribeButtonLabel => IsVirtual ? "Subscribe" : "Unsubscribe";

    public ModEntry Entry => _entry;

    // Per-friend "has this mod?" map. WPF binds via `FriendHasMod[<sid>]` indexer syntax.
    public Dictionary<string, bool> FriendHasMod { get; } = new();
    public void SetFriendHas(string sid, bool has)
    {
        FriendHasMod[sid] = has;
        OnPropertyChanged("FriendHasMod[]");
    }
    public void ClearFriendHas()
    {
        FriendHasMod.Clear();
        OnPropertyChanged("FriendHasMod[]");
    }

    public int Order
    {
        get => _order;
        set { if (_order != value) { _order = value; OnPropertyChanged(); } }
    }

    public string Id => _entry.Id;
    public string Name => _entry.Name;
    public string Author => _entry.Author;
    public string LastUpdated => _entry.LastUpdated;
    public string Status => _entry.Sanctioned ? "Sanctioned" : (_entry.OutOfDate ? "Out of date" : "Modded");
    public int NumChildren => _entry.NumChildren;
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={_entry.Id}";

    // Local install state. Populated by MainWindow at row creation time:
    //   "Downloaded" — appworkshop_552500.acf has the item on disk
    //   "Pending"    — Steam is subscribed (per <appid>_subscriptions.vdf) but hasn't finished
    //                  pulling the bundle yet; the mod will fail to load until Steam catches up
    //   ""           — unknown (Steam couldn't be resolved, or this is a virtual friend-ghost row)
    // The Mods grid surfaces this in its own column so users notice queued downloads instead of
    // assuming a "missing" mod has been unsubscribed.
    private string _localState = "";
    public string LocalState
    {
        get => _localState;
        set
        {
            if (_localState == value) return;
            _localState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LocalStateTooltip));
        }
    }
    public string LocalStateTooltip => LocalState switch
    {
        "Downloaded" => "Steam has this mod's bundle on disk.",
        "Pending"    => "Steam is subscribed to this mod but the bundle hasn't finished downloading. "
                      + "Launch Steam and let it catch up, or check Steam's Downloads pane.",
        _            => "",
    };

    // Workshop-state columns, populated by "Refresh from Workshop". Empty until then.
    private WorkshopItemLocal? _local;
    private WorkshopItemRemote? _remote;
    public string? LocalFolderPath { get; private set; }

    public void AttachWorkshopData(WorkshopItemLocal? local, WorkshopItemRemote? remote, string? folderPath)
    {
        _local = local;
        _remote = remote;
        LocalFolderPath = folderPath;
        OnPropertyChanged(nameof(LocalSizeText));
        OnPropertyChanged(nameof(SizeMatchIcon));
        OnPropertyChanged(nameof(SizeMatchTooltip));
        OnPropertyChanged(nameof(FreshnessIcon));
        OnPropertyChanged(nameof(FreshnessTooltip));
    }

    public string LocalSizeText => FormatSize(_local?.LocalSizeBytes);

    /// <summary>"✓" matched, "⚠" mismatch, "?" unknown (no remote data yet or friends-only).</summary>
    public string SizeMatchIcon
    {
        get
        {
            if (_local is null || _remote is null) return "";
            if (_remote.ApiResult != 1 || _remote.RemoteSizeBytes is null) return "?";
            return _remote.RemoteSizeBytes == _local.LocalSizeBytes ? "✓" : "⚠";
        }
    }

    public string SizeMatchTooltip
    {
        get
        {
            if (_local is null || _remote is null) return "Click 'Refresh from Workshop' to populate.";
            if (_remote.ApiResult == 9) return "Friends-only / private — Steam Web API returns no size.";
            if (_remote.ApiResult != 1 || _remote.RemoteSizeBytes is null) return "Steam API didn't return a size.";
            var match = _remote.RemoteSizeBytes == _local.LocalSizeBytes;
            return match
                ? $"Match: local {_local.LocalSizeBytes:N0} = remote {_remote.RemoteSizeBytes:N0} bytes."
                : $"Mismatch: local {_local.LocalSizeBytes:N0} ≠ remote {_remote.RemoteSizeBytes:N0} bytes. Refresh in Steam.";
        }
    }

    /// <summary>"✓" up to date, "⚠" stale (local older than remote), "?" unknown.</summary>
    public string FreshnessIcon
    {
        get
        {
            if (_local is null || _remote is null) return "";
            if (_remote.ApiResult != 1 || _remote.RemoteTimeUpdated is null) return "?";
            return _local.LocalTimeUpdated >= _remote.RemoteTimeUpdated ? "✓" : "⚠";
        }
    }

    public string FreshnessTooltip
    {
        get
        {
            if (_local is null || _remote is null) return "Click 'Refresh from Workshop' to populate.";
            if (_remote.ApiResult == 9) return "Friends-only / private — can't compare timestamps without an API key.";
            if (_remote.ApiResult != 1 || _remote.RemoteTimeUpdated is null) return "Steam API didn't return a timestamp.";
            var localUtc = DateTimeOffset.FromUnixTimeSeconds(_local.LocalTimeUpdated).UtcDateTime;
            var remoteUtc = DateTimeOffset.FromUnixTimeSeconds(_remote.RemoteTimeUpdated.Value).UtcDateTime;
            return _local.LocalTimeUpdated >= _remote.RemoteTimeUpdated
                ? $"Up to date. Local: {localUtc:u}. Remote: {remoteUtc:u}."
                : $"STALE. Local: {localUtc:u}. Remote: {remoteUtc:u}. Refresh in Steam.";
        }
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null || bytes <= 0) return "—";
        var b = bytes.Value;
        if (b < 1024)       return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / 1024.0 / 1024.0:F1} MB";
    }

    public bool MatchesFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return
            Name.Contains(filter,   System.StringComparison.OrdinalIgnoreCase) ||
            Author.Contains(filter, System.StringComparison.OrdinalIgnoreCase) ||
            Id.Contains(filter,     System.StringComparison.OrdinalIgnoreCase);
    }

    public bool Enabled
    {
        get => _entry.Enabled;
        set
        {
            if (_entry.Enabled != value)
            {
                _entry.Enabled = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
