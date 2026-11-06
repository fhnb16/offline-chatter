using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using OfflineChatter.Core.Chat;

namespace OfflineChatter.Core.Export;

public enum ExportFormat
{
    Markdown,
    Html,
    Docx,
    /// <summary>Word 97–2003: RTF-содержимое с расширением .doc (Word и LibreOffice открывают без вопросов).</summary>
    Doc,
    Rtf,
    Json,
}

public sealed record ExportMessage(ChatRole Role, string Content, string? Reasoning, IReadOnlyList<ChatAttachment> Attachments, DateTime Time, string? Model);

/// <summary>Подписи и настройки экспорта (локализуются в UI).</summary>
public sealed class ExportOptions
{
    public string UserLabel { get; set; } = "User";
    public string AssistantLabel { get; set; } = "Assistant";
    public string ReasoningLabel { get; set; } = "Reasoning";
    public bool IncludeReasoning { get; set; } = true;
    public bool IncludeImages { get; set; } = true;
    public bool DarkHtml { get; set; }

    /// <summary>Рендер формулы в PNG (для RTF/.doc): (latex, display) → (png, ширина, высота в px) или null.</summary>
    public Func<string, bool, (byte[] png, int width, int height)?>? RenderMath { get; set; }
}

public sealed class ExportDocument
{
    public string Title { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public List<ExportMessage> Messages { get; } = new();

    /// <summary>Активная ветка чата (или только указанные сообщения).</summary>
    public static ExportDocument FromSession(ChatSession chat, ICollection<string>? onlyIds = null)
    {
        var doc = new ExportDocument { Title = chat.Title, Created = chat.CreatedAt.ToLocalTime() };
        if (!string.IsNullOrWhiteSpace(chat.SystemPrompt) && onlyIds == null)
            doc.Messages.Add(new ExportMessage(ChatRole.System, chat.SystemPrompt!, null, Array.Empty<ChatAttachment>(), chat.CreatedAt, null));
        foreach (var m in chat.ActivePath().Where(m => m.Role is ChatRole.User or ChatRole.Assistant))
        {
            if (onlyIds != null && !onlyIds.Contains(m.Id)) continue;
            doc.Messages.Add(new ExportMessage(m.Role, m.Content, m.Reasoning, m.Attachments, m.CreatedAt.ToLocalTime(), m.Stats?.Model));
        }
        return doc;
    }

    public static string Label(ExportMessage m, ExportOptions o) => m.Role switch
    {
        ChatRole.User => o.UserLabel,
        ChatRole.Assistant => o.AssistantLabel + (string.IsNullOrEmpty(m.Model) ? string.Empty : " · " + m.Model),
        _ => "System",
    };
}

public static class Exporter
{
    /// <summary>Общий конвейер Markdig для всех экспортёров.</summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseMathematics().UsePipeTables().UseGridTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().Build();

    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Markdown => "md",
        ExportFormat.Html => "html",
        ExportFormat.Docx => "docx",
        ExportFormat.Doc => "doc",
        ExportFormat.Rtf => "rtf",
        _ => "json",
    };

    public static void Export(ExportDocument doc, ExportFormat format, string path, ExportOptions options, ChatSession? session = null)
    {
        switch (format)
        {
            case ExportFormat.Markdown:
                System.IO.File.WriteAllText(path, MarkdownExporter.Export(doc, options), new System.Text.UTF8Encoding(false));
                break;
            case ExportFormat.Html:
                System.IO.File.WriteAllText(path, HtmlExporter.Export(doc, options), new System.Text.UTF8Encoding(false));
                break;
            case ExportFormat.Docx:
                DocxExporter.Export(doc, options, path);
                break;
            case ExportFormat.Doc:
            case ExportFormat.Rtf:
                System.IO.File.WriteAllText(path, RtfExporter.Export(doc, options), System.Text.Encoding.ASCII);
                break;
            default:
                System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject((object?)session ?? doc, Storage.JsonFile.Settings), new System.Text.UTF8Encoding(false));
                break;
        }
    }
}

/// <summary>Markdown: исходный текст, формулы остаются в $…$ / $$…$$.</summary>
public static class MarkdownExporter
{
    public static string Export(ExportDocument doc, ExportOptions o)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ").AppendLine(doc.Title).AppendLine();
        sb.Append('_').Append(doc.Created.ToString("g")).AppendLine("_").AppendLine();
        foreach (var m in doc.Messages)
        {
            sb.Append("### ").AppendLine(ExportDocument.Label(m, o)).AppendLine();
            if (o.IncludeReasoning && !string.IsNullOrWhiteSpace(m.Reasoning))
            {
                sb.Append("<details><summary>").Append(o.ReasoningLabel).AppendLine("</summary>").AppendLine();
                sb.AppendLine(m.Reasoning!.Trim()).AppendLine().AppendLine("</details>").AppendLine();
            }
            foreach (var a in m.Attachments)
            {
                if (a.Kind == AttachmentKind.Image && o.IncludeImages) sb.Append("![").Append(a.Name).Append("](").Append(a.DataUri).AppendLine(")").AppendLine();
                else if (a.Kind == AttachmentKind.Text) sb.Append("📎 ").AppendLine(a.Name).AppendLine();
            }
            sb.AppendLine(m.Content.Trim()).AppendLine();
        }
        return sb.ToString();
    }
}
