using System.Globalization;
using System.Linq;
using Vt2ModManager.Services;

namespace Vt2ModManager.ViewModels;

public sealed class ProfileRowViewModel
{
    public ProfileRowViewModel(Profile profile)
    {
        Profile = profile;
    }

    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Description ?? "";
    public int EntryCount => Profile.Entries.Count;
    public int EnabledCount => Profile.Entries.Count(e => e.Enabled);
    public string SavedDisplay => Profile.CreatedUtc == default
        ? "—"
        : Profile.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
