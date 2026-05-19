using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class ModSourceCacheTests
{
    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "source-cache");

    [Fact]
    public void GetLuaFiles_returns_every_lua_under_source_dir()
    {
        var cache = new ModSourceCache(FixtureRoot);

        var files = cache.GetLuaFiles("111111111");

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("AlphaMod.lua"));
        Assert.Contains(files, f => f.EndsWith("AlphaMod_data.lua"));
    }

    [Fact]
    public void HasLooseSource_true_when_source_dir_contains_lua()
    {
        var cache = new ModSourceCache(FixtureRoot);

        Assert.True(cache.HasLooseSource("111111111"));
        Assert.True(cache.HasLooseSource("222222222"));
        Assert.True(cache.HasLooseSource("333333333"));
    }

    [Fact]
    public void HasLooseSource_false_for_bundle_only_mod()
    {
        var cache = new ModSourceCache(FixtureRoot);

        Assert.False(cache.HasLooseSource("444444444"));
        Assert.Empty(cache.GetLuaFiles("444444444"));
    }

    [Fact]
    public void GetLuaFiles_empty_for_unknown_mod_id()
    {
        var cache = new ModSourceCache(FixtureRoot);

        Assert.Empty(cache.GetLuaFiles("does-not-exist"));
        Assert.False(cache.HasLooseSource("does-not-exist"));
    }

    [Fact]
    public void GetLuaFiles_empty_for_null_or_whitespace_id()
    {
        var cache = new ModSourceCache(FixtureRoot);

        Assert.Empty(cache.GetLuaFiles(""));
        Assert.Empty(cache.GetLuaFiles("   "));
    }

    [Fact]
    public void ReadFile_caches_so_repeated_reads_return_same_text()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var path = cache.GetLuaFiles("111111111").First();

        var a = cache.ReadFile(path);
        var b = cache.ReadFile(path);

        Assert.Equal(a, b);
        Assert.NotEmpty(a);
        // Reference-equal because we cache the string instance.
        Assert.Same(a, b);
    }

    [Fact]
    public void GetLuaFiles_results_are_cached()
    {
        var cache = new ModSourceCache(FixtureRoot);

        var first = cache.GetLuaFiles("111111111");
        var second = cache.GetLuaFiles("111111111");

        // Same list instance on the second call confirms caching.
        Assert.Same(first, second);
    }
}
