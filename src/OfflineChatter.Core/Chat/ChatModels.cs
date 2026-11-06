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
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public string? Folder { get; set; }
    public List<string> Tags { get; set; } = new();

    /// <summary>"local:{modelId}" или "provider:{providerId}:{model}".</summary>
    public string? ModelRef { get; set; }
    public string? SystemPrompt { get; set; }
    public bool? Thinking { get; set; }

    /// <summary>Разрешить модели вызывать инструменты (встроенные и MCP). null — по настройке по умолчанию.</summary>
    public bool? Tools { get; set; }
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
