using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;

namespace OfflineChatter.Core.Tools;

/// <summary>Готовый набор инструментов чата.</summary>
public enum ToolPreset
{
    /// <summary>Все включённые инструменты (как было до наборов).</summary>
    All,
    None,
    /// <summary>Калькулятор, единицы, дата и время.</summary>
    Math,
    /// <summary>Поиск, страницы, погода, курсы, дата и время.</summary>
    Web,
    /// <summary>Свой список (галочки в правой панели).</summary>
    Custom,
}

/// <summary>
/// Чистка схем инструментов перед отправкой. Только служебные поля JSON Schema, которые модели ничего не дают:
/// описания, примеры и значения по умолчанию не трогаются — маленькие модели (1–9B) по ним заполняют аргументы.
/// </summary>
public static class ToolSchemaCompactor
{
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "$schema", "title", "additionalProperties" };

    /// <summary>Копия схемы без служебных полей. Исходная схема (у MCP — из tools/list) не меняется.</summary>
    public static JObject CompactSchema(JObject schema)
    {
        var copy = (JObject)schema.DeepClone();
        Clean(copy, isPropertyMap: false);
        return copy;
    }

    private static void Clean(JToken token, bool isPropertyMap)
    {
        if (token is JArray array)
        {
            foreach (var item in array) Clean(item, false);
            return;
        }
        if (token is not JObject obj) return;
        foreach (var p in obj.Properties().ToList())
        {
            // В «properties» ключи — имена параметров: параметр с именем «title» служебным полем не является
            if (!isPropertyMap && Dropped.Contains(p.Name))
            {
                p.Remove();
                continue;
            }
            Clean(p.Value, isPropertyMap: !isPropertyMap && p.Name is "properties" or "patternProperties" or "$defs" or "definitions");
        }
    }
}

/// <summary>
/// Готовые наборы инструментов чата. Состав «Вычислений» и «Веба» определяется по имени и описанию инструмента,
/// поэтому в набор попадают и подходящие инструменты MCP-серверов.
/// </summary>
public static class ToolPresets
{
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MathTool = R(@"calculat|\bcalc|\bmath|arithmet|convert|\bunits?\b|_units");
    private static readonly Regex TimeTool = R(@"datetime|\bdate\b|_date|\btime\b|_time|clock|calendar");
    private static readonly Regex WebTool = R(@"search|fetch|\bweb\b|web_|browse|\burl\b|webpage|currenc|exchange|forex|crypto");

    private static bool Is(Regex re, ToolDefinition t) => re.IsMatch(t.Name) || re.IsMatch(t.Description);

    // Дата и время — только по имени: «time» в описаниях встречается слишком часто (единицы времени, таймауты)
    private static bool IsTime(ToolDefinition t) => TimeTool.IsMatch(t.Name);

    public static bool Contains(ToolPreset preset, ToolDefinition tool) => preset switch
    {
        ToolPreset.All => true,
        ToolPreset.Math => Is(MathTool, tool) || IsTime(tool),
        ToolPreset.Web => Is(WebTool, tool) || IsTime(tool) || DefaultLocation.IsWeatherTool(tool.Name, tool.Description),
        _ => false,
    };

    /// <summary>
    /// Включён ли инструмент в чате: <paramref name="preset"/> — набор чата, <paramref name="custom"/> — свой список id
    /// (для <see cref="ToolPreset.Custom"/>).
    /// </summary>
    public static bool IsEnabled(ToolDefinition tool, ToolPreset preset, ICollection<string>? custom) =>
        preset == ToolPreset.Custom ? custom?.Contains(tool.Id) == true : Contains(preset, tool);
}

/// <summary>
/// Старые результаты инструментов: до последнего вопроса пользователя модель их уже прочитала и учла в ответе,
/// но без сокращения они уходили бы с каждым следующим вопросом целиком (страница — тысячи токенов).
/// Модели отправляется начало и пометка; в чате и в файле результат остаётся полным. Включается в настройках.
/// </summary>
public static class ToolHistory
{
    /// <summary>Сокращать имеет смысл только результаты заметно длиннее пометки.</summary>
    private const int MinSaving = 200;

    public static List<ChatMessage> CompactOldResults(IReadOnlyList<ChatMessage> messages, int keepChars)
    {
        var lastUser = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.User) continue;
            lastUser = i;
            break;
        }

        var result = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            result.Add(i < lastUser && m.Role == ChatRole.Tool && m.Content.Length > keepChars + MinSaving ? Shortened(m, keepChars) : m);
        }
        return result;
    }

    private static ChatMessage Shortened(ChatMessage m, int keep)
    {
        var text = m.Content;
        var end = keep;
        // По границе строки, если она недалеко
        var nl = text.LastIndexOf('\n', keep, Math.Min(keep, 200));
        if (nl > keep / 2) end = nl;
        var omitted = text.Length - end;
        return new ChatMessage
        {
            Id = m.Id,
            ParentId = m.ParentId,
            Role = m.Role,
            ToolCallId = m.ToolCallId,
            CreatedAt = m.CreatedAt,
            Content = text.Substring(0, end).TrimEnd()
                      + $"\n[Earlier tool result shortened: {omitted} of {text.Length} characters omitted. Call the tool again if you need the full text.]",
        };
    }
}
