using System.Text;

namespace SimpleArchitectureMcp.Server.Core.Markdown;

/// <summary>Inline Markdown parser. A literal '\n' in the input denotes a hard line break.</summary>
public static class InlineParser
{
    public static List<MdInline> Parse(string s)
    {
        var result = new List<MdInline>();
        var text = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (text.Length > 0)
            {
                result.Add(new MdText(text.ToString()));
                text.Clear();
            }
        }

        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '\\' when i + 1 < s.Length && char.IsAsciiLetterOrDigit(s[i + 1]) is false && !char.IsWhiteSpace(s[i + 1]):
                    text.Append(s[i + 1]);
                    i += 2;
                    continue;

                case '&' when HtmlEntities.TryDecode(s, i, out var entity, out var entityLength):
                    text.Append(entity);
                    i += entityLength;
                    continue;

                case '\n':
                    Flush();
                    result.Add(new MdLineBreak());
                    i++;
                    continue;

                case '`':
                {
                    var run = RunLength(s, i, '`');
                    var close = FindBacktickRun(s, i + run, run);
                    if (close < 0)
                    {
                        text.Append('`', run);
                        i += run;
                        continue;
                    }

                    var code = s[(i + run)..close].Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    {
                        code = code[1..^1];
                    }

                    Flush();
                    result.Add(new MdCode(code));
                    i = close + run;
                    continue;
                }

                case '!' when i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, out var alt, out var src, out var imgEnd):
                    Flush();
                    result.Add(new MdImage(src, Unescape(alt)));
                    i = imgEnd;
                    continue;

                case '[' when TryLink(s, i, out var label, out var href, out var linkEnd):
                    Flush();
                    result.Add(new MdLink(href, Parse(label)));
                    i = linkEnd;
                    continue;

                case '<':
                {
                    var gt = s.IndexOf('>', i + 1);
                    if (gt > 0)
                    {
                        var inner = s[(i + 1)..gt];
                        if (IsBreakTag(inner))
                        {
                            Flush();
                            result.Add(new MdLineBreak());
                            i = gt + 1;
                            continue;
                        }

                        if ((inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                            && !inner.Any(char.IsWhiteSpace))
                        {
                            Flush();
                            result.Add(new MdLink(inner, [new MdText(inner)]));
                            i = gt + 1;
                            continue;
                        }
                    }

                    break;
                }

                case '*' or '_' when TryEmphasis(s, i, out var node, out var emEnd):
                    Flush();
                    result.Add(node);
                    i = emEnd;
                    continue;

                case '~' when i + 1 < s.Length && s[i + 1] == '~' && i + 2 < s.Length && !char.IsWhiteSpace(s[i + 2]):
                {
                    var close = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (close > i + 2 && !char.IsWhiteSpace(s[close - 1]))
                    {
                        Flush();
                        result.Add(new MdStrike(Parse(s[(i + 2)..close])));
                        i = close + 2;
                        continue;
                    }

                    break;
                }
            }

            // Literal character (also the fallback for unmatched delimiters).
            var len = c is '*' or '_' or '`' ? RunLength(s, i, c) : 1;
            text.Append(s, i, len);
            i += len;
        }

        Flush();
        return result;
    }

    private static bool TryEmphasis(string s, int i, out MdInline node, out int end)
    {
        node = null!;
        end = 0;
        var d = s[i];
        var run = Math.Min(RunLength(s, i, d), 3);
        var after = i + run;
        if (after >= s.Length || char.IsWhiteSpace(s[after]))
        {
            return false; // not left-flanking
        }

        if (d == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1]))
        {
            return false; // intraword underscore is literal
        }

        for (var j = after; j < s.Length; j++)
        {
            if (s[j] == '\\')
            {
                j++;
                continue;
            }

            if (s[j] == '`')
            {
                var r = RunLength(s, j, '`');
                var close = FindBacktickRun(s, j + r, r);
                if (close > 0)
                {
                    j = close + r - 1;
                    continue;
                }
            }

            if (s[j] != d)
            {
                continue;
            }

            var m = RunLength(s, j, d);
            if (char.IsWhiteSpace(s[j - 1]) || (d == '_' && j + m < s.Length && char.IsLetterOrDigit(s[j + m])))
            {
                j += m - 1;
                continue;
            }

            int closeAt;
            if (run == 3 && m >= 3)
            {
                closeAt = j + m - 3;
            }
            else if (run == 2 && m >= 2)
            {
                closeAt = j + m - 2; // "**a *b***": the first '*' of the run closes the inner emphasis
            }
            else if (run == 1 && m is 1 or 3)
            {
                closeAt = j + m - 1;
            }
            else
            {
                j += m - 1;
                continue;
            }

            var inner = Parse(s[after..closeAt]);
            node = run switch
            {
                3 => new MdStrong([new MdEmphasis(inner)]),
                2 => new MdStrong(inner),
                _ => new MdEmphasis(inner),
            };
            end = closeAt + run;
            return true;
        }

        return false;
    }

    /// <summary>Parses "[label](dest "title")" starting at '['.</summary>
    private static bool TryLink(string s, int i, out string label, out string url, out int end)
    {
        label = url = string.Empty;
        end = 0;
        var depth = 0;
        var close = -1;
        for (var j = i; j < s.Length; j++)
        {
            if (s[j] == '\\')
            {
                j++;
            }
            else if (s[j] == '[')
            {
                depth++;
            }
            else if (s[j] == ']' && --depth == 0)
            {
                close = j;
                break;
            }
        }

        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
        {
            return false;
        }

        var p = close + 2;
        while (p < s.Length && s[p] == ' ')
        {
            p++;
        }

        if (p < s.Length && s[p] == '<')
        {
            var gt = s.IndexOf('>', p + 1);
            if (gt < 0)
            {
                return false;
            }

            url = s[(p + 1)..gt];
            p = gt + 1;
        }
        else
        {
            var start = p;
            var parens = 0;
            while (p < s.Length && !char.IsWhiteSpace(s[p]))
            {
                if (s[p] == '(')
                {
                    parens++;
                }
                else if (s[p] == ')')
                {
                    if (parens == 0)
                    {
                        break;
                    }

                    parens--;
                }

                p++;
            }

            url = s[start..p];
        }

        while (p < s.Length && s[p] == ' ')
        {
            p++;
        }

        if (p < s.Length && s[p] is '"' or '\'')
        {
            var q = s.IndexOf(s[p], p + 1);
            if (q < 0)
            {
                return false;
            }

            p = q + 1;
            while (p < s.Length && s[p] == ' ')
            {
                p++;
            }
        }

        if (p >= s.Length || s[p] != ')')
        {
            return false;
        }

        label = s[(i + 1)..close];
        url = Unescape(url);
        end = p + 1;
        return true;
    }

    private static bool IsBreakTag(string inner)
    {
        var t = inner.Replace(" ", string.Empty).TrimEnd('/');
        return t.Equals("br", StringComparison.OrdinalIgnoreCase);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\' && k + 1 < s.Length && char.IsAsciiLetterOrDigit(s[k + 1]) is false && !char.IsWhiteSpace(s[k + 1]))
            {
                k++;
            }

            sb.Append(s[k]);
        }

        return sb.ToString();
    }

    private static int RunLength(string s, int i, char c)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == c)
        {
            n++;
        }

        return n;
    }

    private static int FindBacktickRun(string s, int from, int length)
    {
        for (var j = from; j < s.Length; j++)
        {
            if (s[j] != '`')
            {
                continue;
            }

            var r = RunLength(s, j, '`');
            if (r == length)
            {
                return j;
            }

            j += r - 1;
        }

        return -1;
    }
}
