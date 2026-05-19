using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class FriendsStoreTests
{
    private static (FriendsStore Store, string Path, string Dir) FreshStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt2mm-friends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "friends.json");
        return (new FriendsStore(path), path, dir);
    }

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var (store, _, dir) = FreshStore();
        try
        {
            var list = store.Load();
            Assert.NotNull(list);
            Assert.Empty(list);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var (store, _, dir) = FreshStore();
        try
        {
            var now = DateTime.UtcNow;
            var friends = new List<Friend>
            {
                new() { SteamId64 = "76561197960287930", DisplayName = "Gaben", Favorite = true,
                        AddedUtc = now, LastFetchedUtc = now },
                new() { SteamId64 = "76561198000000001", DisplayName = "Someone Else",
                        AddedUtc = now },
            };

            store.Save(friends);
            var loaded = store.Load();

            Assert.Equal(2, loaded.Count);
            Assert.Equal("76561197960287930", loaded[0].SteamId64);
            Assert.True(loaded[0].Favorite);
            Assert.Equal("Gaben", loaded[0].DisplayName);
            Assert.NotNull(loaded[0].LastFetchedUtc);
            Assert.Null(loaded[1].LastFetchedUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_is_atomic_via_tmp_then_move()
    {
        var (store, path, dir) = FreshStore();
        try
        {
            store.Save(new List<Friend>
            {
                new() { SteamId64 = "76561197960287930", DisplayName = "Gaben", AddedUtc = DateTime.UtcNow },
            });

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_quarantines_corrupt_file_and_returns_empty()
    {
        var (store, path, dir) = FreshStore();
        try
        {
            File.WriteAllText(path, "{not valid json");

            var list = store.Load();
            Assert.Empty(list);

            Assert.False(File.Exists(path));
            var quarantined = Directory.GetFiles(dir, "*.corrupt-*").Single();
            Assert.StartsWith("friends.json.corrupt-", Path.GetFileName(quarantined));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DefaultPath_points_at_appdata_vt2modmanager()
    {
        var p = FriendsStore.DefaultPath();
        Assert.EndsWith(Path.Combine("Vt2ModManager", "friends.json"), p);
    }
}
