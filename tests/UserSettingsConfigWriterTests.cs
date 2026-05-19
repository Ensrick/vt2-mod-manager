using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class UserSettingsConfigWriterTests
{
    private static string FixturePath() =>
        Path.Combine(System.AppContext.BaseDirectory, "fixtures", "sample_user_settings.config");

    [Fact]
    public void Round_trip_preserves_unrelated_lines()
    {
        var path = FixturePath();
        var block = new UserSettingsConfigReader().ReadFile(path);

        var rendered = new UserSettingsConfigWriter().Render(block);

        // Prefix + suffix verbatim; the mods region is recomputed but content semantically equal.
        Assert.StartsWith(block.RawPrefix, rendered);
        Assert.EndsWith(block.RawSuffix, rendered);

        // And every mod ID present in the original is present in the rendered output.
        foreach (var e in block.Entries)
            Assert.Contains($"id = \"{e.Id}\"", rendered);
    }

    [Fact]
    public void Toggle_enabled_changes_rendered_output()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        block.Entries[0].Enabled = false;
        var rendered = new UserSettingsConfigWriter().Render(block);

        // Verify by re-reading the rendered text.
        var rt = new UserSettingsConfigReader().ReadText(rendered);
        Assert.False(rt.Entries[0].Enabled);
    }

    [Fact]
    public void Reorder_entries_persists_through_round_trip()
    {
        var block = new UserSettingsConfigReader().ReadFile(FixturePath());
        var first = block.Entries[0];
        var second = block.Entries[1];
        block.Entries[0] = second;
        block.Entries[1] = first;
        var rendered = new UserSettingsConfigWriter().Render(block);
        var rt = new UserSettingsConfigReader().ReadText(rendered);
        Assert.Equal("2222222222", rt.Entries[0].Id);
        Assert.Equal("1111111111", rt.Entries[1].Id);
    }

    [Fact]
    public void Writes_to_temp_path_atomically()
    {
        var src = FixturePath();
        var tmpDir = Path.Combine(Path.GetTempPath(), "vt2mm-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var dst = Path.Combine(tmpDir, "user_settings.config");
        File.Copy(src, dst);

        var block = new UserSettingsConfigReader().ReadFile(dst);
        block.Entries[1].Enabled = true;
        new UserSettingsConfigWriter().WriteFile(dst, block);

        Assert.True(File.Exists(dst + ".bak"));
        var rt = new UserSettingsConfigReader().ReadFile(dst);
        Assert.True(rt.Entries[1].Enabled);

        Directory.Delete(tmpDir, recursive: true);
    }
}
