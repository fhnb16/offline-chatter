using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using OfflineChatter.Theming;

namespace OfflineChatter.Rendering;

/// <summary>
/// Лёгкая подсветка синтаксиса без внешних библиотек: комментарии, строки, числа, ключевые слова.
/// Цвета — из темы (CodeKeyword/CodeString/CodeComment/CodeNumber).
/// </summary>
public static class SyntaxHighlighter
{
    private enum Family { CLike, Python, Shell, Sql, Json, Markup, Plain }

    private static readonly Dictionary<Family, HashSet<string>> Keywords = new()
    {
        [Family.CLike] = Set("abstract as async await base bool break byte case catch char class const continue decimal default delegate do double else enum event explicit export extends extern false final finally fixed float for foreach fn func function goto if impl implements import in int interface internal is let long match mod module mut namespace new nil null object operator out override package private protected pub public readonly ref return sealed self short static string struct super switch this throw trait true try type typeof uint ulong unsafe use using var virtual void volatile where while yield get set value from select auto template typename include define std vector println"),
        [Family.Python] = Set("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield self print len range int str float list dict set tuple"),
        [Family.Shell] = Set("if then else elif fi for while do done case esac function in echo exit export local return set unset cd ls rm cp mv mkdir sudo apt git pip npm cat grep sed awk"),
        [Family.Sql] = Set("select from where and or not insert into values update set delete create table index view drop alter join left right inner outer on group by order having limit offset as distinct union all null is in like between case when then else end primary key foreign references"),
        [Family.Json] = Set("true false null"),
    };

    private static readonly Regex Token = new(
        @"(?<comment>//[^\n]*|/\*[\s\S]*?\*/|#[^\n]*|--[^\n]*|<!--[\s\S]*?-->)|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)|(?<number>\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static HashSet<string> Set(string s) => new(s.Split(' '), StringComparer.Ordinal);

    private static Family FamilyOf(string? lang) => (lang ?? string.Empty).ToLowerInvariant() switch
    {
        "py" or "python" or "python3" => Family.Python,
        "sh" or "bash" or "shell" or "zsh" or "ps" or "powershell" or "ps1" or "bat" or "cmd" => Family.Shell,
        "sql" or "mysql" or "postgres" or "sqlite" => Family.Sql,
        "json" or "jsonc" => Family.Json,
        "xml" or "html" or "xaml" or "svg" => Family.Markup,
        "" or "text" or "txt" or "plain" or "markdown" or "md" or "latex" or "tex" => Family.Plain,
        _ => Family.CLike,
    };

    public static IEnumerable<Inline> Highlight(string code, string? language)
    {
        var family = FamilyOf(language);
        if (family == Family.Plain || code.Length > 200_000)
        {
            yield return new Run(code);
            yield break;
        }

        Keywords.TryGetValue(family, out var keywords);
        var caseInsensitive = family == Family.Sql;
        var pos = 0;
        foreach (Match m in Token.Matches(code))
        {
            string? key = null;
            if (m.Groups["comment"].Success)
            {
                var c = m.Value;
                // '#' — комментарий только в python/shell; '--' — только в SQL
                if ((c.StartsWith("#", StringComparison.Ordinal) && family is not (Family.Python or Family.Shell)) ||
                    (c.StartsWith("--", StringComparison.Ordinal) && family != Family.Sql) ||
                    (c.StartsWith("<!--", StringComparison.Ordinal) && family != Family.Markup))
                    continue;
                key = ThemeKeys.CodeComment;
            }
            else if (m.Groups["string"].Success) key = ThemeKeys.CodeString;
            else if (m.Groups["number"].Success) key = ThemeKeys.CodeNumber;
            else if (m.Groups["word"].Success && keywords != null &&
                     keywords.Contains(caseInsensitive ? m.Value.ToLowerInvariant() : m.Value))
                key = ThemeKeys.CodeKeyword;
            if (key == null) continue;

            if (m.Index > pos) yield return new Run(code.Substring(pos, m.Index - pos));
            var run = new Run(m.Value);
            run.SetResourceReference(TextElement.ForegroundProperty, key);
            yield return run;
            pos = m.Index + m.Length;
        }
        if (pos < code.Length) yield return new Run(code.Substring(pos));
    }
}
