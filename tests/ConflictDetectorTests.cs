using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class ConflictDetectorTests
{
    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "source-cache");

    private static ModEntry Entry(string id, string name, bool enabled)
    {
        var e = new ModEntry();
        e.Fields.Add(new KeyValuePair<string, RawValue>("id", new RawValue.StringValue(id)));
        e.Fields.Add(new KeyValuePair<string, RawValue>("name", new RawValue.StringValue(name)));
        e.Fields.Add(new KeyValuePair<string, RawValue>("enabled", new RawValue.BoolValue(enabled)));
        return e;
    }

    private static ModListBlock Block(params ModEntry[] entries) => new()
    {
        RawPrefix = "",
        RawSuffix = "",
        LineEnding = "\r\n",
        Entries = entries.ToList(),
    };

    private static (ConflictDetector det, ModListBlock block) BuildAlphaBeta()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var block = Block(
            Entry("111111111", "AlphaMod", enabled: true),
            Entry("222222222", "BetaMod", enabled: true));
        return (new ConflictDetector(cache), block);
    }

    [Fact]
    public void Detects_hook_origin_collision()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        var hook = Assert.Single(conflicts, c => c.Kind == "hook_origin");
        Assert.Equal("BuffUI._align_widgets", hook.Key);
        Assert.Equal(ConflictSeverity.Crash, hook.Severity);
        Assert.Equal(new[] { "111111111", "222222222" }, hook.ModIds.ToArray());
    }

    [Fact]
    public void Detects_keybind_setting_id_collision()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        var keybind = Assert.Single(conflicts, c => c.Kind == "setting_id");
        Assert.Equal("shared_keybind", keybind.Key);
        Assert.Equal(ConflictSeverity.Medium, keybind.Severity);
        Assert.Equal(new[] { "111111111", "222222222" }, keybind.ModIds.ToArray());
    }

    [Fact]
    public void Detects_buff_template_collision_across_dot_and_index_syntax()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        var buff = Assert.Single(conflicts, c => c.Kind == "buff_template");
        Assert.Equal("alpha_shared_buff", buff.Key);
        Assert.Equal(ConflictSeverity.High, buff.Severity);
        Assert.Equal(new[] { "111111111", "222222222" }, buff.ModIds.ToArray());
    }

    [Fact]
    public void Does_not_flag_unique_keys_only_one_mod_touches()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        Assert.DoesNotContain(conflicts, c => c.Key == "IngameUI.destroy");
        Assert.DoesNotContain(conflicts, c => c.Key == "alpha_unique");
        Assert.DoesNotContain(conflicts, c => c.Key == "alpha_only_keybind");
    }

    [Fact]
    public void Ignores_disabled_mods_even_when_they_would_collide()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var block = Block(
            Entry("111111111", "AlphaMod", enabled: true),
            Entry("222222222", "BetaMod", enabled: false),  // disabled
            Entry("333333333", "GammaMod", enabled: false)); // disabled
        var det = new ConflictDetector(cache);

        var conflicts = det.Detect(block);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Three_way_collision_lists_all_three_mods()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var block = Block(
            Entry("111111111", "AlphaMod", enabled: true),
            Entry("222222222", "BetaMod", enabled: true),
            Entry("333333333", "GammaMod", enabled: true));
        var det = new ConflictDetector(cache);

        var conflicts = det.Detect(block);

        var hook = Assert.Single(conflicts, c => c.Kind == "hook_origin");
        Assert.Equal(3, hook.ModIds.Count);
        Assert.Contains("333333333", hook.ModIds);
    }

    [Fact]
    public void Ignores_commented_out_buff_template_lines()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        Assert.DoesNotContain(conflicts, c => c.Key == "commented_buff");
    }

    [Fact]
    public void Ignores_setting_id_when_widget_is_not_keybind()
    {
        // AlphaMod's data file repeats "shared_keybind" on a checkbox widget; ensure that the
        // single-keybind-occurrence in AlphaMod still pairs only with BetaMod (no triple-count).
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        var keybind = Assert.Single(conflicts, c => c.Kind == "setting_id");
        Assert.Equal(2, keybind.ModIds.Count);
    }

    [Fact]
    public void Ignores_numeric_setting_id_noise()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        Assert.DoesNotContain(conflicts, c => c.Kind == "setting_id" && c.Key == "0");
    }

    [Fact]
    public void Empty_mod_list_yields_no_conflicts()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var det = new ConflictDetector(cache);

        var conflicts = det.Detect(Block());

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Bundle_only_mods_are_silently_skipped()
    {
        var cache = new ModSourceCache(FixtureRoot);
        var block = Block(
            Entry("111111111", "AlphaMod", enabled: true),
            Entry("222222222", "BetaMod", enabled: true),
            Entry("444444444", "BundleOnlyMod", enabled: true));
        var det = new ConflictDetector(cache);

        // Should still find the Alpha/Beta conflicts; bundle-only mod just contributes nothing.
        var conflicts = det.Detect(block);

        Assert.NotEmpty(conflicts);
        Assert.DoesNotContain(conflicts, c => c.ModIds.Contains("444444444"));
    }

    [Fact]
    public void Conflicts_are_ordered_by_severity_descending()
    {
        var (det, block) = BuildAlphaBeta();

        var conflicts = det.Detect(block);

        Assert.Equal(3, conflicts.Count);
        Assert.Equal(ConflictSeverity.Crash, conflicts[0].Severity);
        Assert.Equal(ConflictSeverity.High, conflicts[1].Severity);
        Assert.Equal(ConflictSeverity.Medium, conflicts[2].Severity);
    }
}
