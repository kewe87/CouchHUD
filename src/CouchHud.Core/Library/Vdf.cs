using System.Text;

namespace CouchHud.Core.Library;

/// <summary>
/// Minimal parser for Valve's text KeyValues format (.vdf, .acf).
/// Values are either strings or nested <see cref="VdfObject"/>s; key lookup ignores case.
/// </summary>
public sealed class VdfObject : Dictionary<string, object>
{
    public VdfObject() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    public VdfObject? GetObject(string key) => TryGetValue(key, out var v) ? v as VdfObject : null;
}

public static class Vdf
{
    /// <summary>
    /// Deeper nesting than any Steam file has (localconfig.vdf goes about ten levels deep). The files are written by Steam into
    /// user-writable folders, so a crafted one must end in a <see cref="FormatException"/>, never in a stack overflow.
    /// </summary>
    public const int MaxDepth = 64;

    public static VdfObject Parse(string text)
    {
        var reader = new Reader(text);
        return reader.ReadObject(expectClose: false, depth: 0);
    }

    private sealed class Reader(string text)
    {
        private int _i;

        private enum Kind { String, Open, Close }

        public VdfObject ReadObject(bool expectClose, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("VDF: nested too deep");
            var obj = new VdfObject();
            while (true)
            {
                var key = Next();
                if (key is null)
                {
                    if (expectClose) throw new FormatException("VDF: unexpected end of input");
                    return obj;
                }
                if (key.Value.Kind == Kind.Close)
                {
                    if (!expectClose) throw new FormatException("VDF: unexpected }");
                    return obj;
                }
                if (key.Value.Kind != Kind.String) throw new FormatException("VDF: expected key");

                var value = Next() ?? throw new FormatException($"VDF: missing value for {key.Value.Text}");
                obj[key.Value.Text] = value.Kind switch
                {
                    Kind.Open => ReadObject(expectClose: true, depth + 1),
                    Kind.String => value.Text,
                    _ => throw new FormatException($"VDF: unexpected }} after {key.Value.Text}")
                };
            }
        }

        private (Kind Kind, string Text)? Next()
        {
            SkipWhitespaceAndComments();
            if (_i >= text.Length) return null;

            var c = text[_i];
            if (c == '{')
            {
                _i++;
                return (Kind.Open, "{");
            }
            if (c == '}')
            {
                _i++;
                return (Kind.Close, "}");
            }

            var sb = new StringBuilder();
            if (c == '"')
            {
                _i++;
                while (_i < text.Length && text[_i] != '"')
                {
                    if (text[_i] == '\\' && _i + 1 < text.Length)
                    {
                        var n = text[_i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                        _i += 2;
                    }
                    else
                    {
                        sb.Append(text[_i++]);
                    }
                }
                _i++; // closing quote
                return (Kind.String, sb.ToString());
            }

            while (_i < text.Length && !char.IsWhiteSpace(text[_i]) && text[_i] is not ('{' or '}' or '"'))
            {
                sb.Append(text[_i++]);
            }
            return (Kind.String, sb.ToString());
        }

        private void SkipWhitespaceAndComments()
        {
            while (_i < text.Length)
            {
                if (char.IsWhiteSpace(text[_i]))
                {
                    _i++;
                }
                else if (text[_i] == '/' && _i + 1 < text.Length && text[_i + 1] == '/')
                {
                    while (_i < text.Length && text[_i] != '\n') _i++;
                }
                else
                {
                    break;
                }
            }
        }
    }
}
