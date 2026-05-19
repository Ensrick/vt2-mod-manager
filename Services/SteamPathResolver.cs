using System;
using System.IO;
#pragma warning disable CA1416
using Microsoft.Win32;
#pragma warning restore CA1416

namespace Vt2ModManager.Services;

public sealed class SteamNotFoundException : Exception
{
    public SteamNotFoundException(string message) : base(message) { }
}

public sealed class SteamPathResolver
{
    private readonly Settings _settings;
    private readonly Func<string?> _probeRegistry;

    public SteamPathResolver(Settings settings, Func<string?>? probeRegistry = null)
    {
        _settings = settings;
        _probeRegistry = probeRegistry ?? ProbeWindowsRegistry;
    }

    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_settings.SteamPathOverride))
        {
            if (!Directory.Exists(_settings.SteamPathOverride))
                throw new SteamNotFoundException(
                    $"settings.json override points at '{_settings.SteamPathOverride}' but that directory does not exist.");
            return _settings.SteamPathOverride!;
        }

        var fromRegistry = _probeRegistry();
        if (!string.IsNullOrWhiteSpace(fromRegistry) && Directory.Exists(fromRegistry))
            return fromRegistry!;

        throw new SteamNotFoundException(
            "Could not locate Steam install. Set 'steam_path_override' in settings.json or install Steam to a registered location.");
    }

    public bool TryResolve(out string path)
    {
        try { path = Resolve(); return true; }
        catch (SteamNotFoundException) { path = ""; return false; }
    }

    private static string? ProbeWindowsRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return
            ReadKey(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ??
            ReadKey(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") ??
            ReadKey(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
    }

    private static string? ReadKey(RegistryHive hive, string subkey, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subkey);
            return key?.GetValue(value) as string;
        }
        catch
        {
            return null;
        }
    }
}
