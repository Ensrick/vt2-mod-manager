using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vt2ModManager.Services;

/// <summary>
/// Node in a Valve KeyValues / ACF tree (used for libraryfolders.vdf and appworkshop_*.acf).
/// Not used for VT2's user_settings.config — that's a different Stingray format; see
/// <see cref="UserSettingsConfigReader"/>.
/// </summary>
public sealed class AcfNode
{
    private readonly string? _scalar;
    private readonly Dictionary<string, AcfNode>? _children;

    private AcfNode(string scalar) { _scalar = scalar; }
    private AcfNode(Dictionary<string, AcfNode> children) { _children = children; }

    public bool IsObject => _children is not null;
    public bool IsScalar => _scalar is not null;

    public IReadOnlyDictionary<string, AcfNode> Children =>
        _children ?? (IReadOnlyDictionary<string, AcfNode>)EmptyDict;

    private static readonly Dictionary<string, AcfNode> EmptyDict = new();

    public AcfNode? this[string key]
    {
        get
        {
            if (_children is null) throw new InvalidOperationException(
                "Indexer called on a scalar AcfNode. Use AsString()/AsLong() instead.");
            return _children.TryGetValue(key, out var v) ? v : null;
        }
    }

    public string AsString() => _scalar ?? throw new InvalidOperationException(
        "AsString() called on an object AcfNode.");

    public long AsLong() => long.TryParse(AsString(), out var v) ? v : 0;
    public ulong AsULong() => ulong.TryParse(AsString(), out var v) ? v : 0;

    public static AcfNode Scalar(string value) => new(value);
    public static AcfNode Object(Dictionary<string, AcfNode> children) => new(children);

    public static AcfNode Parse(string text)
    {
        var p = new Parser(text);
        p.SkipWhitespace();
        if (p.Eof) throw new FormatException("ACF input is empty.");

        _ = p.ReadToken();
        p.SkipWhitespace();
        p.Expect('{');
        return p.ParseObject();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;
        public Parser(string text) { _text = text; }

        public bool Eof => _pos >= _text.Length;

        public AcfNode ParseObject()
        {
            var dict = new Dictionary<string, AcfNode>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (Eof) return AcfNode.Object(dict);
                if (_text[_pos] == '}') { _pos++; return AcfNode.Object(dict); }

                var key = ReadToken();
                SkipWhitespace();
                if (Eof) { dict[key] = AcfNode.Scalar(""); return AcfNode.Object(dict); }

                if (_text[_pos] == '{')
                {
                    _pos++;
                    dict[key] = ParseObject();
                }
                else
                {
                    dict[key] = AcfNode.Scalar(ReadToken());
                }
            }
        }

        public string ReadToken()
        {
            SkipWhitespace();
            if (Eof) return string.Empty;
            return _text[_pos] == '"' ? ReadQuoted() : ReadBareword();
        }

        private string ReadQuoted()
        {
            _pos++;
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[_pos++];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && !Eof)
                {
                    var esc = _text[_pos++];
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
            return sb.ToString();
        }

        private string ReadBareword()
        {
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch) || ch == '{' || ch == '}' || ch == '"') break;
                sb.Append(ch);
                _pos++;
            }
            return sb.ToString();
        }

        public void SkipWhitespace()
        {
            while (!Eof)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch)) { _pos++; }
                else if (ch == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (!Eof && _text[_pos] != '\n') _pos++;
                }
                else
                {
                    break;
                }
            }
        }

        public void Expect(char c)
        {
            if (Eof || _text[_pos] != c)
                throw new FormatException($"Expected '{c}' at position {_pos}.");
            _pos++;
        }
    }

    public static AcfNode ParseFile(string path) => Parse(File.ReadAllText(path));
}
