using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vt2ModManager.Services;

/// <summary>
/// Serializes a <see cref="ModListBlock"/> back to disk. The block's RawPrefix/RawSuffix
/// are preserved byte-for-byte; only the <c>mods = [ ... ]</c> region is regenerated.
///
/// Write is atomic: a sibling .tmp file is fsync'd, the existing file is moved to .bak,
/// then .tmp is renamed into place. If the file is locked (game running), the write fails
/// before any state changes.
/// </summary>
public sealed class UserSettingsConfigWriter
{
    public void WriteFile(string path, ModListBlock block)
    {
        // Preflight: the file must be writable. The launcher and the game both open this file;
        // if either is running we surface a clear error before touching disk.
        AssertWritable(path);

        var rendered = Render(block);

        var dir = Path.GetDirectoryName(path) ?? ".";
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
        var bak = Path.Combine(dir, Path.GetFileName(path) + ".bak");

        // Write the new content fully before touching the live file.
        File.WriteAllText(tmp, rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // File.Replace gives us the atomic swap with backup in one syscall on NTFS.
        try
        {
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        catch (FileNotFoundException)
        {
            // Original didn't exist (first write) — fall back to a plain move.
            File.Move(tmp, path, overwrite: false);
        }
    }

    public string Render(ModListBlock block)
    {
        var sb = new StringBuilder(block.RawPrefix.Length + block.RawSuffix.Length + block.Entries.Count * 600);
        sb.Append(block.RawPrefix);
        sb.Append("mods = [");
        sb.Append(block.LineEnding);
        foreach (var entry in block.Entries)
        {
            WriteObjectBody(sb, entry.Fields, depth: 1, le: block.LineEnding, openWith: "{", closeWith: "}");
        }
        sb.Append(']');
        sb.Append(block.RawSuffix);
        return sb.ToString();
    }

    private static void WriteObjectBody(StringBuilder sb, List<KeyValuePair<string, RawValue>> fields, int depth, string le, string openWith, string closeWith)
    {
        var pad = new string('\t', depth);
        var inner = new string('\t', depth + 1);
        sb.Append(pad);
        sb.Append(openWith);
        sb.Append(le);
        foreach (var (key, value) in fields)
        {
            sb.Append(inner);
            sb.Append(key);
            sb.Append(" = ");
            WriteValue(sb, value, depth + 1, le);
            sb.Append(le);
        }
        sb.Append(pad);
        sb.Append(closeWith);
        sb.Append(le);
    }

    private static void WriteValue(StringBuilder sb, RawValue value, int depth, string le)
    {
        switch (value)
        {
            case RawValue.StringValue s:
                sb.Append('"');
                AppendEscaped(sb, s.Text);
                sb.Append('"');
                break;
            case RawValue.BoolValue b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case RawValue.NumberValue n:
                sb.Append(n.Text);
                break;
            case RawValue.ArrayValue arr:
                WriteArrayValue(sb, arr, depth, le);
                break;
            case RawValue.ObjectValue obj:
                // Nested objects: emit `{`, newline, fields, newline, indent, `}` — Stingray uses
                // the same brace-on-own-line style as it does for mod entries.
                sb.Append('{');
                sb.Append(le);
                var inner = new string('\t', depth + 1);
                foreach (var (k, v) in obj.Fields)
                {
                    sb.Append(inner);
                    sb.Append(k);
                    sb.Append(" = ");
                    WriteValue(sb, v, depth + 1, le);
                    sb.Append(le);
                }
                sb.Append(new string('\t', depth));
                sb.Append('}');
                break;
        }
    }

    private static void WriteArrayValue(StringBuilder sb, RawValue.ArrayValue arr, int depth, string le)
    {
        if (arr.Items.Count == 0) { sb.Append("[]"); return; }
        sb.Append('[');
        sb.Append(le);
        var inner = new string('\t', depth + 1);
        foreach (var item in arr.Items)
        {
            sb.Append(inner);
            WriteValue(sb, item, depth + 1, le);
            sb.Append(le);
        }
        sb.Append(new string('\t', depth));
        sb.Append(']');
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:   sb.Append(ch);     break;
            }
        }
    }

    private static void AssertWritable(string path)
    {
        if (!File.Exists(path)) return; // first write
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException(
                "user_settings.config is locked. Close Vermintide 2 and the Fatshark launcher, then try again.", ex);
        }
    }
}
