using System.IO;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Resolves the VT2 install directory by scanning each Steam library root for
/// <c>steamapps\appmanifest_552500.acf</c> and reading the <c>installdir</c> field.
/// </summary>
public sealed class Vt2Installation
{
    public const uint AppId = 552500;

    public string Root { get; }

    private Vt2Installation(string root) { Root = root; }

    public string BinariesDir    => Path.Combine(Root, "binaries");
    public string GameExePath    => Path.Combine(BinariesDir, "vermintide2.exe");
    public string LauncherExePath => Path.Combine(Root, "start_protected_game.exe");
    public string BundleDir      => Path.Combine(Root, "bundle");
    public string SteamAppIdPath => Path.Combine(BinariesDir, "steam_appid.txt");
    public bool   HasGameExe     => File.Exists(GameExePath);
    public bool   HasLauncherExe => File.Exists(LauncherExePath);

    public static Vt2Installation? Resolve(string steamRoot, System.Collections.Generic.IReadOnlyList<string> libraries)
    {
        foreach (var lib in libraries)
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var node = AcfNode.ParseFile(manifest);
            var installDir = node["installdir"]?.AsString();
            if (string.IsNullOrWhiteSpace(installDir)) continue;
            var root = Path.Combine(lib, "steamapps", "common", installDir!);
            if (Directory.Exists(root))
                return new Vt2Installation(root);
        }
        return null;
    }
}
