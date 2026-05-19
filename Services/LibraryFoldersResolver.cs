using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Enumerates every Steam library root on the machine by parsing
/// &lt;Steam&gt;\steamapps\libraryfolders.vdf (KeyValues format).
/// </summary>
public sealed class LibraryFoldersResolver
{
    public IReadOnlyList<string> Resolve(string steamInstallPath)
    {
        var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return new[] { steamInstallPath };

        var node = AcfNode.ParseFile(vdfPath);

        var roots = new List<string>();
        foreach (var (_, childNode) in node.Children)
        {
            if (!childNode.IsObject) continue;
            var p = childNode["path"]?.AsString();
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                roots.Add(p!);
        }

        if (roots.Count == 0) roots.Add(steamInstallPath);

        return roots
            .Select(r => Path.GetFullPath(r))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
