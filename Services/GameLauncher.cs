using System;
using System.Diagnostics;
using System.IO;

namespace Vt2ModManager.Services;

/// <summary>
/// Starts Vermintide 2 in one of two modes:
///   • Direct: spawns <c>binaries\vermintide2.exe -bundle-dir bundle</c> in the game's root
///     directory. Steam must be running so the in-process Steam API can initialize
///     (SteamUGC, ownership, friends).
///   • Via Steam: opens <c>steam://rungameid/552500</c>, which honors the user's configured
///     launch options. The Fatshark launcher will appear unless those options bypass it.
///
/// Bypassing the launcher is safe for modded play because this tool already owns the
/// mod list (<c>user_settings.config</c>'s <c>mods</c> array) that the launcher would have
/// written. Workshop subscription downloads happen via Steam's background sync — not via
/// the launcher — so they continue to land while the game is running.
/// </summary>
public sealed class GameLauncher
{
    public sealed record LaunchResult(bool Started, int? ProcessId, string Message);

    public LaunchResult LaunchDirect(Vt2Installation install, bool moddedRealm = true, string? extraArgs = null)
    {
        if (!install.HasGameExe)
            return new(false, null, $"Game exe not found at {install.GameExePath}.");
        if (!IsSteamRunning())
            return new(false, null, "Steam is not running. Start Steam first so the in-game Steam API can initialize (Workshop, ownership check).");

        // When bypassing start_protected_game.exe, Steamworks needs steam_appid.txt next to the
        // .exe so SteamAPI_Init() knows which app to authenticate. Write it idempotently.
        EnsureSteamAppId(install);

        // -bundle-dir locates the Stingray bundle archive.
        // -eac-untrusted is read in application_parameter.lua:150 (outside the dev-build gate)
        // and switches the game to the modded realm backend, which is the only realm where
        // user mods load.
        var args = $"-bundle-dir \"{install.BundleDir}\"";
        if (moddedRealm) args += " -eac-untrusted";
        if (!string.IsNullOrWhiteSpace(extraArgs)) args += " " + extraArgs;

        var psi = new ProcessStartInfo
        {
            FileName = install.GameExePath,
            Arguments = args,
            WorkingDirectory = install.Root,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi);
        if (proc is null) return new(false, null, "Process.Start returned null.");
        var realm = moddedRealm ? "modded" : "official";
        return new(true, proc.Id, $"Launched {Path.GetFileName(install.GameExePath)} ({realm} realm, PID {proc.Id}).");
    }

    private static void EnsureSteamAppId(Vt2Installation install)
    {
        try
        {
            var path = install.SteamAppIdPath;
            var want = Vt2Installation.AppId.ToString();
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing == want) return;
            }
            File.WriteAllText(path, want);
        }
        catch
        {
            // Best effort. If we can't write, the launch may still succeed if Fatshark left
            // steam_appid.txt in place from a prior official-launcher run.
        }
    }

    public LaunchResult LaunchViaSteam()
    {
        // steam://rungameid/<appid> always honors the user's library launch options. If the
        // Fatshark launcher is still wired up there, it will appear.
        var psi = new ProcessStartInfo
        {
            FileName = $"steam://rungameid/{Vt2Installation.AppId}",
            UseShellExecute = true,
        };
        try
        {
            Process.Start(psi);
            return new(true, null, $"Sent steam://rungameid/{Vt2Installation.AppId}.");
        }
        catch (Exception ex)
        {
            return new(false, null, $"Failed to open steam:// URL: {ex.Message}");
        }
    }

    public static bool IsSteamRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("vermintide2").Length > 0; }
        catch { return false; }
    }
}
