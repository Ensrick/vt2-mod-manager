using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Vt2ModManager.Services;

/// <summary>
/// Walks `appworkshop_&lt;appid&gt;.acf` under the given Steam library roots and yields one
/// <see cref="WorkshopItemLocal"/> per subscribed item.
/// </summary>
public sealed class WorkshopEnumerator
{
    private static readonly Regex AcfNameRegex =
        new(@"^appworkshop_(\d+)\.acf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<WorkshopItemLocal> EnumerateForApp(IEnumerable<string> libraryRoots, uint appId)
    {
        var wantName = $"appworkshop_{appId}.acf";
        foreach (var root in libraryRoots)
        {
            var acf = Path.Combine(root, "steamapps", "workshop", wantName);
            if (!File.Exists(acf)) continue;
            foreach (var item in ParseOne(acf)) yield return item;
        }
    }

    private static IEnumerable<WorkshopItemLocal> ParseOne(string acfPath)
    {
        var fileName = Path.GetFileName(acfPath);
        var m = AcfNameRegex.Match(fileName);
        if (!m.Success || !uint.TryParse(m.Groups[1].Value, out var appId)) yield break;

        AcfNode root;
        try { root = AcfNode.ParseFile(acfPath); }
        catch (Exception) { yield break; }

        var installed = root["WorkshopItemsInstalled"];
        if (installed is null || !installed.IsObject) yield break;

        foreach (var (idStr, body) in installed.Children)
        {
            if (!ulong.TryParse(idStr, out var publishedFileId)) continue;
            if (!body.IsObject) continue;

            yield return new WorkshopItemLocal(
                AppId: appId,
                PublishedFileId: publishedFileId,
                LocalTimeUpdated: body["timeupdated"]?.AsLong() ?? 0,
                LocalManifest: body["manifest"]?.AsString() ?? "",
                LocalSizeBytes: body["size"]?.AsLong() ?? 0);
        }
    }
}
