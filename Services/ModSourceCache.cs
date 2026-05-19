using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vt2ModManager.Services;

/// <summary>
/// Locates loose Lua source for subscribed VT2 mods under a Steam Workshop content root
/// (e.g. <c>...\steamapps\workshop\content\552500</c>).
///
/// VT2 mod folders look like: <c>&lt;workshop_id&gt;\source\scripts\mods\&lt;name&gt;\*.lua</c>.
/// Roughly ~65% of mods ship loose source; the rest are bundle-only and return an empty list
/// (UI should label them "no source available" — Tier-2 will decompile their bytecode).
///
/// Results are cached per mod ID so repeated detector runs do not re-walk the filesystem.
/// </summary>
public sealed class ModSourceCache
{
    private readonly string _workshopContentRoot;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _readCache = new(StringComparer.OrdinalIgnoreCase);

    public ModSourceCache(string workshopContentRoot)
    {
        _workshopContentRoot = workshopContentRoot ?? throw new ArgumentNullException(nameof(workshopContentRoot));
    }

    /// <summary>Root folder being scanned (e.g. <c>...\workshop\content\552500</c>).</summary>
    public string WorkshopContentRoot => _workshopContentRoot;

    /// <summary>
    /// Return every <c>.lua</c> path under <c>&lt;modId&gt;\source\</c>. Empty list when the mod
    /// is missing, bundle-only, or otherwise has no loose source.
    /// </summary>
    public IReadOnlyList<string> GetLuaFiles(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Array.Empty<string>();
        return _files.GetOrAdd(modId, ScanLuaFiles);
    }

    /// <summary>Quick check: does this mod have any analyzable loose source?</summary>
    public bool HasLooseSource(string modId) => GetLuaFiles(modId).Count > 0;

    /// <summary>
    /// Read a Lua source file once and cache the text. UTF-8 BOM is auto-detected by
    /// <see cref="File.ReadAllText(string)"/>.
    /// </summary>
    public string ReadFile(string path)
    {
        return _readCache.GetOrAdd(path, static p =>
        {
            try { return File.ReadAllText(p); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        });
    }

    /// <summary>Forget every cached file list and file body. Used by tests.</summary>
    public void Clear()
    {
        _files.Clear();
        _readCache.Clear();
    }

    private IReadOnlyList<string> ScanLuaFiles(string modId)
    {
        var modDir = Path.Combine(_workshopContentRoot, modId);
        var sourceDir = Path.Combine(modDir, "source");
        if (!Directory.Exists(sourceDir)) return Array.Empty<string>();

        try
        {
            return Directory
                .EnumerateFiles(sourceDir, "*.lua", SearchOption.AllDirectories)
                .ToArray();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
