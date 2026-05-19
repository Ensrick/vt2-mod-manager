using System;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class SteamSubscriptionsResolverTests
{
    [Fact]
    public void ParseSubscriptionsFile_extracts_published_file_ids()
    {
        var src = """
            "subscribedfiles"
            {
                "appid" "552500"
                "time_last_updated" "1779220710"
                "0" { "publishedfileid" "2449540920" "time_subscribed" "1779171563" "disabled_locally" "0" }
                "1" { "publishedfileid" "2490558925" "time_subscribed" "1779171553" "disabled_locally" "0" }
                "2" { "publishedfileid" "2582671762" "time_subscribed" "1779171544" "disabled_locally" "0" }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-subs-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, src);
        try
        {
            var ids = SteamSubscriptionsResolver.ParseSubscriptionsFile(tmp).ToList();
            Assert.Equal(3, ids.Count);
            Assert.Contains("2449540920", ids);
            Assert.Contains("2490558925", ids);
            Assert.Contains("2582671762", ids);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ParseSubscriptionsFile_skips_locally_disabled_entries()
    {
        // disabled_locally != "0" means the user manually toggled it off in Steam's UI.
        // For the Mods tab we treat that the same as "not subscribed" — otherwise users
        // would see ghost rows for things they intentionally disabled.
        var src = """
            "subscribedfiles"
            {
                "appid" "552500"
                "0" { "publishedfileid" "1111" "disabled_locally" "0" }
                "1" { "publishedfileid" "2222" "disabled_locally" "1" }
                "2" { "publishedfileid" "3333" }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-subs-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, src);
        try
        {
            var ids = SteamSubscriptionsResolver.ParseSubscriptionsFile(tmp).ToList();
            Assert.Contains("1111", ids);
            Assert.Contains("3333", ids);                  // missing disabled_locally treated as enabled
            Assert.DoesNotContain("2222", ids);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ParseSubscriptionsFile_skips_scalar_metadata_keys()
    {
        // The "appid" and "time_last_updated" keys sit alongside the numeric ordinals but
        // are scalars, not nested objects. Make sure the parser doesn't try to read
        // publishedfileid off them.
        var src = """
            "subscribedfiles"
            {
                "appid" "552500"
                "time_last_updated" "1779220710"
                "0" { "publishedfileid" "9999" "disabled_locally" "0" }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-subs-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, src);
        try
        {
            var ids = SteamSubscriptionsResolver.ParseSubscriptionsFile(tmp).ToList();
            Assert.Single(ids);
            Assert.Equal("9999", ids[0]);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ParseSubscriptionsFile_returns_empty_for_missing_file()
    {
        Assert.Empty(SteamSubscriptionsResolver.ParseSubscriptionsFile("Z:\\nope.vdf"));
    }

    [Fact]
    public void ParseSubscriptionsFile_returns_empty_for_malformed_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vt2mm-subs-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, "this is not even close to vdf {{{ ");
        try
        {
            Assert.Empty(SteamSubscriptionsResolver.ParseSubscriptionsFile(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ParseSubscriptionsFile_reads_fixture_on_disk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "steam-subscriptions", "552500_subscriptions_sample.vdf");
        Assert.True(File.Exists(path), $"Fixture not copied to output: {path}");

        var ids = SteamSubscriptionsResolver.ParseSubscriptionsFile(path).ToList();
        Assert.Equal(3, ids.Count);                             // 4 entries minus the disabled one
        Assert.DoesNotContain("3000000000", ids);
    }

    [Fact]
    public void ResolveSubscribedIds_returns_empty_when_userdata_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var resolver = new SteamSubscriptionsResolver(dir);
            Assert.Empty(resolver.ResolveSubscribedIds(552500));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveSubscribedIds_aggregates_across_account_dirs_and_dedupes()
    {
        // Two account dirs, each with their own subscriptions file. ID "5555" is in both —
        // we expect a single entry in the union.
        var root = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dirA = Path.Combine(root, "userdata", "111", "ugc");
            var dirB = Path.Combine(root, "userdata", "222", "ugc");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            File.WriteAllText(Path.Combine(dirA, "552500_subscriptions.vdf"), """
                "subscribedfiles"
                {
                    "appid" "552500"
                    "0" { "publishedfileid" "5555" "disabled_locally" "0" }
                    "1" { "publishedfileid" "1001" "disabled_locally" "0" }
                }
                """);
            File.WriteAllText(Path.Combine(dirB, "552500_subscriptions.vdf"), """
                "subscribedfiles"
                {
                    "appid" "552500"
                    "0" { "publishedfileid" "5555" "disabled_locally" "0" }
                    "1" { "publishedfileid" "2002" "disabled_locally" "0" }
                }
                """);

            var resolver = new SteamSubscriptionsResolver(root);
            var ids = resolver.ResolveSubscribedIds(552500).ToList();

            Assert.Equal(3, ids.Count);
            Assert.Contains("5555", ids);
            Assert.Contains("1001", ids);
            Assert.Contains("2002", ids);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ResolveSubscribedIds_ignores_other_app_subscription_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ugc = Path.Combine(root, "userdata", "111", "ugc");
            Directory.CreateDirectory(ugc);

            // Wrong appid file — must not be read.
            File.WriteAllText(Path.Combine(ugc, "730_subscriptions.vdf"), """
                "subscribedfiles" { "0" { "publishedfileid" "9999" "disabled_locally" "0" } }
                """);
            // Right appid, one entry.
            File.WriteAllText(Path.Combine(ugc, "552500_subscriptions.vdf"), """
                "subscribedfiles" { "0" { "publishedfileid" "1234" "disabled_locally" "0" } }
                """);

            var resolver = new SteamSubscriptionsResolver(root);
            var ids = resolver.ResolveSubscribedIds(552500).ToList();
            Assert.Single(ids);
            Assert.Equal("1234", ids[0]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ResolveSubscribedIds_skips_non_numeric_account_dirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt2mm-steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var garbage = Path.Combine(root, "userdata", "ac", "ugc");
            Directory.CreateDirectory(garbage);
            File.WriteAllText(Path.Combine(garbage, "552500_subscriptions.vdf"), """
                "subscribedfiles" { "0" { "publishedfileid" "9999" "disabled_locally" "0" } }
                """);

            var resolver = new SteamSubscriptionsResolver(root);
            Assert.Empty(resolver.ResolveSubscribedIds(552500));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
