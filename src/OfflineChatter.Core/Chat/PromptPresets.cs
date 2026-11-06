using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Chat;

/// <summary>Пресет системного промпта.</summary>
public sealed class PromptPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>Поставляется с программой; правится, но при удалении вернётся «Сбросом».</summary>
    public bool BuiltIn { get; set; }
}

/// <summary>Библиотека пресетов системных промптов: config/prompts.json.</summary>
public sealed class PromptPresetStore
{
    private readonly string _file;

    public PromptPresetStore(string file)
    {
        _file = file;
        Load();
    }

    public List<PromptPreset> Items { get; private set; } = new();

    public event EventHandler? Changed;

    /// <summary>Встроенные пресеты. Тексты на английском: модели лучше следуют английским инструкциям, язык ответа — по языку вопроса.</summary>
    public static IReadOnlyList<PromptPreset> Defaults(bool russianNames) => new[]
    {
        new PromptPreset
        {
            Id = "assistant", BuiltIn = true, Name = russianNames ? "Ассистент" : "Assistant",
            Text = "You are a helpful, precise assistant. Answer in the language of the question. Be concise; use Markdown for structure and LaTeX ($...$) for math.",
        },
        new PromptPreset
        {
            Id = "coder", BuiltIn = true, Name = russianNames ? "Программист" : "Programmer",
            Text = "You are a senior software engineer. Give working code first, then a short explanation. Point out edge cases and security issues. Use fenced code blocks with a language tag.",
        },
        new PromptPreset
        {
            Id = "translator", BuiltIn = true, Name = russianNames ? "Переводчик" : "Translator",
            Text = "You are a professional translator. Translate the user's text between Russian and English (detect the direction). Keep formatting, names and code untouched. Output only the translation.",
        },
        new PromptPreset
        {
            Id = "editor", BuiltIn = true, Name = russianNames ? "Редактор текста" : "Text editor",
            Text = "You are an editor. Improve clarity, grammar and flow of the user's text without changing its meaning or language. Return the edited text, then a short list of the main changes.",
        },
        new PromptPreset
        {
            Id = "tutor", BuiltIn = true, Name = russianNames ? "Репетитор" : "Tutor",
            Text = "You are a patient tutor. Explain step by step, check understanding with a short question at the end, and show formulas in LaTeX.",
        },
    };

    public void Load()
    {
        try
        {
            Items = JsonFile.Read<List<PromptPreset>>(_file) ?? new List<PromptPreset>();
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Prompts", "prompts.json is unreadable", ex);
            Items = new List<PromptPreset>();
        }
        Items.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id));
    }

    /// <summary>Первый запуск: заполнить встроенными пресетами.</summary>
    public void SeedIfEmpty(bool russianNames)
    {
        if (File.Exists(_file)) return;
        Items = Defaults(russianNames).ToList();
        Save();
    }

    /// <summary>Вернуть удалённые встроенные пресеты (свои пресеты не трогаются).</summary>
    public int RestoreDefaults(bool russianNames)
    {
        var added = 0;
        foreach (var d in Defaults(russianNames))
        {
            if (Items.Any(p => p.Id == d.Id)) continue;
            Items.Add(d);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public PromptPreset? Find(string? id) => id == null ? null : Items.FirstOrDefault(p => p.Id == id);

    public void Save()
    {
        try
        {
            JsonFile.WriteAtomic(_file, Items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Prompts", "Cannot save prompts.json", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
