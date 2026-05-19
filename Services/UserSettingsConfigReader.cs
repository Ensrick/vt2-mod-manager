using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vt2ModManager.Services;

/// <summary>
/// Surgically extracts the <c>mods = [ ... ]</c> block from VT2's user_settings.config.
/// Everything before/after that block is preserved as raw text so writers can round-trip
/// the file without touching unrelated user settings.
///
/// The format is Stingray's custom config: bareword keys, <c>= value</c>, multiline quoted
/// strings, nested objects and arrays. Numbers and bool keywords are kept as raw tokens to
/// avoid formatting drift.
/// </summary>
public sealed class UserSettingsConfigReader
{
    public ModListBlock ReadFile(string path) => ReadText(File.ReadAllText(path));

    public ModListBlock ReadText(string text)
    {
        var span = FindModsArraySpan(text);
        if (span is null)
            throw new FormatException("Could not find 'mods = [' in user_settings.config.");
        var (keywordStart, afterOpenBracket) = span.Value;

        // Position the parser at the byte immediately after the `[`.
        var p = new Parser(text, afterOpenBracket);
        var entries = p.ParseModArray();
        // p.Pos is now just past the matching `]`.

        // Prefix stops at the `mods` keyword; suffix begins after the closing `]`. The
        // writer re-emits the `mods = [ ... ]` block itself, so neither side contains it.
        var prefix = text.Substring(0, keywordStart);
        var suffix = text.Substring(p.Pos);

        var le = text.Contains("\r\n") ? "\r\n" : "\n";

        return new ModListBlock
        {
            RawPrefix = prefix,
            RawSuffix = suffix,
            LineEnding = le,
            Entries = entries,
        };
    }

    /// <summary>
    /// Locates the `mods` keyword and the byte just past the opening `[`. Tolerates whitespace
    /// between `mods`, `=`, and `[`. Returns null if not found. Skips past unrelated keys
    /// (including `mod_settings`, which would prefix-collide on a naive scan).
    /// </summary>
    internal static (int keywordStart, int afterOpenBracket)? FindModsArraySpan(string text)
    {
        // Look for a line whose first non-whitespace token is exactly `mods`, followed by `=` `[`.
        // The keyword `mod_settings` is a different top-level key — we must not match it.
        int i = 0;
        while (i < text.Length)
        {
            // Skip to next non-whitespace.
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            // Read bareword.
            int wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=' && text[i] != '"' && text[i] != '{' && text[i] != '[' && text[i] != '}' && text[i] != ']') i++;
            var word = text.Substring(wordStart, i - wordStart);

            if (word == "mods")
            {
                // Expect `= [`.
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i < text.Length && text[i] == '[')
                    {
                        return (wordStart, i + 1);
                    }
                }
                continue;
            }

            // Skip past `= <value>` so we don't accidentally match `mods` inside another value.
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            if (text[i] == '=')
            {
                i++;
                SkipValue(text, ref i);
            }
        }
        return null;
    }

    private static void SkipValue(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return;
        var ch = text[i];
        if (ch == '"') { SkipQuoted(text, ref i); return; }
        if (ch == '[') { SkipBracketed(text, ref i, '[', ']'); return; }
        if (ch == '{') { SkipBracketed(text, ref i, '{', '}'); return; }
        // bareword
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '}' && text[i] != ']') i++;
    }

    private static void SkipQuoted(string text, ref int i)
    {
        i++; // consume opening "
        while (i < text.Length)
        {
            var ch = text[i++];
            if (ch == '\\' && i < text.Length) { i++; continue; }
            if (ch == '"') return;
        }
    }

    private static void SkipBracketed(string text, ref int i, char open, char close)
    {
        i++; // consume opener
        int depth = 1;
        while (i < text.Length && depth > 0)
        {
            var ch = text[i];
            if (ch == '"') { SkipQuoted(text, ref i); continue; }
            if (ch == open) depth++;
            else if (ch == close) depth--;
            i++;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        public int Pos;

        public Parser(string text, int pos) { _text = text; Pos = pos; }

        private bool Eof => Pos >= _text.Length;

        public List<ModEntry> ParseModArray()
        {
            // Caller positioned us just past `[`.
            var entries = new List<ModEntry>();
            while (true)
            {
                SkipWhitespace();
                if (Eof) throw new FormatException("Unexpected EOF inside mods array.");
                if (_text[Pos] == ']') { Pos++; return entries; }

                if (_text[Pos] != '{')
                    throw new FormatException($"Expected '{{' inside mods array at position {Pos} (got '{_text[Pos]}').");
                Pos++; // consume {
                var entry = new ModEntry();
                ParseFieldsInto(entry.Fields);
                entries.Add(entry);
            }
        }

        // Parse `key = value` pairs until a closing `}`.
        private void ParseFieldsInto(List<KeyValuePair<string, RawValue>> fields)
        {
            while (true)
            {
                SkipWhitespace();
                if (Eof) throw new FormatException("Unexpected EOF inside object.");
                if (_text[Pos] == '}') { Pos++; return; }

                var key = ReadBareword();
                if (key.Length == 0) throw new FormatException($"Expected field name at position {Pos}.");

                SkipWhitespace();
                Expect('=');
                SkipWhitespace();

                var value = ParseValue();
                fields.Add(new KeyValuePair<string, RawValue>(key, value));
            }
        }

        private RawValue ParseValue()
        {
            SkipWhitespace();
            if (Eof) throw new FormatException("Unexpected EOF reading value.");
            var ch = _text[Pos];
            if (ch == '"') return new RawValue.StringValue(ReadQuoted());
            if (ch == '[') return ParseArray();
            if (ch == '{') return ParseObject();
            var token = ReadBareword();
            if (token == "true")  return new RawValue.BoolValue(true);
            if (token == "false") return new RawValue.BoolValue(false);
            return new RawValue.NumberValue(token);
        }

        private RawValue.ArrayValue ParseArray()
        {
            Pos++; // consume [
            var items = new List<RawValue>();
            while (true)
            {
                SkipWhitespace();
                if (Eof) throw new FormatException("Unexpected EOF inside array.");
                if (_text[Pos] == ']') { Pos++; return new RawValue.ArrayValue(items); }
                items.Add(ParseValue());
            }
        }

        private RawValue.ObjectValue ParseObject()
        {
            Pos++; // consume {
            var fields = new List<KeyValuePair<string, RawValue>>();
            ParseFieldsInto(fields);
            return new RawValue.ObjectValue(fields);
        }

        private string ReadQuoted()
        {
            Pos++; // consume opening "
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[Pos++];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && !Eof)
                {
                    var esc = _text[Pos++];
                    sb.Append(esc switch
                    {
                        'n'  => '\n',
                        't'  => '\t',
                        'r'  => '\r',
                        '"'  => '"',
                        '\\' => '\\',
                        _    => esc,
                    });
                }
                else
                {
                    sb.Append(ch);
                }
            }
            throw new FormatException("Unterminated quoted string.");
        }

        private string ReadBareword()
        {
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[Pos];
                if (char.IsWhiteSpace(ch) || ch == '=' || ch == '{' || ch == '}' || ch == '[' || ch == ']' || ch == '"') break;
                sb.Append(ch);
                Pos++;
            }
            return sb.ToString();
        }

        private void SkipWhitespace()
        {
            while (!Eof && char.IsWhiteSpace(_text[Pos])) Pos++;
        }

        private void Expect(char c)
        {
            if (Eof || _text[Pos] != c)
                throw new FormatException($"Expected '{c}' at position {Pos}.");
            Pos++;
        }
    }
}
