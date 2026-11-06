using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Chat;

/// <summary>Хранилище чатов: по одному JSON-файлу на чат (chats/{id}.json), атомарная запись.</summary>
public sealed class ChatStore
{
    private readonly string _dir;

    public ChatStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    public IReadOnlyList<ChatSession> LoadAll()
    {
        var list = new List<ChatSession>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var chat = JsonFile.Read<ChatSession>(f);
                if (chat == null) continue;
                chat.Id = Path.GetFileNameWithoutExtension(f);
                chat.Messages ??= new List<ChatMessage>();
                list.Add(chat);
            }
            catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Chats", "Cannot read chat " + f, ex);
            }
        }
        return list.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt).ToList();
    }

    public void Save(ChatSession chat)
    {
        try
        {
            JsonFile.WriteAtomic(PathOf(chat.Id), chat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Chats", "Cannot save chat " + chat.Id, ex);
        }
    }

    public void Delete(ChatSession chat)
    {
        try { File.Delete(PathOf(chat.Id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Chats", "Cannot delete chat", ex); }
    }

    /// <summary>Совпадает ли чат с поисковой строкой (заголовок и текст всех сообщений).</summary>
    public static bool Matches(ChatSession chat, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return chat.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
               || chat.Messages.Any(m => m.Content.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
    }
}
