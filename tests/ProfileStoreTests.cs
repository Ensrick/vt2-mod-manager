using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class ProfileStoreTests
{
    private static string FixturePath() =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", "sample_user_settings.config");

    private static (ProfileStore Store, string Dir) FreshStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt2mm-profiles-" + System.Guid.NewGuid().ToString("N"));
        return (new ProfileStore(dir), dir);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var (store, dir) = FreshStore();
        try
        {
            var block = new UserSettingsConfigReader().ReadFile(FixturePath());
            var profile = ProfileStore.Capture("modded-realm", block, "test profile");
            store.Save(profile);

            var names = store.List();
            Assert.Contains("modded-realm", names);

            var loaded = store.Load("modded-realm");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Entries.Count);
            Assert.Equal("1111111111", loaded.Entries[0].Id);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Apply_toggles_and_reorders_known_mods()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());

        // Synthesize a profile that flips both mods' enabled state AND reverses their order.
        var profile = new Profile
        {
            Name = "test",
            Entries =
            {
                new ProfileEntry { Id = "2222222222", Name = "Beta Mod",  Enabled = true  },
                new ProfileEntry { Id = "1111111111", Name = "Alpha Mod", Enabled = false },
            }
        };
        var result = ProfileStore.Apply(profile, block);

        Assert.Equal(2, result.ToggledCount);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Extras);
        Assert.Equal("2222222222", block.Entries[0].Id);
        Assert.True(block.Entries[0].Enabled);
        Assert.False(block.Entries[1].Enabled);
    }

    [Fact]
    public void Apply_reports_missing_and_extras()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        var profile = new Profile
        {
            Name = "incomplete",
            Entries =
            {
                new ProfileEntry { Id = "1111111111", Name = "Alpha Mod", Enabled = true },
                new ProfileEntry { Id = "9999999999", Name = "Phantom",   Enabled = true },
            }
        };
        var result = ProfileStore.Apply(profile, block);
        Assert.Single(result.Missing);
        Assert.Equal("9999999999", result.Missing[0].Id);
        Assert.Single(result.Extras);
        Assert.Equal("2222222222", result.Extras[0].Id);
        // Extra mod kept its original Enabled state (false in the fixture).
        Assert.False(result.Extras[0].Enabled);
    }
}
