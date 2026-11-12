using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Chat;

/// <summary>Ответ модели после разбора: что показывать как текст, что — как рассуждения, какие инструменты вызвать.</summary>
public sealed class RecoveredAnswer
{
    public string Content { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; } = new();

    /// <summary>Сервер отдал весь ответ как рассуждения — он показан как текст ответа.</summary>
    public bool Promoted { get; set; }

    /// <summary>Вызовы инструментов найдены в тексте (сервер их не разобрал).</summary>
    public bool ToolCallsFromText { get; set; }
}

/// <summary>
/// Приводит ответ модели в порядок, когда сервер разложил его не туда. Типичный случай — Qwen 3.5 и другие
/// модели, чей шаблон сам открывает &lt;think&gt; в промпте: после результата инструмента небольшая модель
/// отвечает сразу, не закрывая тег, и llama-server отдаёт весь ответ как reasoning_content с пустым content.
/// Здесь же — закрывающий тег без открывающего и вызовы инструментов, оставшиеся текстом.
/// </summary>
public static class AnswerRecovery
{
    private static readonly (string open, string close)[] Tags =
    {
        ("<think>", "</think>"),
        ("<thinking>", "</thinking>"),
    };

    private static readonly Regex ToolCallBlock = new(@"<tool_call>\s*(\{.*?\})\s*(?:</tool_call>|$)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Отделить рассуждения от текста: обёртка в начале текста, закрывающий тег без открывающего
    /// (открывающий остался в промпте) и закрывающий тег внутри рассуждений при пустом тексте.
    /// Подходит и для незаконченного потока.
    /// </summary>
    public static (string reasoning, string content) Separate(string reasoning, string content)
    {
        if (reasoning.Length == 0 && ThinkSplitter.StartsWithWrapper(content))
        {
            var split = ThinkSplitter.Split(content);
            return (split.reasoning, split.content);
        }

        foreach (var (open, close) in Tags)
        {
            var end = content.IndexOf(close, StringComparison.Ordinal);
            if (end >= 0 && content.IndexOf(open, 0, end, StringComparison.Ordinal) < 0)
            {
                var before = content.Substring(0, end).Trim();
                var joined = reasoning.Length == 0 ? before : before.Length == 0 ? reasoning : reasoning + "\n" + before;
                return (joined, content.Substring(end + close.Length).TrimStart());
            }

            if (content.Trim().Length == 0)
            {
                var inside = reasoning.IndexOf(close, StringComparison.Ordinal);
                if (inside >= 0)
                    return (reasoning.Substring(0, inside).TrimEnd(), reasoning.Substring(inside + close.Length).TrimStart());
            }
        }
        return (reasoning, content);
    }

    /// <summary>Итог шага генерации.</summary>
    /// <param name="finishReason">Причина остановки: рассуждения становятся ответом только при обычном завершении.</param>
    /// <param name="toolNames">Имена инструментов, предложенных модели (null — инструменты не предлагались).</param>
    public static RecoveredAnswer Finish(string? reasoning, string? content, IEnumerable<ToolCall>? toolCalls,
        string? finishReason, ICollection<string>? toolNames)
    {
        var (r, c) = Separate(reasoning ?? string.Empty, content ?? string.Empty);
        var answer = new RecoveredAnswer { Reasoning = r, Content = c };
        if (toolCalls != null) answer.ToolCalls.AddRange(toolCalls);

        if (answer.ToolCalls.Count == 0 && toolNames is { Count: > 0 })
        {
            answer.Content = ExtractToolCalls(answer.Content, toolNames, answer.ToolCalls);
            if (answer.ToolCalls.Count == 0) answer.Reasoning = ExtractToolCalls(answer.Reasoning, toolNames, answer.ToolCalls);
            answer.ToolCallsFromText = answer.ToolCalls.Count > 0;
        }

        if (answer.Content.Trim().Length == 0 && answer.ToolCalls.Count == 0 && answer.Reasoning.Trim().Length > 0 && IsNaturalStop(finishReason))
        {
            answer.Content = answer.Reasoning.Trim();
            answer.Reasoning = string.Empty;
            answer.Promoted = true;
        }
        return answer;
    }

    /// <summary>Обрыв по лимиту токенов или остановка пользователем — это не ответ, а недописанные рассуждения.</summary>
    private static bool IsNaturalStop(string? finishReason) =>
        string.IsNullOrEmpty(finishReason) || finishReason == "stop" || finishReason == "eos" || finishReason == "end_turn";

    /// <summary>Вызовы в формате Hermes / Qwen: &lt;tool_call&gt;{"name": …, "arguments": {…}}&lt;/tool_call&gt;.</summary>
    private static string ExtractToolCalls(string text, ICollection<string> toolNames, List<ToolCall> into)
    {
        if (text.IndexOf("<tool_call>", StringComparison.Ordinal) < 0) return text;
        return ToolCallBlock.Replace(text, m =>
        {
            JObject o;
            try { o = JObject.Parse(m.Groups[1].Value); }
            catch (JsonException) { return m.Value; }
            var name = (string?)(o["name"] as JValue);
            if (name == null || !toolNames.Contains(name)) return m.Value;
            var args = o["arguments"] ?? o["parameters"];
            into.Add(new ToolCall
            {
                Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = name,
                Arguments = args switch
                {
                    null => "{}",
                    JValue { Type: JTokenType.String } s => (string?)s ?? "{}",
                    _ => args.ToString(Formatting.None),
                },
            });
            return string.Empty;
        }).Trim();
    }
}
