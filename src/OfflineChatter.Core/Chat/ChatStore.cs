using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Chat;

/// <summary>Хранилище чатов: по одному JSON-файлу на чат (chats/{id}.json), атомарная запись.</summary>
public sealed class ChatStore
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<string, Write> _writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<ChatSession, object> _deleted = new();

    private sealed class Write
    {
        public Write(ChatSession session, ChatSession? snapshot)
        {
            Session = session;
            Snapshot = snapshot;
        }

        public ChatSession Session { get; }
        public ChatSession? Snapshot { get; }
        public Task<bool> Completion { get; set; } = null!;
    }

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
                foreach (var m in chat.Messages) m.Stats?.FixLegacyPromptCount();
                list.Add(chat);
            }
            catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Chats", "Cannot read chat " + f, ex);
            }
        }
        return list.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt).ToList();
    }

    public void Save(ChatSession chat) => SaveAsync(chat).GetAwaiter().GetResult();

    public void Delete(ChatSession chat) => DeleteAsync(chat).GetAwaiter().GetResult();

    public Task<bool> SaveAsync(ChatSession chat)
    {
        lock (_gate)
        {
            // Позднее завершение генерации не должно вернуть удалённый экземпляр чата.
            if (_deleted.TryGetValue(chat, out _)) return Task.FromResult(true);
            return QueueWrite(chat, chat.Snapshot());
        }
    }

    public Task<bool> DeleteAsync(ChatSession chat)
    {
        lock (_gate)
        {
            _deleted.GetValue(chat, _ => new object());
            return QueueWrite(chat, null);
        }
    }

    public async Task<bool> FlushAsync()
    {
        lock (_gate)
        {
            foreach (var failed in _writes.Values.Where(w => w.Completion.Status == TaskStatus.RanToCompletion).ToArray())
                QueueWrite(failed.Session, failed.Snapshot);
        }

        while (true)
        {
            Task<bool>[] pending;
            lock (_gate)
            {
                if (_writes.Count == 0) return true;
                pending = _writes.Values.Select(w => w.Completion).Where(task => !task.IsCompleted).ToArray();
                if (pending.Length == 0) return false;
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private Task<bool> QueueWrite(ChatSession chat, ChatSession? snapshot)
    {
        var id = chat.Id;
        _writes.TryGetValue(id, out var previous);
        var write = new Write(chat, snapshot);
        _writes[id] = write;
        return write.Completion = Task.Run(async () =>
        {
            if (previous != null) await previous.Completion.ConfigureAwait(false);
            lock (_gate)
            {
                if (snapshot != null && _deleted.TryGetValue(chat, out _)) return false;
            }
            try
            {
                if (snapshot == null) File.Delete(PathOf(id));
                else JsonFile.WriteAtomic(PathOf(id), snapshot);
                lock (_gate)
                {
                    if (_writes[id] == write) _writes.Remove(id);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                Log.Error("Chats", "Cannot persist chat " + id, ex);
                return false;
            }
        });
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
