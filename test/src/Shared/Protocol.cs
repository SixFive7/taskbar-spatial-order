// The test app's command channel: one request line, one JSON response line.
//
// Request:  verb positional... key=value key="value with spaces"
// Response: {"ok":true,...} or {"ok":false,"error":"..."}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TsoShared;

public sealed class Command
{
    public string Verb = "";
    public readonly List<string> Args = new();
    public readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Window keys: a capital letter, optionally followed by one digit (A, B, Z1).</summary>
    public static readonly Regex KeyPattern = new("^[A-Z][0-9]?$", RegexOptions.CultureInvariant);

    public static Command Parse(string line)
    {
        var tokens = Tokenize(line ?? "");
        if (tokens.Count == 0)
        {
            throw new FormatException("empty command");
        }

        var cmd = new Command { Verb = tokens[0].ToLowerInvariant() };
        for (int i = 1; i < tokens.Count; i++)
        {
            string t = tokens[i];
            int eq = t.IndexOf('=');
            // key=value only when the key is a plain identifier; "x=1" is named, "-5" positional.
            if (eq > 0 && IsIdentifier(t.Substring(0, eq)))
            {
                cmd.Named[t.Substring(0, eq)] = t.Substring(eq + 1);
            }
            else
            {
                cmd.Args.Add(t);
            }
        }

        return cmd;
    }

    static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !char.IsLetter(s[0]))
        {
            return false;
        }
        foreach (char c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Splits on spaces; double quotes group, backslash escapes a quote or a backslash.
    /// Quotes may start mid-token (key="a b").</summary>
    public static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                {
                    cur.Append(line[++i]);
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    cur.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
                any = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (any)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                    any = false;
                }
            }
            else
            {
                cur.Append(c);
                any = true;
            }
        }

        if (inQuotes)
        {
            throw new FormatException("unterminated quote");
        }
        if (any)
        {
            result.Add(cur.ToString());
        }
        return result;
    }

    public static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '"', '\\', '\t', '=' }) < 0)
        {
            return s;
        }
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>A named argument for <see cref="Build"/>; skipped when the value is null.</summary>
    public readonly record struct Kv(string Key, object Value);

    /// <summary>Builds a request line. Parts are positional values or <see cref="Kv"/> pairs.</summary>
    public static string Build(string verb, params object[] parts)
    {
        var sb = new StringBuilder(verb);
        foreach (object p in parts)
        {
            if (p == null)
            {
                continue;
            }
            if (p is Kv kv)
            {
                if (kv.Value != null)
                {
                    sb.Append(' ').Append(kv.Key).Append('=').Append(Quote(Format(kv.Value)));
                }
            }
            else
            {
                sb.Append(' ').Append(Quote(Format(p)));
            }
        }
        return sb.ToString();
    }

    static string Format(object o) => o switch
    {
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => o.ToString(),
    };

    public string Arg(int i, string name = null)
    {
        if (i >= Args.Count)
        {
            throw new FormatException($"{Verb}: missing argument {i + 1}{(name != null ? " (" + name + ")" : "")}");
        }
        return Args[i];
    }

    public int ArgInt(int i, string name = null) => ParseInt(Arg(i, name), name ?? $"argument {i + 1}");

    public string Key(int i = 0)
    {
        string k = Arg(i, "window key").ToUpperInvariant();
        if (!KeyPattern.IsMatch(k))
        {
            throw new FormatException($"{Verb}: bad window key '{k}'");
        }
        return k;
    }

    public string Opt(string name, string fallback = null) => Named.TryGetValue(name, out string v) ? v : fallback;

    public int OptInt(string name, int fallback) => Named.TryGetValue(name, out string v) ? ParseInt(v, name) : fallback;

    public bool OptBool(string name, bool fallback = false)
    {
        if (!Named.TryGetValue(name, out string v))
        {
            return fallback;
        }
        return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static int ParseInt(string s, string what)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
        {
            return hex;
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        {
            return v;
        }
        throw new FormatException($"bad integer for {what}: '{s}'");
    }

    /// <summary>Parses RRGGBB (optionally with #) to 0x00RRGGBB.</summary>
    public static int ParseRgb(string s)
    {
        string t = s.TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            throw new FormatException($"bad colour '{s}', expected RRGGBB");
        }
        return v;
    }
}
