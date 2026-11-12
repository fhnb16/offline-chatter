using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Tools;

/// <summary>
/// Город по умолчанию для инструментов погоды. Инструмент может быть любым (встроенным или с MCP-сервера),
/// поэтому он опознаётся по названию и описанию, а параметр места — по имени в схеме.
/// </summary>
public static class DefaultLocation
{
    private static readonly Regex WeatherName = new("weather|forecast|meteo|pogod|погод|прогноз", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WeatherText = new(@"\bweather\b|\bforecast\b|погод", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Имена параметра места — в порядке предпочтения.</summary>
    private static readonly string[] LocationNames =
    {
        "city", "location", "place", "town", "city_name", "cityname", "location_name", "locality", "address", "region", "q", "query", "name",
    };

    /// <summary>Город из настройки: одна строка без лишних пробелов; пусто — null.</summary>
    public static string? Normalize(string? city)
    {
        var c = PageExtractor.Collapse(city);
        if (c.Length == 0) return null;
        return c.Length > 80 ? c.Substring(0, 80) : c;
    }

    public static bool IsWeatherTool(string name, string? description) =>
        WeatherName.IsMatch(name) || (description != null && WeatherText.IsMatch(description));

    /// <summary>Описание инструмента с указанием города: так модель знает, что место можно не спрашивать.</summary>
    public static string Describe(string description, string city) =>
        description.TrimEnd() + $" Default location: {city} - use it when the user does not name another place.";

    /// <summary>Параметр схемы, в который передаётся место (строковый), или null.</summary>
    public static string? FindLocationProperty(JObject? schema)
    {
        if (schema?["properties"] is not JObject props) return null;
        bool IsString(JProperty p) => p.Value is not JObject o || o["type"] == null || (o["type"]!.Type == JTokenType.String ? (string?)o["type"] == "string"
            : o["type"] is JArray types && types.Any(t => (string?)t == "string"));

        var strings = props.Properties().Where(IsString).ToList();
        foreach (var known in LocationNames)
            if (strings.FirstOrDefault(p => p.Name.Equals(known, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact.Name;
        return strings.FirstOrDefault(p => p.Name.IndexOf("city", StringComparison.OrdinalIgnoreCase) >= 0
                                           || p.Name.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0
                                           || p.Name.IndexOf("place", StringComparison.OrdinalIgnoreCase) >= 0)?.Name;
    }

    /// <summary>Подставить город, если модель не передала место. Возвращает true, если аргументы изменены.</summary>
    public static bool Fill(JObject? schema, JObject args, string city)
    {
        var name = FindLocationProperty(schema);
        if (name == null) return false;
        var current = args.Properties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (current != null && current.Value.Type != JTokenType.Null && current.Value.ToString().Trim().Length > 0) return false;
        // Координаты вместо названия — место уже задано
        if (args.Properties().Any(p => p.Name.StartsWith("lat", StringComparison.OrdinalIgnoreCase) && p.Value.Type is JTokenType.Float or JTokenType.Integer))
            return false;
        current?.Remove();
        args[name] = city;
        return true;
    }
}
