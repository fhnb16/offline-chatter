using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineChatter.Core.Chat;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum AttachmentKind
{
    Image,
    Text,
}

/// <summary>Вложение сообщения. Изображения хранятся как data URI (после уменьшения), текст — как есть.</summary>
public sealed class ChatAttachment
{
    public AttachmentKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/png";

    /// <summary>Для изображений — base64 без префикса; для текста — содержимое.</summary>
    public string Data { get; set; } = string.Empty;

    public string DataUri => "data:" + MimeType + ";base64," + Data;
}

/// <summary>Статистика генерации одного ответа.</summary>
public sealed class GenerationStats
{
    /// <summary>Весь промпт запроса в токенах — вместе с тем, что сервер взял из кэша.</summary>
    public int PromptTokens { get; set; }

    /// <summary>Сгенерировано токенов: ответ вместе с рассуждениями.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Сколько токенов промпта сервер обработал заново (llama.cpp: prompt_n, без кэша). К нему относится скорость промпта.</summary>
    public int? PromptEvalTokens { get; set; }

    /// <summary>Из сгенерированных — токены рассуждений, если провайдер их сообщает.</summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>Провайдер не прислал usage: числа токенов оценены по тексту (в интерфейсе — со знаком «≈»).</summary>
    public bool Estimated { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public int TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// Чаты, сохранённые до исправления подсчёта: в PromptTokens лежала только заново обработанная часть промпта
    /// (prompt_n llama-server), поэтому «из кэша» получалось больше «входа». Восстанавливаем полный размер.
    /// </summary>
    public void FixLegacyPromptCount()
    {
        if (PromptEvalTokens != null || CachedTokens is not { } cached || cached <= PromptTokens) return;
        PromptEvalTokens = PromptTokens;
        PromptTokens += cached;
    }
    public double? PromptTokensPerSecond { get; set; }
    public double? TokensPerSecond { get; set; }
    public double TimeToFirstTokenMs { get; set; }
    public double TotalMs { get; set; }
    public double? ReasoningMs { get; set; }
    public int? CachedTokens { get; set; }
    public int? DraftAccepted { get; set; }
    public int? DraftTotal { get; set; }
    public string? FinishReason { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
}

/// <summary>Вызов инструмента (MCP / встроенные), этап 9.</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = "{}";
    public string? Result { get; set; }
    public bool IsError { get; set; }
}

/// <summary>Узел дерева сообщений. Ветвление: у одного родителя несколько детей (варианты).</summary>
public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public List<ChatAttachment> Attachments { get; set; } = new();
    public List<ToolCall> ToolCalls { get; set; } = new();
    public string? ToolCallId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public GenerationStats? Stats { get; set; }
    public string? Error { get; set; }
}

/// <summary>Чат: дерево сообщений + активная ветка + настройки беседы.</summary>
public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Название пока временное (первые слова вопроса) и ждёт названия от модели. Название от модели заменяет только его:
    /// после первой попытки (удачной или нет) флаг снимается.
    /// </summary>
    public bool TitlePending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public string? Folder { get; set; }
    public List<string> Tags { get; set; } = new();

    /// <summary>"local:{modelId}" или "provider:{providerId}:{model}".</summary>
    public string? ModelRef { get; set; }
    public string? SystemPrompt { get; set; }
    /// <summary>Устарело: заменено <see cref="Reasoning"/> (читается для старых чатов).</summary>
    public bool? Thinking { get; set; }

    /// <summary>Уровень размышлений; null — взять из <see cref="Thinking"/> или «Авто».</summary>
    public ReasoningLevel? Reasoning { get; set; }

    /// <summary>Как модель включает размышления; null — определить автоматически.</summary>
    public ReasoningStyle? ReasoningStyle { get; set; }

    public ReasoningLevel EffectiveReasoning => Reasoning ?? Thinking switch
    {
        true => ReasoningLevel.Medium,
        false => ReasoningLevel.Off,
        _ => ReasoningLevel.Auto,
    };

    /// <summary>Разрешить модели вызывать инструменты (встроенные и MCP). null — по настройке по умолчанию.</summary>
    public bool? Tools { get; set; }

    /// <summary>
    /// Набор инструментов чата; null — «Все» (или «Без инструментов», если <see cref="Tools"/> = false):
    /// старые чаты ведут себя как раньше.
    /// </summary>
    public Tools.ToolPreset? ToolPreset { get; set; }

    /// <summary>Свой список инструментов (id) для набора <see cref="OfflineChatter.Core.Tools.ToolPreset.Custom"/>.</summary>
    public List<string>? ToolIds { get; set; }

    /// <summary>Набор с учётом старого флага <see cref="Tools"/> и настройки по умолчанию.</summary>
    public Tools.ToolPreset EffectiveToolPreset(bool enabledByDefault) =>
        ToolPreset ?? ((Tools ?? enabledByDefault) ? OfflineChatter.Core.Tools.ToolPreset.All : OfflineChatter.Core.Tools.ToolPreset.None);
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>Последнее сообщение активной ветки.</summary>
    public string? ActiveLeafId { get; set; }

    public ChatMessage? Find(string? id) => id == null ? null : Messages.FirstOrDefault(m => m.Id == id);

    /// <summary>Сообщения активной ветки от корня к листу.</summary>
    public List<ChatMessage> ActivePath()
    {
        var path = new List<ChatMessage>();
        var cur = Find(ActiveLeafId) ?? Messages.LastOrDefault();
        var guard = 0;
        while (cur != null && guard++ < 100_000)
        {
            path.Add(cur);
            cur = Find(cur.ParentId);
        }
        path.Reverse();
        return path;
    }

    public IReadOnlyList<ChatMessage> Siblings(ChatMessage m) =>
        Messages.Where(x => x.ParentId == m.ParentId && x.Role == m.Role).OrderBy(x => x.CreatedAt).ToList();

    public IEnumerable<ChatMessage> Children(string id) => Messages.Where(m => m.ParentId == id);

    /// <summary>Самый свежий лист под узлом (чтобы при переключении варианта показать его продолжение).</summary>
    public ChatMessage DeepestLeaf(ChatMessage from)
    {
        var cur = from;
        while (true)
        {
            var next = Children(cur.Id).OrderByDescending(c => c.CreatedAt).FirstOrDefault();
            if (next == null) return cur;
            cur = next;
        }
    }

    public ChatMessage Append(ChatMessage message)
    {
        message.ParentId ??= ActivePath().LastOrDefault()?.Id;
        Messages.Add(message);
        ActiveLeafId = message.Id;
        UpdatedAt = DateTime.UtcNow;
        return message;
    }

    /// <summary>Удалить сообщение вместе с поддеревом.</summary>
    public void Remove(ChatMessage m)
    {
        var doomed = new HashSet<string> { m.Id };
        bool changed;
        do
        {
            changed = false;
            foreach (var x in Messages)
                if (x.ParentId != null && doomed.Contains(x.ParentId) && doomed.Add(x.Id)) changed = true;
        } while (changed);
        Messages.RemoveAll(x => doomed.Contains(x.Id));
        if (ActiveLeafId != null && doomed.Contains(ActiveLeafId))
        {
            var parent = Find(m.ParentId);
            ActiveLeafId = parent != null ? DeepestLeaf(parent).Id : Messages.LastOrDefault()?.Id;
        }
    }
}
