using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Vt2ModManager.Cli;
using Vt2ModManager.Services;

namespace Vt2ModManager;

public static class Program
{
    public const string Version = "0.1.3";

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Best-effort cleanup of leftover self-update artifacts (a .old from a clean update,
        // or a .new from one that crashed mid-flight) before anything else touches the disk.
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            UpdateInstaller.CleanupStaleArtifacts(exePath);
        }

        if (IsHeadlessInvocation(args))
            return CliRunner.Run(args);

        if (TryFreeConsole()) { /* best-effort console detach */ }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    public static bool IsHeadlessInvocation(string[] args)
    {
        if (args.Length == 0) return false;
        if (args.Any(a => string.Equals(a, "--gui", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>
    /// True when the current process is a local dev build (running under <c>dotnet run</c>
    /// or unpacked from <c>bin\Debug\</c>). The GUI's auto-update check honours this so we
    /// get zero update-check chatter during local development; only the published Release
    /// exe will reach GitHub on startup.
    /// </summary>
    public static bool IsDevBuild()
    {
        if (string.Equals(Version, "0.0.0-dev", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(loc)
                && loc.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { /* best-effort signal — not load-bearing */ }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private static bool TryFreeConsole()
    {
        try { return FreeConsole(); } catch { return false; }
    }
}
