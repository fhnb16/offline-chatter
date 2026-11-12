using System;
using System.Collections.Generic;

namespace OfflineChatter.Core.Chat;

/// <summary>
/// Грубая оценка числа токенов по тексту — только для провайдеров, которые не присылают usage
/// (или когда ответ оборван до последнего чанка). Точного токенизатора модели у клиента нет, поэтому
/// результат всегда помечается как оценка (<see cref="GenerationStats.Estimated"/>).
/// </summary>
public static class TokenEstimator
{
    /// <summary>Служебные токены на одно сообщение (роль и разделители шаблона чата).</summary>
    private const int PerMessageOverhead = 4;

    /// <summary>
    /// Латиница — около 4 символов на токен, кириллица и прочие алфавиты — около 2,5,
    /// иероглифы — токен на символ, знаки препинания чаще всего отдельные токены.
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double tokens = 0;
        foreach (var ch in text!)
        {
            if (ch <= 0x7F)
            {
                if (char.IsLetterOrDigit(ch)) tokens += 0.25;
                else if (ch == ' ') tokens += 0.05;        // пробел обычно входит в соседний токен
                else if (char.IsWhiteSpace(ch)) tokens += 0.5;
                else tokens += 0.6;
            }
            else if (IsCjk(ch)) tokens += 1.0;
            else if (char.IsLetter(ch)) tokens += 0.4;
            else if (char.IsSurrogate(ch)) tokens += 1.0;   // эмодзи: пара суррогатов ≈ 2 токена
            else tokens += 0.6;
        }
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    public static int EstimatePrompt(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += PerMessageOverhead + Estimate(m.Content);
            foreach (var a in m.Attachments)
                if (a.Kind == AttachmentKind.Text) total += Estimate(a.Data);
            foreach (var call in m.ToolCalls) total += Estimate(call.Name) + Estimate(call.Arguments);
        }
        return total;
    }

    private static bool IsCjk(char ch) =>
        ch >= 0x3040 && ch <= 0x30FF      // хирагана и катакана
        || ch >= 0x3400 && ch <= 0x9FFF   // иероглифы
        || ch >= 0xAC00 && ch <= 0xD7AF   // хангыль
        || ch >= 0xF900 && ch <= 0xFAFF;
}
