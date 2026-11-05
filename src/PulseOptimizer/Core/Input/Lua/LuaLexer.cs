using System.Globalization;
using System.Text;

namespace Pulse.Core.Input.Lua;

public enum Tok
{
    Eof, Name, Number, String,
    // keywords
    And, Break, Do, Else, Elseif, End, False, For, Function, Goto, If, In, Local, Nil, Not, Or, Repeat, Return, Then, True, Until, While,
    // symbols
    Plus, Minus, Star, Slash, DSlash, Percent, Caret, Hash, Amp, Tilde, Pipe, Shl, Shr,
    Eq, Ne, Le, Ge, Lt, Gt, Assign, LParen, RParen, LBrace, RBrace, LBracket, RBracket, DColon,
    Semi, Colon, Comma, Dot, Concat, Dots,
}

public readonly record struct Token(Tok Kind, string Text, double Number, int Line);

/// <summary>A script error, with the line it happened on (shown in the macro's log like Logitech's).</summary>
public sealed class LuaException : Exception
{
    public int Line { get; }
    public LuaException(string message, int line = 0) : base(line > 0 ? $"line {line}: {message}" : message) { Line = line; }
}

/// <summary>Turns Lua 5.1 source (what Logitech G Hub scripts are written in) into tokens.</summary>
public static class LuaLexer
{
    static readonly Dictionary<string, Tok> Keywords = new()
    {
        ["and"] = Tok.And, ["break"] = Tok.Break, ["do"] = Tok.Do, ["else"] = Tok.Else, ["elseif"] = Tok.Elseif,
        ["end"] = Tok.End, ["false"] = Tok.False, ["for"] = Tok.For, ["function"] = Tok.Function, ["goto"] = Tok.Goto,
        ["if"] = Tok.If, ["in"] = Tok.In, ["local"] = Tok.Local, ["nil"] = Tok.Nil, ["not"] = Tok.Not, ["or"] = Tok.Or,
        ["repeat"] = Tok.Repeat, ["return"] = Tok.Return, ["then"] = Tok.Then, ["true"] = Tok.True, ["until"] = Tok.Until,
        ["while"] = Tok.While,
    };

    public static List<Token> Lex(string src)
    {
        var list = new List<Token>();
        int i = 0, line = 1;
        while (true)
        {
            // whitespace and comments
            while (i < src.Length)
            {
                char c = src[i];
                if (c == '\n') { line++; i++; }
                else if (char.IsWhiteSpace(c)) i++;
                else if (c == '-' && i + 1 < src.Length && src[i + 1] == '-')
                {
                    i += 2;
                    if (i < src.Length && src[i] == '[' && LongBracket(src, i, out int lvl))
                    {
                        var (_, end) = ReadLong(src, i, lvl, ref line);
                        i = end;
                    }
                    else while (i < src.Length && src[i] != '\n') i++;
                }
                else break;
            }
            if (i >= src.Length) { list.Add(new Token(Tok.Eof, "", 0, line)); return list; }
            char ch = src[i];
            int start = i;
            if (char.IsLetter(ch) || ch == '_')
            {
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                var word = src[start..i];
                list.Add(new Token(Keywords.TryGetValue(word, out var k) ? k : Tok.Name, word, 0, line));
                continue;
            }
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                double value;
                if (ch == '0' && i + 1 < src.Length && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    i += 2;
                    int hs = i;
                    while (i < src.Length && Uri.IsHexDigit(src[i])) i++;
                    value = long.Parse(src[hs..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else
                {
                    while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;
                    if (i < src.Length && (src[i] == 'e' || src[i] == 'E'))
                    {
                        i++;
                        if (i < src.Length && (src[i] == '+' || src[i] == '-')) i++;
                        while (i < src.Length && char.IsDigit(src[i])) i++;
                    }
                    if (!double.TryParse(src[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        throw new LuaException($"bad number '{src[start..i]}'", line);
                }
                list.Add(new Token(Tok.Number, src[start..i], value, line));
                continue;
            }
            if (ch == '"' || ch == '\'')
            {
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= src.Length || src[i] == '\n') throw new LuaException("unfinished string", line);
                    char c = src[i++];
                    if (c == ch) break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= src.Length) throw new LuaException("unfinished string", line);
                    char e = src[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'a': sb.Append('\a'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'v': sb.Append('\v'); break;
                        case '\n': sb.Append('\n'); line++; break;
                        default:
                            if (char.IsDigit(e))
                            {
                                int n = e - '0', d = 1;
                                while (d < 3 && i < src.Length && char.IsDigit(src[i])) { n = n * 10 + (src[i++] - '0'); d++; }
                                sb.Append((char)n);
                            }
                            else sb.Append(e); // \\ \" \' and anything else
                            break;
                    }
                }
                list.Add(new Token(Tok.String, sb.ToString(), 0, line));
                continue;
            }
            if (ch == '[' && LongBracket(src, i, out int level))
            {
                int l0 = line;
                var (text, end) = ReadLong(src, i, level, ref line);
                i = end;
                list.Add(new Token(Tok.String, text, 0, l0));
                continue;
            }
            // symbols, longest first
            string three = i + 3 <= src.Length ? src.Substring(i, 3) : "";
            string two = i + 2 <= src.Length ? src.Substring(i, 2) : "";
            if (three == "...") { list.Add(new Token(Tok.Dots, three, 0, line)); i += 3; continue; }
            Tok? t2 = two switch
            {
                "==" => Tok.Eq, "~=" => Tok.Ne, "<=" => Tok.Le, ">=" => Tok.Ge, ".." => Tok.Concat, "::" => Tok.DColon,
                "//" => Tok.DSlash, "<<" => Tok.Shl, ">>" => Tok.Shr, _ => null,
            };
            if (t2 != null) { list.Add(new Token(t2.Value, two, 0, line)); i += 2; continue; }
            Tok t1 = ch switch
            {
                '+' => Tok.Plus, '-' => Tok.Minus, '*' => Tok.Star, '/' => Tok.Slash, '%' => Tok.Percent, '^' => Tok.Caret,
                '#' => Tok.Hash, '&' => Tok.Amp, '~' => Tok.Tilde, '|' => Tok.Pipe, '<' => Tok.Lt, '>' => Tok.Gt,
                '=' => Tok.Assign, '(' => Tok.LParen, ')' => Tok.RParen, '{' => Tok.LBrace, '}' => Tok.RBrace,
                '[' => Tok.LBracket, ']' => Tok.RBracket, ';' => Tok.Semi, ':' => Tok.Colon, ',' => Tok.Comma, '.' => Tok.Dot,
                _ => throw new LuaException($"unexpected character '{ch}'", line),
            };
            list.Add(new Token(t1, ch.ToString(), 0, line));
            i++;
        }
    }

    static bool LongBracket(string s, int i, out int level)
    {
        level = 0;
        int j = i + 1;
        while (j < s.Length && s[j] == '=') { level++; j++; }
        return j < s.Length && s[j] == '[';
    }

    static (string Text, int End) ReadLong(string s, int i, int level, ref int line)
    {
        int start = i + level + 2;
        if (start < s.Length && s[start] == '\n') { start++; line++; }
        string close = "]" + new string('=', level) + "]";
        int end = s.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0) throw new LuaException("unfinished long string or comment", line);
        var text = s[start..end];
        line += text.Count(c => c == '\n');
        return (text, end + close.Length);
    }
}
