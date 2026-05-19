using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Vt2ModManager.Services;

public enum UpdateStatus
{
    Latest,
    UpdateAvailable,
    CheckFailed,
}

/// <summary>
/// One snapshot of an update-availability check. Persisted to disk as JSON so we can
/// short-circuit subsequent launches inside the cache TTL.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    long? AssetSize,
    string? AssetSha256,
    string? ErrorMessage,
    DateTime CheckedAtUtc);

/// <summary>
/// Polls the GitHub Releases API for the latest Vt2ModManager release and compares the
/// tag against the running version. Disk-caches the last successful result for 6h so we
/// don't hammer the API on every launch. Failed checks aren't cached — next launch retries.
///
/// Asset selection: looks for a release asset named exactly <c>Vt2ModManager.exe</c>. SHA-256
/// is sourced from GitHub's per-asset <c>digest</c> field (<c>sha256:&lt;hex&gt;</c>) when
/// present; otherwise we fall back to a sibling <c>.sha256</c> asset whose contents are the
/// hex digest (optionally followed by whitespace + filename, sha256sum-style).
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultReleasesUrl =
        "https://api.github.com/repos/Ensrick/vt2-mod-manager/releases/latest";

    public const string DefaultAssetName = "Vt2ModManager.exe";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _releasesUrl;
    private readonly string _assetName;

    public UpdateChecker(
        HttpClient http,
        string? cachePath = null,
        string? releasesUrl = null,
        string? assetName = null)
    {
        _http = http;
        _cachePath = cachePath ?? DefaultCachePath();
        _releasesUrl = releasesUrl ?? DefaultReleasesUrl;
        _assetName = assetName ?? DefaultAssetName;
    }

    public static string DefaultCachePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Vt2ModManager", "update-cache.json");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            var cached = TryLoadCache();
            if (cached is not null
                && DateTime.UtcNow - cached.CheckedAtUtc < CacheTtl
                // Cache was written by an older install. After a self-update the previous
                // build cached "{current: X, latest: Y}" then installed Y, and the new
                // build's launch must not just re-use that cache, because a NEWER release
                // (Z > Y) may have shipped inside the 6h TTL window. Force a fresh poll
                // whenever the running binary's version doesn't match what wrote the cache.
                && string.Equals(cached.CurrentVersion, currentVersion, StringComparison.Ordinal))
            {
                // Cached result was computed against whatever version was running back then —
                // re-evaluate against the version we're running *now* so a just-installed
                // exe doesn't keep seeing "update available" for the rest of the TTL window.
                return RecomputeStatus(cached, currentVersion);
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Failed(currentVersion, $"HTTP {(int)resp.StatusCode}");
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var parsed = ParseLatestRelease(json, _assetName);
            if (parsed is null)
            {
                return Failed(currentVersion, $"release payload missing '{_assetName}' asset");
            }

            // Fall back to a sibling .sha256 file if the digest wasn't embedded.
            string? sha256 = parsed.AssetSha256;
            if (string.IsNullOrEmpty(sha256))
            {
                var shaUrl = TryExtractShaSiblingUrl(json, _assetName);
                if (!string.IsNullOrEmpty(shaUrl))
                {
                    sha256 = await TryDownloadShaTextAsync(shaUrl!, ct).ConfigureAwait(false);
                }
            }

            var result = parsed with
            {
                CurrentVersion = currentVersion,
                AssetSha256 = sha256,
                Status = CompareStatus(currentVersion, parsed.LatestVersion!),
                CheckedAtUtc = DateTime.UtcNow,
            };

            TrySaveCache(result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed(currentVersion, ex.Message);
        }
    }

    private static UpdateCheckResult Failed(string current, string msg) =>
        new(UpdateStatus.CheckFailed, current, null, null, null, null, msg, DateTime.UtcNow);

    /// <summary>
    /// Parses one GitHub <c>/releases/latest</c> payload. Returns null if no asset named
    /// <paramref name="assetName"/> is present. The <c>Status</c> and <c>CurrentVersion</c>
    /// fields on the returned record are placeholders — the caller fills those in.
    /// </summary>
    public static UpdateCheckResult? ParseLatestRelease(string json, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        var tag = tagEl.GetString();
        if (string.IsNullOrEmpty(tag)) return null;
        var version = tag.TrimStart('v', 'V');

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            long? size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            // GitHub returns "sha256:<hex>" — strip the prefix so callers can hex-compare.
            string? sha256 = !string.IsNullOrEmpty(digest)
                && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest.Substring(7).ToLowerInvariant()
                    : null;

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable,
                CurrentVersion: "",
                LatestVersion: version,
                DownloadUrl: url,
                AssetSize: size,
                AssetSha256: sha256,
                ErrorMessage: null,
                CheckedAtUtc: DateTime.UtcNow);
        }
        return null;
    }

    /// <summary>
    /// Locate the <c>browser_download_url</c> for a sibling <c>&lt;assetName&gt;.sha256</c>
    /// asset, if the release ships one. Used as a fallback when GitHub didn't compute the
    /// per-asset digest (typical for releases uploaded via the web UI rather than the API).
    /// </summary>
    public static string? TryExtractShaSiblingUrl(string json, string assetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            var target = assetName + ".sha256";
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) continue;
                return asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            }
        }
        catch { /* best effort */ }
        return null;
    }

    /// <summary>
    /// Parse a sha256sum-style file body: optional leading whitespace, 64-hex-char digest,
    /// optional trailing whitespace + filename. Returns the lowercase hex digest, or null.
    /// </summary>
    public static string? ParseShaFile(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var trimmed = content.Trim();
        var token = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return null;
        if (token.Length != 64) return null;
        for (int i = 0; i < token.Length; i++)
        {
            var c = token[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }
        return token.ToLowerInvariant();
    }

    private async Task<string?> TryDownloadShaTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseShaFile(body);
        }
        catch { return null; }
    }

    /// <summary>
    /// semver-ish compare: split each side on '-' first, parse the numeric prefix with
    /// <see cref="Version.TryParse(string?, out Version)"/>. If the numeric prefixes tie,
    /// a pre-release suffix (e.g. <c>-alpha</c>) on the running version with a clean tag on
    /// the latest counts as "update available" — that's how we get folks off the alpha.
    /// </summary>
    public static UpdateStatus CompareStatus(string current, string latest)
    {
        if (!TryParseSemverPrefix(current, out var cv)) return UpdateStatus.CheckFailed;
        if (!TryParseSemverPrefix(latest,  out var lv)) return UpdateStatus.CheckFailed;
        if (lv > cv) return UpdateStatus.UpdateAvailable;
        if (lv == cv && current.Contains('-') && !latest.Contains('-')) return UpdateStatus.UpdateAvailable;
        return UpdateStatus.Latest;
    }

    private static bool TryParseSemverPrefix(string s, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var prefix = s.Split('-', 2)[0];
        return Version.TryParse(prefix, out version!);
    }

    // ---- cache ----
    private UpdateCheckResult? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOpts);
        }
        catch { return null; }
    }

    private void TrySaveCache(UpdateCheckResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(result, JsonOpts));
        }
        catch { /* best effort — cache is a perf optimization, not load-bearing */ }
    }

    private static UpdateCheckResult RecomputeStatus(UpdateCheckResult cached, string currentVersion)
    {
        if (string.IsNullOrEmpty(cached.LatestVersion)) return cached with { CurrentVersion = currentVersion };
        return cached with
        {
            CurrentVersion = currentVersion,
            Status = CompareStatus(currentVersion, cached.LatestVersion),
        };
    }
}
