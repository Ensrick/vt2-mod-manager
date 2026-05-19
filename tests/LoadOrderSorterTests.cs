using System.Collections.Generic;
using System.Linq;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class LoadOrderSorterTests
{
    private static ModEntry Mod(string id, string name, bool enabled = true, params string[] children)
    {
        var e = new ModEntry();
        e.Fields.Add(new("id",      new RawValue.StringValue(id)));
        e.Fields.Add(new("name",    new RawValue.StringValue(name)));
        e.Fields.Add(new("enabled", new RawValue.BoolValue(enabled)));
        if (children.Length > 0)
        {
            var arr = new List<RawValue>();
            foreach (var c in children) arr.Add(new RawValue.StringValue(c));
            e.Fields.Add(new("children", new RawValue.ArrayValue(arr)));
        }
        return e;
    }

    [Fact]
    public void Top_pin_moves_VMF_to_position_zero()
    {
        var entries = new List<ModEntry>
        {
            Mod("a", "A"),
            Mod("b", "B"),
            Mod("1369573612", "VMF"),
            Mod("c", "C"),
        };
        var rules = new LoadOrderRules { PinTopIds = { "1369573612" } };
        var r = new LoadOrderSorter().Sort(entries, rules);

        Assert.Equal("1369573612", r.Sorted[0].Id);
        Assert.Equal("a", r.Sorted[1].Id);
        Assert.Equal("b", r.Sorted[2].Id);
        Assert.Equal("c", r.Sorted[3].Id);
    }

    [Fact]
    public void Children_load_before_parent_when_currently_after()
    {
        // parent declares child as dependency but child is currently AFTER parent.
        var entries = new List<ModEntry>
        {
            Mod("parent", "Parent", children: new[] { "child" }),
            Mod("child",  "Child"),
        };
        var r = new LoadOrderSorter().Sort(entries, new LoadOrderRules());
        Assert.Equal("child",  r.Sorted[0].Id);
        Assert.Equal("parent", r.Sorted[1].Id);
    }

    [Fact]
    public void Stable_when_no_constraints_match()
    {
        var entries = new List<ModEntry>
        {
            Mod("x", "X"), Mod("y", "Y"), Mod("z", "Z"),
        };
        var r = new LoadOrderSorter().Sort(entries, new LoadOrderRules());
        Assert.Equal(new[] { "x", "y", "z" }, r.Sorted.Select(e => e.Id));
    }

    [Fact]
    public void Cycle_is_detected_and_members_reported()
    {
        var entries = new List<ModEntry>
        {
            Mod("a", "A", children: new[] { "b" }),
            Mod("b", "B", children: new[] { "a" }),
        };
        var r = new LoadOrderSorter().Sort(entries, new LoadOrderRules());
        Assert.Equal(2, r.CycleMemberIds.Count);
        Assert.Contains("a", r.CycleMemberIds);
        Assert.Contains("b", r.CycleMemberIds);
        // Both still present in output (cycle fallback preserves them in current order).
        Assert.Equal(2, r.Sorted.Count);
    }

    [Fact]
    public void Extra_dependency_rule_adds_edge()
    {
        var entries = new List<ModEntry>
        {
            Mod("consumer", "Consumer"),
            Mod("library",  "Library"),
        };
        var rules = new LoadOrderRules
        {
            ExtraDependencies = { new DependencyRule { ParentId = "consumer", ChildId = "library", Note = "manual" } }
        };
        var r = new LoadOrderSorter().Sort(entries, rules);
        Assert.Equal("library",  r.Sorted[0].Id);
        Assert.Equal("consumer", r.Sorted[1].Id);
    }

    [Fact]
    public void Unknown_pin_is_reported_but_does_not_throw()
    {
        var entries = new List<ModEntry> { Mod("a", "A") };
        var rules = new LoadOrderRules { PinTopIds = { "999999" } };
        var r = new LoadOrderSorter().Sort(entries, rules);
        Assert.Single(r.UnknownPinIds);
        Assert.Equal("999999", r.UnknownPinIds[0]);
        Assert.Single(r.Sorted);
    }

    [Fact]
    public void Bottom_pin_moves_id_to_end()
    {
        var entries = new List<ModEntry>
        {
            Mod("a", "A"), Mod("b", "B"), Mod("c", "C"),
        };
        var rules = new LoadOrderRules { PinBottomIds = { "a" } };
        var r = new LoadOrderSorter().Sort(entries, rules);
        Assert.Equal("a", r.Sorted[^1].Id);
    }
}
