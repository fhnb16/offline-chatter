using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Chat;

/// <summary>Как модель включает и выключает размышления (рассуждения перед ответом).</summary>
public enum ReasoningStyle
{
    /// <summary>Определить по шаблону чата, названию модели и провайдеру.</summary>
    Auto,

    /// <summary>Модель не размышляет (Gemma 3, Llama и т. п.) — ничего не отправляем.</summary>
    None,

    /// <summary>Размышляет всегда, выключить нельзя (DeepSeek-R1, QwQ, Phi-4-reasoning, Magistral).</summary>
    AlwaysOn,

    /// <summary>chat_template_kwargs.enable_thinking (Qwen3, GLM-4.5+, Hunyuan, DeepSeek V3.1).</summary>
    TemplateFlag,

    /// <summary>reasoning_effort в запросе (OpenAI o-серия, GPT-5, Gemini, Grok).</summary>
    ReasoningEffort,

    /// <summary>GPT-OSS: chat_template_kwargs.reasoning_effort (low / medium / high).</summary>
    GptOss,

    /// <summary>IBM Granite 3.2+: chat_template_kwargs.thinking.</summary>
    Granite,

    /// <summary>Переключатели /think и /no_think в системном промпте (SmolLM3, мягкий режим Qwen3).</summary>
    PromptSwitch,
}

/// <summary>Уровень размышлений, который выбрал пользователь.</summary>
public enum ReasoningLevel
{
    /// <summary>Не вмешиваться: модель решает сама.</summary>
    Auto,
    Off,
    Low,
    Medium,
    High,
}

public static class Reasoning
{
    private static readonly Regex AlwaysOnNames = new(@"deepseek[-_ ]?r1|r1[-_]distill|\bqwq\b|phi-?4[-_ ]?(mini[-_ ]?)?reasoning|magistral|openthinker|exaone[-_ ]?deep|skywork[-_ ]?or1|\bmimo\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FlagNames = new(@"qwen3|qwen-?3|glm-?4\.[5-9]|glm-?z1|hunyuan|deepseek[-_ ]?v3\.[1-9]|seed[-_ ]?oss|ernie-?4\.5",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EffortNames = new(@"(^|[/:])o[134](-|$)|gpt-5|gemini|grok-?[34]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoReasoningNames = new(@"gemma|llama|mistral(?!.*magistral)|phi-?[34](?!.*reasoning)|yandexgpt|saiga|command-?r",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Определяет стиль. Шаблон чата (из /props llama-server) надёжнее всего: в нём прямо видно, какую переменную
    /// он читает. Без шаблона — по названию модели; для облачных API — по названию и адресу провайдера.
    /// </summary>
    public static ReasoningStyle Detect(string? modelId, string? chatTemplate, bool isLlamaCpp, string? baseUrl = null)
    {
        var name = modelId ?? string.Empty;
        if (!string.IsNullOrEmpty(chatTemplate))
        {
            var t = chatTemplate!;
            if (t.Contains("reasoning_effort")) return ReasoningStyle.GptOss;
            if (t.Contains("enable_thinking")) return ReasoningStyle.TemplateFlag;
            if (Regex.IsMatch(t, @"\bif\s+thinking\b")) return ReasoningStyle.Granite;
            if (t.Contains("/no_think") || t.Contains("/think")) return ReasoningStyle.PromptSwitch;
            if (t.Contains("<think>")) return ReasoningStyle.AlwaysOn;
        }

        if (name.IndexOf("gpt-oss", StringComparison.OrdinalIgnoreCase) >= 0) return isLlamaCpp ? ReasoningStyle.GptOss : ReasoningStyle.ReasoningEffort;
        if (AlwaysOnNames.IsMatch(name)) return ReasoningStyle.AlwaysOn;
        if (Regex.IsMatch(name, @"granite[-_ ]?3\.[2-9]|granite[-_ ]?4", RegexOptions.IgnoreCase)) return ReasoningStyle.Granite;
        if (Regex.IsMatch(name, "smollm3", RegexOptions.IgnoreCase)) return ReasoningStyle.PromptSwitch;
        if (FlagNames.IsMatch(name)) return isLlamaCpp ? ReasoningStyle.TemplateFlag : ReasoningStyle.None;
        if (!isLlamaCpp && (EffortNames.IsMatch(name) || IsGemini(baseUrl))) return ReasoningStyle.ReasoningEffort;
        if (NoReasoningNames.IsMatch(name)) return ReasoningStyle.None;
        // Неизвестная локальная модель: флаг шаблона безопасен — шаблон, который его не читает, просто игнорирует его
        return isLlamaCpp ? ReasoningStyle.TemplateFlag : ReasoningStyle.None;
    }

    private static bool IsGemini(string? baseUrl) =>
        baseUrl != null && baseUrl.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Effort(ReasoningLevel level) => level switch
    {
        ReasoningLevel.Low => "low",
        ReasoningLevel.High => "high",
        _ => "medium",
    };

    /// <summary>Дописывает в тело запроса параметры размышлений под стиль модели. Возвращает фактический стиль.</summary>
    public static ReasoningStyle Apply(JObject body, ReasoningStyle style, ReasoningLevel level, string? modelId, string? chatTemplate,
        bool isLlamaCpp, string? baseUrl)
    {
        if (style == ReasoningStyle.Auto) style = Detect(modelId, chatTemplate, isLlamaCpp, baseUrl);
        if (level == ReasoningLevel.Auto) return style;
        var on = level != ReasoningLevel.Off;

        JObject Kwargs()
        {
            if (body["chat_template_kwargs"] is not JObject k)
            {
                k = new JObject();
                body["chat_template_kwargs"] = k;
            }
            return k;
        }

        switch (style)
        {
            case ReasoningStyle.TemplateFlag when isLlamaCpp:
                Kwargs()["enable_thinking"] = on;
                break;
            case ReasoningStyle.Granite when isLlamaCpp:
                Kwargs()["thinking"] = on;
                break;
            case ReasoningStyle.GptOss when isLlamaCpp:
                // У GPT-OSS размышления не выключаются — «выключено» значит самый короткий режим
                Kwargs()["reasoning_effort"] = on ? Effort(level) : "low";
                break;
            case ReasoningStyle.GptOss:
            case ReasoningStyle.ReasoningEffort:
                if (on) body["reasoning_effort"] = Effort(level);
                else if (IsGemini(baseUrl)) body["reasoning_effort"] = "none"; // Gemini 2.5 Flash умеет выключать полностью
                else body["reasoning_effort"] = "low";                         // OpenAI: минимально допустимый уровень
                break;
            case ReasoningStyle.PromptSwitch:
                AppendSwitch(body, on ? "/think" : "/no_think");
                break;
        }
        return style;
    }

    /// <summary>Добавляет переключатель в системное сообщение (или создаёт его).</summary>
    private static void AppendSwitch(JObject body, string tag)
    {
        if (body["messages"] is not JArray messages) return;
        var system = messages.OfType<JObject>().FirstOrDefault(m => (string?)m["role"] == "system");
        if (system == null)
        {
            messages.Insert(0, new JObject { ["role"] = "system", ["content"] = tag });
            return;
        }
        if (system["content"] is JValue v && v.Type == JTokenType.String)
        {
            var text = (string?)v ?? string.Empty;
            if (!text.Contains(tag)) system["content"] = text.Length == 0 ? tag : text + "\n" + tag;
        }
    }
}
