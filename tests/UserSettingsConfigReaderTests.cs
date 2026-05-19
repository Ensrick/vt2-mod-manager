using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class UserSettingsConfigReaderTests
{
    private static string FixturePath() =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", "sample_user_settings.config");

    [Fact]
    public void Parses_two_mod_entries_in_order()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        Assert.Equal(2, block.Entries.Count);
        Assert.Equal("1111111111", block.Entries[0].Id);
        Assert.Equal("Alpha Mod",  block.Entries[0].Name);
        Assert.True (block.Entries[0].Enabled);
        Assert.Equal("2222222222", block.Entries[1].Id);
        Assert.False(block.Entries[1].Enabled);
    }

    [Fact]
    public void Captures_author_and_status_fields()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        Assert.Equal("alpha author", block.Entries[0].Author);
        Assert.True (block.Entries[0].Sanctioned);
        Assert.False(block.Entries[0].OutOfDate);
        Assert.Equal(0, block.Entries[0].NumChildren);
        Assert.Equal(1, block.Entries[1].NumChildren);
    }

    [Fact]
    public void Parses_children_array_as_string_ids()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        var children = block.Entries[1].Children;
        Assert.Single(children);
        Assert.Equal("1111111111", children[0]);
    }

    [Fact]
    public void Handles_multiline_description_string()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        var desc = block.Entries[0].GetString("description")!;
        Assert.Contains("multi-line", desc);
        Assert.Contains("\n", desc);
    }

    [Fact]
    public void Preserves_text_before_and_after_mods_block()
    {
        var text = File.ReadAllText(FixturePath());
        var block = new UserSettingsConfigReader().ReadText(text);
        Assert.Contains("adapter_index = 0", block.RawPrefix);
        Assert.Contains("mod_settings = {", block.RawPrefix);
        Assert.Contains("max_fps = 0", block.RawSuffix);
        // The mods array delimiters themselves live in neither half — the writer re-emits them.
        Assert.DoesNotContain("mods = [", block.RawPrefix);
        Assert.DoesNotContain("mods = [", block.RawSuffix);
    }

    [Fact]
    public void Does_not_match_mod_settings_key()
    {
        // Regression guard: the keyword `mod_settings` appears before `mods` in the file.
        // The reader must not match the prefix and confuse the two.
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        Assert.Equal(2, block.Entries.Count);
    }
}
