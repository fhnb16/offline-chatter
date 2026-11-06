using System;
using System.Text;

namespace OfflineChatter.Core.Text;

/// <summary>Подготовка Markdown от моделей: разделители формул, границы стабильных блоков при стриминге.</summary>
public static class MarkdownText
{
    /// <summary>
    /// \( … \) → $…$, \[ … \] → $$…$$ (так их понимает расширение Mathematics в Markdig).
    /// Внутри блоков кода и `инлайн-кода` ничего не меняется.
    /// </summary>
    public static string NormalizeMath(string md)
    {
        md = NormalizeMathCore(md);
        return md.IndexOf("$$", StringComparison.Ordinal) >= 0 ? SplitSingleLineDisplayMath(md) : md;
    }

    /// <summary>
    /// «$$x = 2$$» одной строкой Markdig считает строчной формулой; модели пишут так постоянно —
    /// превращаем в блочную (на отдельных строках), чтобы она была по центру и крупнее.
    /// </summary>
    private static string SplitSingleLineDisplayMath(string md)
    {
        var lines = md.Split('\n');
        var inFence = false;
        var sb = new StringBuilder(md.Length + 16);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsFence(line)) inFence = !inFence;
            var t = line.Trim();
            if (!inFence && t.Length > 4 && t.StartsWith("$$", StringComparison.Ordinal) && t.EndsWith("$$", StringComparison.Ordinal) &&
                t.IndexOf("$$", 2, StringComparison.Ordinal) == t.Length - 2)
            {
                sb.Append("$$\n").Append(t.Substring(2, t.Length - 4).Trim()).Append("\n$$");
            }
            else
            {
                sb.Append(line);
            }
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string NormalizeMathCore(string md)
    {
        if (md.IndexOf('\\') < 0) return md;
        var sb = new StringBuilder(md.Length + 16);
        var inFence = false;
        var lines = md.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (IsFence(line))
            {
                inFence = !inFence;
                sb.Append(line);
            }
            else if (inFence)
            {
                sb.Append(line);
            }
            else
            {
                var inCode = false;
                for (var i = 0; i < line.Length; i++)
                {
                    var c = line[i];
                    if (c == '`')
                    {
                        inCode = !inCode;
                        sb.Append(c);
                        continue;
                    }
                    if (!inCode && c == '\\' && i + 1 < line.Length)
                    {
                        var n = line[i + 1];
                        if (n is '(' or ')')
                        {
                            sb.Append('$');
                            i++;
                            continue;
                        }
                        if (n is '[' or ']')
                        {
                            sb.Append("$$");
                            i++;
                            continue;
                        }
                    }
                    sb.Append(c);
                }
            }
            if (li < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsFence(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary>
    /// Граница «устоявшейся» части текста при стриминге: позиция после последней пустой строки
    /// вне блока кода и вне $$-формулы. Всё до неё больше не изменится и рендерится один раз.
    /// </summary>
    public static int StableBoundary(string md)
    {
        var inFence = false;
        var inDisplayMath = false;
        var boundary = 0;
        var pos = 0;
        var prevBlank = false;
        while (pos < md.Length)
        {
            var nl = md.IndexOf('\n', pos);
            if (nl < 0) break; // последняя строка ещё дописывается
            var line = md.Substring(pos, nl - pos).TrimEnd('\r');
            if (IsFence(line)) inFence = !inFence;
            else if (!inFence && CountDisplayMath(line) % 2 == 1) inDisplayMath = !inDisplayMath;
            var blank = line.Trim().Length == 0;
            if (blank && !inFence && !inDisplayMath && !prevBlank) boundary = nl + 1;
            prevBlank = blank;
            pos = nl + 1;
        }
        return boundary;
    }

    private static int CountDisplayMath(string line)
    {
        var count = 0;
        for (var i = line.IndexOf("$$", StringComparison.Ordinal); i >= 0; i = line.IndexOf("$$", i + 2, StringComparison.Ordinal)) count++;
        return count;
    }
}
