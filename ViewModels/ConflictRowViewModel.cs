using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Vt2ModManager.Services;

namespace Vt2ModManager.ViewModels;

/// <summary>
/// One conflict row in the Conflicts tab. Resolves mod IDs to mod names for display.
/// </summary>
public sealed class ConflictRowViewModel
{
    public ConflictRowViewModel(Conflict conflict, IDictionary<string, string> modNamesById)
    {
        Conflict = conflict;
        ModNames = string.Join(", ", conflict.ModIds.Select(id =>
            modNamesById.TryGetValue(id, out var name) ? name : id));
    }

    public Conflict Conflict { get; }
    public string Kind => Conflict.Kind;
    public string Key => Conflict.Key;
    public string Severity => Conflict.Severity.ToString();
    public string ModNames { get; }
    public string Detail => Conflict.Detail;

    public Brush SeverityBrush => Conflict.Severity switch
    {
        ConflictSeverity.Crash  => new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55)),
        ConflictSeverity.High   => new SolidColorBrush(Color.FromRgb(0xE0, 0x9F, 0x3E)),
        ConflictSeverity.Medium => new SolidColorBrush(Color.FromRgb(0xE0, 0xCE, 0x3E)),
        _                       => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };
}
