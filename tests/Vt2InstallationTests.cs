using System;
using System.IO;
using Vt2ModManager.Services;
using Xunit;

namespace Vt2ModManager.Tests;

public sealed class Vt2InstallationTests
{
    [Fact]
    public void Resolve_finds_install_from_manifest_in_first_matching_library()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vt2mm-vt2install-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Library 1: no VT2 here.
            var lib1 = Path.Combine(tmp, "lib1");
            Directory.CreateDirectory(Path.Combine(lib1, "steamapps"));

            // Library 2: has VT2.
            var lib2 = Path.Combine(tmp, "lib2");
            var installDirName = "Warhammer Vermintide 2";
            var installRoot = Path.Combine(lib2, "steamapps", "common", installDirName);
            Directory.CreateDirectory(Path.Combine(installRoot, "binaries"));
            File.WriteAllText(Path.Combine(installRoot, "binaries", "vermintide2.exe"), "");
            File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_552500.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\"552500\"\n\t\"installdir\"\t\"" + installDirName + "\"\n}\n");

            var install = Vt2Installation.Resolve(steamRoot: lib1, libraries: new[] { lib1, lib2 });
            Assert.NotNull(install);
            Assert.Equal(installRoot, install!.Root);
            Assert.True(install.HasGameExe);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Resolve_returns_null_when_no_library_has_manifest()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vt2mm-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "steamapps"));
            var install = Vt2Installation.Resolve(tmp, new[] { tmp });
            Assert.Null(install);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }
}
