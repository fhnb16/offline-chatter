using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Chat;

/// <summary>
/// Особенности OpenAI-совместимых API (quirks): какие поля принимает провайдер и где отдаёт рассуждения.
/// </summary>
public sealed class ProviderQuirks
{
    /// <summary>"max_tokens" или "max_completion_tokens" (OpenAI o-серия / GPT-5).</summary>
    public string MaxTokensField { get; set; } = "max_tokens";
    public bool SupportsTemperature { get; set; } = true;
    public bool SupportsTopK { get; set; }
    public bool SupportsMinP { get; set; }
    public bool SupportsVision { get; set; } = true;
    public bool SupportsTools { get; set; } = true;
    /// <summary>stream_options.include_usage — usage в последнем чанке стрима.</summary>
    public bool IncludeUsage { get; set; } = true;
    /// <summary>Поле дельты с рассуждениями: reasoning_content (llama.cpp, DeepSeek), reasoning (OpenRouter).</summary>
    public string[] ReasoningFields { get; set; } = { "reasoning_content", "reasoning" };
    /// <summary>llama.cpp: chat_template_kwargs, timings_per_token, cache_prompt.</summary>
    public bool IsLlamaCpp { get; set; }
}

public sealed record ProviderPreset(string Id, string Name, string BaseUrl, bool NeedsKey, ProviderQuirks Quirks, string? KeyUrl = null);

public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } = new[]
    {
        new ProviderPreset("openai", "OpenAI", "https://api.openai.com/v1", true,
            new ProviderQuirks { MaxTokensField = "max_completion_tokens" }, "https://platform.openai.com/api-keys"),
        new ProviderPreset("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", true,
            new ProviderQuirks(), "https://aistudio.google.com/apikey"),
        new ProviderPreset("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", true,
            new ProviderQuirks { SupportsTopK = true, SupportsMinP = true }, "https://openrouter.ai/keys"),
        new ProviderPreset("anthropic", "Anthropic (OpenAI-compatible)", "https://api.anthropic.com/v1", true,
            new ProviderQuirks { IncludeUsage = false }, "https://console.anthropic.com/settings/keys"),
        new ProviderPreset("deepseek", "DeepSeek", "https://api.deepseek.com/v1", true,
            new ProviderQuirks(), "https://platform.deepseek.com/api_keys"),
        new ProviderPreset("groq", "Groq", "https://api.groq.com/openai/v1", true,
            new ProviderQuirks(), "https://console.groq.com/keys"),
        new ProviderPreset("mistral", "Mistral", "https://api.mistral.ai/v1", true,
            new ProviderQuirks(), "https://console.mistral.ai/api-keys"),
        new ProviderPreset("lmstudio", "LM Studio (local)", "http://127.0.0.1:1234/v1", false,
            new ProviderQuirks { SupportsTopK = true, SupportsMinP = true }),
        new ProviderPreset("ollama", "Ollama (local)", "http://127.0.0.1:11434/v1", false,
            new ProviderQuirks { SupportsTopK = true }),
        new ProviderPreset("vllm", "vLLM", "http://127.0.0.1:8000/v1", false,
            new ProviderQuirks { SupportsTopK = true, SupportsMinP = true }),
        new ProviderPreset("llamacpp", "llama.cpp server (remote)", "http://127.0.0.1:8080/v1", false,
            new ProviderQuirks { SupportsTopK = true, SupportsMinP = true, IsLlamaCpp = true }),
        new ProviderPreset("custom", "Custom (OpenAI-compatible)", "https://", false, new ProviderQuirks()),
    };

    public static ProviderPreset Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All.Last();

    /// <summary>Встроенный локальный llama-server (управляется приложением).</summary>
    public static ProviderQuirks LocalLlama { get; } = new() { SupportsTopK = true, SupportsMinP = true, IsLlamaCpp = true, IncludeUsage = true };
}

/// <summary>Подключённый провайдер (config/providers.json).</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string PresetId { get; set; } = "custom";
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKeyProtected { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Models { get; set; } = new();
    /// <summary>Модели, отмеченные пользователем как избранные (показываются в выборе модели чата).</summary>
    public List<string> PinnedModels { get; set; } = new();
    /// <summary>Переопределения особенностей (null — из пресета).</summary>
    public ProviderQuirks? Quirks { get; set; }

    public ProviderQuirks EffectiveQuirks => Quirks ?? ProviderPresets.Get(PresetId).Quirks;
}

public sealed class ProviderStore
{
    private readonly string _file;

    public ProviderStore(string file)
    {
        _file = file;
        try
        {
            Items = JsonFile.Read<List<ProviderConfig>>(file) ?? new List<ProviderConfig>();
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException)
        {
            Log.Error("Providers", "providers.json unreadable", ex);
            Items = new List<ProviderConfig>();
        }
    }

    public List<ProviderConfig> Items { get; }

    public event EventHandler? Changed;

    public ProviderConfig? Find(string? id) => Items.FirstOrDefault(p => p.Id == id);

    public void Save()
    {
        JsonFile.WriteAtomic(_file, Items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
