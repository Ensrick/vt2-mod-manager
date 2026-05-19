using System;
using System.Collections.Generic;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Three-pass sort that matches the engine's own dependency rules and adds opinionated layering
/// on top:
///   1. Top pins (rules.PinTopIds) in declared order — VMF goes first by default.
///   2. Kahn topological sort using each mod's `children[]` plus rules.ExtraDependencies as edges.
///      Ties are broken by the entry's current index, so the result is stable when nothing
///      structural changes.
///   3. Bottom pins (rules.PinBottomIds) in declared order.
///
/// Mods not mentioned anywhere are folded into pass 2 by current index. Cycles fall back to
/// current order for the cycle members (preserves user intent over crashing).
/// </summary>
public sealed class LoadOrderSorter
{
    public sealed record SortResult(List<ModEntry> Sorted, List<string> CycleMemberIds, List<string> UnknownPinIds);

    public SortResult Sort(IReadOnlyList<ModEntry> entries, LoadOrderRules rules)
    {
        var byId = new Dictionary<string, ModEntry>(StringComparer.Ordinal);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            byId[entries[i].Id] = entries[i];
            index[entries[i].Id] = i;
        }

        var unknownPins = new List<string>();
        var topPinned   = TakePinned(rules.PinTopIds,    byId, unknownPins);
        var bottomPinned= TakePinned(rules.PinBottomIds, byId, unknownPins);
        var pinnedIds = new HashSet<string>(topPinned.Concat(bottomPinned).Select(e => e.Id), StringComparer.Ordinal);

        // Build a dependency graph over the un-pinned middle. Edge child->parent means
        // child must load before parent. children[] is authoritative; rules.ExtraDependencies
        // overlays soft edges (silently ignored if either endpoint isn't installed).
        var middle = entries.Where(e => !pinnedIds.Contains(e.Id)).ToList();
        var middleSet = new HashSet<string>(middle.Select(m => m.Id), StringComparer.Ordinal);

        // Adjacency: forEach parent, the set of children it depends on (must come first).
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var m in middle) deps[m.Id] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in middle)
        {
            foreach (var c in m.Children)
            {
                if (middleSet.Contains(c)) deps[m.Id].Add(c);
            }
        }
        foreach (var rule in rules.ExtraDependencies)
        {
            if (middleSet.Contains(rule.ParentId) && middleSet.Contains(rule.ChildId))
                deps[rule.ParentId].Add(rule.ChildId);
        }

        var sortedMiddle = KahnTopoSort(middle, deps, index, out var cycleMembers);

        var final = new List<ModEntry>(entries.Count);
        final.AddRange(topPinned);
        final.AddRange(sortedMiddle);
        final.AddRange(bottomPinned);

        return new SortResult(final, cycleMembers, unknownPins);
    }

    private static List<ModEntry> TakePinned(List<string> ids, Dictionary<string, ModEntry> byId, List<string> unknownOut)
    {
        var result = new List<ModEntry>();
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var entry)) result.Add(entry);
            else unknownOut.Add(id);
        }
        return result;
    }

    private static List<ModEntry> KahnTopoSort(
        List<ModEntry> middle,
        Dictionary<string, HashSet<string>> deps,
        Dictionary<string, int> originalIndex,
        out List<string> cycleMembers)
    {
        // In-degree = how many deps this mod still waits on.
        var inDegree = middle.ToDictionary(m => m.Id, m => deps[m.Id].Count, StringComparer.Ordinal);

        // Reverse map: child -> set of parents that wait on it.
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var m in middle) dependents[m.Id] = new List<string>();
        foreach (var (parent, childSet) in deps)
            foreach (var child in childSet)
                dependents[child].Add(parent);

        // Min-heap by (original index) so ties break to current order.
        var ready = new SortedSet<(int Index, string Id)>();
        foreach (var m in middle)
            if (inDegree[m.Id] == 0) ready.Add((originalIndex[m.Id], m.Id));

        var byId = middle.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var result = new List<ModEntry>(middle.Count);

        while (ready.Count > 0)
        {
            var node = ready.Min;
            ready.Remove(node);
            result.Add(byId[node.Id]);

            foreach (var parent in dependents[node.Id])
            {
                inDegree[parent]--;
                if (inDegree[parent] == 0) ready.Add((originalIndex[parent], parent));
            }
        }

        cycleMembers = new List<string>();
        if (result.Count < middle.Count)
        {
            // Cycle remains. Append unresolved nodes in their current order — preserves user
            // intent rather than crashing. Record them so the UI can flag.
            foreach (var m in middle)
            {
                if (inDegree.TryGetValue(m.Id, out var d) && d > 0)
                {
                    cycleMembers.Add(m.Id);
                    result.Add(m);
                }
            }
        }

        return result;
    }
}
