using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Chat;

/// <summary>
/// Название чата: короткий запрос к модели после первого ответа; если модель не ответила или ответила не по делу —
/// первые слова вопроса.
/// </summary>
public static class ChatTitles
{
    public const int MaxLength = 60;
    private const int FallbackLength = 48;
    private const int MaxWords = 12;

    private const string Instruction =
        "You name chat conversations. Reply with the title only: 2 to 6 words in the language of the user's message, " +
        "no quotes, no emoji, no period at the end.";

    /// <summary>Название из первых слов вопроса (без разметки Markdown); null — в вопросе нет текста.</summary>
    public static string? FromText(string? text)
    {
        var plain = Plain(text);
        if (plain.Length == 0) return null;
        return Cut(plain, FallbackLength);
    }

    /// <summary>
    /// Запрос названия. Без размышлений, без инструментов и с маленьким лимитом: это служебный запрос,
    /// он не должен долго занимать модель.
    /// </summary>
    public static ChatRequest BuildRequest(string question, string? answer, string? model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("User message:").AppendLine(Clip(question, 1200));
        if (!string.IsNullOrWhiteSpace(answer)) sb.AppendLine().AppendLine("Assistant answer (start):").AppendLine(Clip(answer!, 600));
        sb.AppendLine().Append("Title:");

        var request = new ChatRequest
        {
            Model = model,
            Temperature = 0.2,
            MaxTokens = 64,
            Reasoning = ReasoningLevel.Off,
        };
        // Маленькие модели пропускают «на языке пользователя» — язык называем прямо
        var instruction = IsMostlyCyrillic(question) ? Instruction + " The user writes in Russian: the title must be in Russian." : Instruction;
        request.Messages.Add(new ChatMessage { Role = ChatRole.System, Content = instruction });
        request.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = sb.ToString() });
        return request;
    }

    private static readonly Regex Think = new(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Prefix = new(@"^(title|chat title|conversation title|заголовок|название|тема)\s*[:：\-—]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Название из ответа модели: без размышлений, вступлений («Here is a title:»), префиксов, кавычек, разметки и точки
    /// в конце. null — ответ не похож на название (пустой или целый абзац).
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = Think.Replace(raw!, string.Empty);
        var close = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0) text = text.Substring(close + 8);

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // Вступление вида «Here is a short title:» — название в следующей строке
        var line = lines.FirstOrDefault(l => !l.EndsWith(":", StringComparison.Ordinal) || lines.Count == 1);
        if (line == null) return null;

        line = Prefix.Replace(line, string.Empty);
        line = line.Trim().TrimStart('#', '-', '*', '>', ' ').Trim();
        line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        line = line.Trim('"', '\'', '«', '»', '“', '”', '„', '‘', '’', ' ');
        line = line.TrimEnd('.', '!', ':', ';', ',', '…', ' ').Trim('"', '\'', '«', '»', '“', '”', ' ');
        line = Collapse(line);

        if (line.Length == 0 || line.Split(' ').Length > MaxWords) return null;
        return Cut(line, MaxLength);
    }

    // ---------------- Вспомогательное ----------------

    private static readonly Regex CodeBlock = new(@"```[\s\S]*?(```|$)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    /// <summary>Текст без блоков кода, ссылок-разметки и символов Markdown, в одну строку.</summary>
    private static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = CodeBlock.Replace(text!, " ");
        s = Link.Replace(s, "$1");
        s = Regex.Replace(s, @"(^|\n)\s*(#{1,6}|>|[-*+]|\d+[.)])\s+", "$1");
        s = s.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        var plain = Collapse(s);
        // Вопрос из одного блока кода: лучше первые строки кода, чем пустое название
        return plain.Length > 0 ? plain : Collapse(text!.Replace("`", string.Empty));
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Обрезка по границе слова с многоточием.</summary>
    private static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max);
        var head = cut >= max / 2 ? s.Substring(0, cut) : s.Substring(0, max);
        return head.TrimEnd(',', ';', ':', '-', '—', ' ') + "…";
    }

    /// <summary>Кириллических букв больше, чем латинских (код и адреса в вопросе не перевешивают русский текст).</summary>
    internal static bool IsMostlyCyrillic(string text)
    {
        var plain = Plain(text);
        int cyr = 0, lat = 0;
        foreach (var c in plain)
        {
            if (c is >= 'Ѐ' and <= 'ӿ') cyr++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') lat++;
        }
        return cyr > lat;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
