using System;
using System.Collections.Generic;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Discriminated value type for the Stingray config tree. Numbers are kept as strings to avoid
/// float-formatting drift on round-trip.
/// </summary>
public abstract record RawValue
{
    public sealed record StringValue(string Text) : RawValue;
    public sealed record BoolValue(bool Value) : RawValue;
    /// <summary>Unquoted token — number, bool keyword, or other bareword. Kept verbatim.</summary>
    public sealed record NumberValue(string Text) : RawValue;
    public sealed record ArrayValue(List<RawValue> Items) : RawValue;
    public sealed record ObjectValue(List<KeyValuePair<string, RawValue>> Fields) : RawValue;
}

/// <summary>
/// One entry inside the top-level `mods = [ ... ]` array. Field order is preserved so a
/// round-tripped file diffs cleanly against the original.
/// </summary>
public sealed class ModEntry
{
    public List<KeyValuePair<string, RawValue>> Fields { get; } = new();

    public string Id => GetString("id") ?? "";
    public string Name => GetString("name") ?? "";
    public string Author => GetString("author") ?? "";
    public bool Enabled
    {
        get => GetBool("enabled") ?? false;
        set => SetBool("enabled", value);
    }
    public string LastUpdated => GetString("last_updated") ?? "";
    public bool Sanctioned => GetBool("sanctioned") ?? false;
    public bool OutOfDate => GetBool("out_of_date") ?? false;
    public bool Installed => GetBool("installed") ?? true;
    public int NumChildren => int.TryParse(GetNumber("num_children"), out var n) ? n : 0;

    public IReadOnlyList<string> Children
    {
        get
        {
            if (FindField("children") is RawValue.ArrayValue arr)
                return arr.Items.OfType<RawValue.StringValue>().Select(s => s.Text).ToList();
            return Array.Empty<string>();
        }
    }

    public string? GetString(string key) =>
        FindField(key) is RawValue.StringValue s ? s.Text : null;

    public bool? GetBool(string key) =>
        FindField(key) is RawValue.BoolValue b ? b.Value : null;

    public string? GetNumber(string key) =>
        FindField(key) is RawValue.NumberValue n ? n.Text : null;

    public RawValue? FindField(string key)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (Fields[i].Key == key) return Fields[i].Value;
        return null;
    }

    private void SetBool(string key, bool value)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].Key == key)
            {
                Fields[i] = new KeyValuePair<string, RawValue>(key, new RawValue.BoolValue(value));
                return;
            }
        }
        Fields.Add(new KeyValuePair<string, RawValue>(key, new RawValue.BoolValue(value)));
    }
}

/// <summary>
/// The parsed file, sliced into three parts: everything before `mods = [`, the parsed mod entries,
/// and everything after the matching `]`. Writers preserve Prefix/Suffix verbatim and re-emit
/// Entries in canonical form.
/// </summary>
public sealed class ModListBlock
{
    public required string RawPrefix { get; init; }
    public required string RawSuffix { get; init; }
    public required string LineEnding { get; init; } = "\r\n";
    public required List<ModEntry> Entries { get; init; }
}
