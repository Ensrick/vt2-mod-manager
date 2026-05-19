using System;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class SteamFriendsResolverTests
{
    [Fact]
    public void ParseLocalConfig_extracts_friends_and_skips_self_and_scalars()
    {
        // Trimmed snapshot of a real localconfig.vdf shape: the friends sub-object holds the
        // logged-in user (key matches selfAccountId), some scalar metadata keys, and the rest
        // are friend entries keyed by accountid32.
        var src = """
            "UserLocalConfigStore"
            {
                "friends"
                {
                    "250855163"
                    {
                        "name" "Ensrick"
                        "avatar" "44b9d2eb"
                    }
                    "PersonaName" "Ensrick"
                    "communitypreferences" "189cd"
                    "107010611"
                    {
                        "name" "Silent Pockets"
                        "avatar" "83131420"
                    }
                    "121946418"
                    {
                        "name" "ramos-"
                        "avatar" "76f0e8f6"
                    }
                }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-friends-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, src);
        try
        {
            var friends = SteamFriendsResolver.ParseLocalConfig(tmp, selfAccountId: 250855163).ToList();
            Assert.Equal(2, friends.Count);
            Assert.Contains(friends, f => f.PersonaName == "Silent Pockets" && f.AccountId32 == 107010611u);
            Assert.Contains(friends, f => f.PersonaName == "ramos-"         && f.AccountId32 == 121946418u);
            Assert.DoesNotContain(friends, f => f.PersonaName == "Ensrick");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void ParseLocalConfig_reads_fixture_on_disk()
    {
        // Confirms the fixture-on-disk path used by Resolve() works end-to-end via the
        // shipped tests\fixtures\steam-friends\localconfig_sample.vdf snapshot.
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "steam-friends", "localconfig_sample.vdf");
        Assert.True(File.Exists(path), $"Fixture not copied to output: {path}");

        var friends = SteamFriendsResolver.ParseLocalConfig(path, selfAccountId: 250855163).ToList();
        Assert.Equal(2, friends.Count);
        Assert.All(friends, f => Assert.False(string.IsNullOrEmpty(f.SteamId64)));
        Assert.All(friends, f => Assert.StartsWith("765611", f.SteamId64));
    }

    [Fact]
    public void AccountIdToSteamId64_matches_documented_offset()
    {
        // The user's own account id 250855163 → SteamID64 76561198211120891 (memory-of-record).
        Assert.Equal("76561198211120891", SteamFriendsResolver.AccountIdToSteamId64(250855163));
        // Boundary check: accountid 1 → 76561197960265729.
        Assert.Equal("76561197960265729", SteamFriendsResolver.AccountIdToSteamId64(1));
    }

    [Fact]
    public void ParseLocalConfig_returns_empty_when_file_missing()
    {
        var friends = SteamFriendsResolver.ParseLocalConfig("Z:\\does-not-exist.vdf", 0).ToList();
        Assert.Empty(friends);
    }

    [Fact]
    public void ParseLocalConfig_returns_empty_when_no_friends_node()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-friends-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, "\"UserLocalConfigStore\" { \"something_else\" { } }");
        try
        {
            Assert.Empty(SteamFriendsResolver.ParseLocalConfig(tmp, 0));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_returns_empty_when_userdata_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var resolver = new SteamFriendsResolver(dir);
            Assert.Empty(resolver.Resolve());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Resolve_picks_friends_from_account_dirs_and_dedupes_across_accounts()
    {
        // Build a fake SteamRoot with two account dirs, each holding their own
        // localconfig.vdf. The friend "Shared" appears in both — we expect a single entry.
        var root = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dirA = Path.Combine(root, "userdata", "111", "config");
            var dirB = Path.Combine(root, "userdata", "222", "config");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            File.WriteAllText(Path.Combine(dirA, "localconfig.vdf"), """
                "UserLocalConfigStore"
                {
                    "friends"
                    {
                        "111" { "name" "self-a" }
                        "500" { "name" "Shared" }
                        "501" { "name" "OnlyA" }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(dirB, "localconfig.vdf"), """
                "UserLocalConfigStore"
                {
                    "friends"
                    {
                        "222" { "name" "self-b" }
                        "500" { "name" "Shared" }
                        "502" { "name" "OnlyB" }
                    }
                }
                """);

            var resolver = new SteamFriendsResolver(root);
            var friends = resolver.Resolve();

            // Self entries are skipped in their own account dir; friend "Shared" deduped.
            var names = friends.Select(f => f.PersonaName).ToList();
            Assert.Contains("OnlyA", names);
            Assert.Contains("OnlyB", names);
            Assert.Single(names, n => n == "Shared");
            // Sorted alphabetically (case-insensitive).
            Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_skips_non_numeric_account_dirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var garbage = Path.Combine(root, "userdata", "ac", "config");
            Directory.CreateDirectory(garbage);
            File.WriteAllText(Path.Combine(garbage, "localconfig.vdf"), """
                "UserLocalConfigStore" { "friends" { "999" { "name" "ShouldBeIgnored" } } }
                """);

            var resolver = new SteamFriendsResolver(root);
            Assert.Empty(resolver.Resolve());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
