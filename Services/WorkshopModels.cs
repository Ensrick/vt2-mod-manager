namespace Vt2ModManager.Services;

/// <summary>
/// Local Workshop item state from `appworkshop_&lt;appid&gt;.acf`.
/// </summary>
public sealed record WorkshopItemLocal(
    uint   AppId,
    ulong  PublishedFileId,
    long   LocalTimeUpdated,
    string LocalManifest,
    long   LocalSizeBytes);

/// <summary>
/// Remote Workshop item state from the Steam Web API.
/// Nullable fields stay nullable when the API returns result=9 (friends-only / private).
/// </summary>
public sealed record WorkshopItemRemote(
    ulong   PublishedFileId,
    string? Title,
    long?   RemoteTimeUpdated,
    long?   RemoteSizeBytes,
    int?    Visibility,
    bool?   Banned,
    int     ApiResult,
    int?    FileType = null,
    uint?   ConsumerAppId = null);

public enum FreshnessStatus
{
    Current,
    Stale,
    Unknown,
    Removed,
    ApiFailed,
}
