using System.Collections.Generic;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class CollectionExporterTests
{
    private static ModEntry Mod(string id, string name, string author, bool enabled)
    {
        var e = new ModEntry();
        e.Fields.Add(new("id",      new RawValue.StringValue(id)));
        e.Fields.Add(new("name",    new RawValue.StringValue(name)));
        e.Fields.Add(new("author",  new RawValue.StringValue(author)));
        e.Fields.Add(new("enabled", new RawValue.BoolValue(enabled)));
        return e;
    }

    [Fact]
    public void Build_includes_all_entries_by_default()
    {
        var mods = new[] { Mod("1", "Alpha", "a", true), Mod("2", "Beta", "b", false) };
        var export = new CollectionExporter().Build(mods, enabledOnly: false);
        Assert.Equal(2, export.Count);
        Assert.Equal(552500u, export.AppId);
        Assert.Equal("1", export.Entries[0].Id);
        Assert.Contains("1", export.Entries[0].Url);
    }

    [Fact]
    public void Build_enabled_only_filters_correctly()
    {
        var mods = new[] { Mod("1", "Alpha", "a", true), Mod("2", "Beta", "b", false) };
        var export = new CollectionExporter().Build(mods, enabledOnly: true);
        Assert.Single(export.Entries);
        Assert.Equal("Alpha", export.Entries[0].Name);
    }

    [Fact]
    public void Url_list_format_one_per_line()
    {
        var mods = new[] { Mod("11", "A", "x", true), Mod("22", "B", "y", true) };
        var exporter = new CollectionExporter();
        var urls = exporter.ToUrlList(exporter.Build(mods, false));
        var lines = urls.Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=11", lines[0]);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=22", lines[1]);
    }

    [Fact]
    public void Tabbed_format_is_id_tab_name()
    {
        var mods = new[] { Mod("11", "A", "x", true) };
        var exporter = new CollectionExporter();
        var tabbed = exporter.ToTabbedList(exporter.Build(mods, false)).Trim();
        Assert.Equal("11\tA", tabbed);
    }

    [Fact]
    public void Json_roundtrips_via_System_Text_Json()
    {
        var mods = new[] { Mod("11", "A", "x", true) };
        var exporter = new CollectionExporter();
        var json = exporter.ToJson(exporter.Build(mods, false));
        var dto = System.Text.Json.JsonSerializer.Deserialize<CollectionExporter.Export>(json);
        Assert.NotNull(dto);
        Assert.Equal(552500u, dto!.AppId);
        Assert.Single(dto.Entries);
        Assert.Equal("A", dto.Entries[0].Name);
    }
}
