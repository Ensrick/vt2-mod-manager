using System;
using System.IO;

namespace Vt2ModManager.Services;

/// <summary>
/// Resolves the path to VT2's user_settings.config. Default location:
/// %AppData%\Fatshark\Vermintide 2\user_settings.config
/// </summary>
public sealed class UserSettingsConfigLocator
{
    private readonly Settings _settings;

    public UserSettingsConfigLocator(Settings settings) { _settings = settings; }

    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_settings.UserSettingsConfigPathOverride))
            return _settings.UserSettingsConfigPathOverride!;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Fatshark", "Vermintide 2", "user_settings.config");
    }

    public bool Exists() => File.Exists(Resolve());
}
