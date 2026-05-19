using System.IO;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

/// <summary>
/// JSON-parsing tests for <see cref="UpdateChecker"/>. The HTTP path and self-update swap
/// are exercised manually against a published GitHub Release — we only cover the parts that
/// would silently rot if the GitHub payload shape changed.
/// </summary>
public sealed class UpdateCheckerTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "update", name);

    [Fact]
    public void ParseLatestRelease_reads_tag_and_asset_with_digest()
    {
        var json = File.ReadAllText(FixturePath("release_with_digest.json"));

        var result = UpdateChecker.ParseLatestRelease(json, "Vt2ModManager.exe");

        Assert.NotNull(result);
        Assert.Equal("0.2.0", result!.LatestVersion); // 'v' stripped
        Assert.Equal(
            "https://github.com/Ensrick/vt2-mod-manager/releases/download/v0.2.0/Vt2ModManager.exe",
            result.DownloadUrl);
        Assert.Equal(75000000L, result.AssetSize);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            result.AssetSha256);
    }

    [Fact]
    public void ParseLatestRelease_returns_null_when_named_asset_absent()
    {
        var json = File.ReadAllText(FixturePath("release_missing_asset.json"));
        var result = UpdateChecker.ParseLatestRelease(json, "Vt2ModManager.exe");
        Assert.Null(result);
    }

    [Fact]
    public void ParseLatestRelease_leaves_sha_null_when_digest_field_missing()
    {
        var json = File.ReadAllText(FixturePath("release_with_sha_sibling.json"));
        var result = UpdateChecker.ParseLatestRelease(json, "Vt2ModManager.exe");

        Assert.NotNull(result);
        Assert.Equal("0.3.1", result!.LatestVersion);
        Assert.Null(result.AssetSha256);
    }

    [Fact]
    public void TryExtractShaSiblingUrl_finds_dot_sha256_asset()
    {
        var json = File.ReadAllText(FixturePath("release_with_sha_sibling.json"));
        var url = UpdateChecker.TryExtractShaSiblingUrl(json, "Vt2ModManager.exe");
        Assert.Equal(
            "https://github.com/Ensrick/vt2-mod-manager/releases/download/v0.3.1/Vt2ModManager.exe.sha256",
            url);
    }

    [Fact]
    public void TryExtractShaSiblingUrl_returns_null_when_no_sibling()
    {
        var json = File.ReadAllText(FixturePath("release_with_digest.json"));
        var url = UpdateChecker.TryExtractShaSiblingUrl(json, "Vt2ModManager.exe");
        Assert.Null(url);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("  0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  Vt2ModManager.exe\n",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void ParseShaFile_accepts_well_formed_digests(string input, string expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseShaFile(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notahex")]
    [InlineData("0123456789abcdef")]                                          // too short
    [InlineData("0123456789abcdefg123456789abcdef0123456789abcdef0123456789abcdef0")] // contains 'g'
    public void ParseShaFile_rejects_malformed_input(string input)
    {
        Assert.Null(UpdateChecker.ParseShaFile(input));
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", UpdateStatus.UpdateAvailable)]
    [InlineData("0.2.0", "0.2.0", UpdateStatus.Latest)]
    [InlineData("0.2.0", "0.1.9", UpdateStatus.Latest)]
    [InlineData("1.0.0", "2.0.0", UpdateStatus.UpdateAvailable)]
    // Pre-release on running side, clean tag on latest → counts as available.
    [InlineData("0.2.0-alpha", "0.2.0", UpdateStatus.UpdateAvailable)]
    [InlineData("0.2.0-alpha", "0.2.0-beta", UpdateStatus.Latest)]
    public void CompareStatus_semver_rules(string current, string latest, UpdateStatus expected)
    {
        Assert.Equal(expected, UpdateChecker.CompareStatus(current, latest));
    }

    [Fact]
    public void CompareStatus_unparseable_returns_check_failed()
    {
        Assert.Equal(UpdateStatus.CheckFailed, UpdateChecker.CompareStatus("garbage", "0.1.0"));
        Assert.Equal(UpdateStatus.CheckFailed, UpdateChecker.CompareStatus("0.1.0", "garbage"));
    }
}
