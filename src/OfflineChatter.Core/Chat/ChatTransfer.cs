using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Chat;

/// <summary>
/// Перенос чатов: экспорт в один JSON (со всеми ветками, вложениями и статистикой) и импорт.
/// Импорт понимает наш формат, одиночный чат, массив чатов и простой список сообщений {role, content}
/// (формат OpenAI messages / многих других клиентов).
/// </summary>
public static class ChatTransfer
{
    public const string Format = "offline-chatter-chats";

    public static void Export(IEnumerable<ChatSession> chats, string path)
    {
        var root = new JObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["exported"] = DateTime.UtcNow,
            ["chats"] = JArray.FromObject(chats.ToList(), JsonSerializer.Create(JsonFile.Settings)),
        };
        JsonFile.WriteAllTextAtomic(path, root.ToString(Formatting.Indented));
    }

    /// <summary>Читает чаты из файла. Id, совпадающие с уже существующими, заменяются новыми (копия, а не перезапись).</summary>
    public static IReadOnlyList<ChatSession> Import(string path, ISet<string> existingIds)
    {
        var token = JToken.Parse(File.ReadAllText(path));
        var serializer = JsonSerializer.Create(JsonFile.Settings);
        var result = new List<ChatSession>();

        IEnumerable<JToken> items = token switch
        {
            JObject o when o["chats"] is JArray a => a,
            JObject o when o["messages"] is JArray => new[] { o },
            JArray a when a.Count > 0 && a[0] is JObject first && first["role"] != null => new JToken[] { new JObject { ["messages"] = a } },
            JArray a => a,
            _ => throw new InvalidDataException("Unknown chat file format"),
        };

        foreach (var item in items.OfType<JObject>())
        {
            var chat = LooksLikeOurs(item) ? item.ToObject<ChatSession>(serializer) : FromPlainMessages(item, Path.GetFileNameWithoutExtension(path));
            if (chat == null) continue;
            chat.Messages ??= new List<ChatMessage>();
            chat.Tags ??= new List<string>();
            if (string.IsNullOrWhiteSpace(chat.Id) || existingIds.Contains(chat.Id)) chat.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(chat.Title)) chat.Title = Path.GetFileNameWithoutExtension(path);
            existingIds.Add(chat.Id);
            result.Add(chat);
        }
        return result;
    }

    // В нашем формате сообщения связаны через parentId; в «плоском» — нет
    private static bool LooksLikeOurs(JObject o) => o["Messages"] is JArray;

    private static ChatSession FromPlainMessages(JObject o, string fallbackTitle)
    {
        var chat = new ChatSession { Title = (string?)o["title"] ?? (string?)o["name"] ?? fallbackTitle };
        string? parent = null;
        foreach (var m in (o["messages"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var roleText = ((string?)m["role"] ?? "user").ToLowerInvariant();
            var content = m["content"] switch
            {
                JValue v => (string?)v ?? string.Empty,
                // content: [{type:"text", text:"..."}, ...]
                JArray parts => string.Join("\n", parts.OfType<JObject>().Where(p => (string?)p["type"] == "text").Select(p => (string?)p["text"])),
                _ => string.Empty,
            };
            if (roleText == "system")
            {
                chat.SystemPrompt = content;
                continue;
            }
            var role = roleText switch
            {
                "assistant" or "model" or "bot" => ChatRole.Assistant,
                "tool" or "function" => ChatRole.Tool,
                _ => ChatRole.User,
            };
            var msg = new ChatMessage { Role = role, Content = content, ParentId = parent };
            chat.Messages.Add(msg);
            parent = msg.Id;
        }
        chat.ActiveLeafId = parent;
        return chat;
    }
}
