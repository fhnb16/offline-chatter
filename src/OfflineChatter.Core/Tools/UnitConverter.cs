using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OfflineChatter.Core.Tools;

/// <summary>Перевод единиц: длина, масса, объём, площадь, скорость, время, данные, давление, энергия, температура.</summary>
public static class UnitConverter
{
    private static readonly Dictionary<string, (string dim, double factor)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        // длина → метры
        ["m"] = ("length", 1), ["km"] = ("length", 1000), ["cm"] = ("length", 0.01), ["mm"] = ("length", 0.001), ["um"] = ("length", 1e-6), ["nm"] = ("length", 1e-9),
        ["in"] = ("length", 0.0254), ["inch"] = ("length", 0.0254), ["ft"] = ("length", 0.3048), ["yd"] = ("length", 0.9144), ["mi"] = ("length", 1609.344), ["nmi"] = ("length", 1852),
        ["м"] = ("length", 1), ["км"] = ("length", 1000), ["см"] = ("length", 0.01), ["мм"] = ("length", 0.001),
        // масса → кг
        ["kg"] = ("mass", 1), ["g"] = ("mass", 0.001), ["mg"] = ("mass", 1e-6), ["t"] = ("mass", 1000), ["lb"] = ("mass", 0.45359237), ["oz"] = ("mass", 0.028349523125),
        ["кг"] = ("mass", 1), ["г"] = ("mass", 0.001), ["т"] = ("mass", 1000),
        // объём → литры
        ["l"] = ("volume", 1), ["ml"] = ("volume", 0.001), ["m3"] = ("volume", 1000), ["gal"] = ("volume", 3.785411784), ["qt"] = ("volume", 0.946352946), ["floz"] = ("volume", 0.0295735295625),
        ["л"] = ("volume", 1), ["мл"] = ("volume", 0.001),
        // площадь → м²
        ["m2"] = ("area", 1), ["km2"] = ("area", 1e6), ["cm2"] = ("area", 1e-4), ["ha"] = ("area", 1e4), ["acre"] = ("area", 4046.8564224), ["ft2"] = ("area", 0.09290304),
        // скорость → м/с
        ["m/s"] = ("speed", 1), ["km/h"] = ("speed", 1 / 3.6), ["kmh"] = ("speed", 1 / 3.6), ["mph"] = ("speed", 0.44704), ["kn"] = ("speed", 0.514444), ["км/ч"] = ("speed", 1 / 3.6),
        // время → секунды
        ["s"] = ("time", 1), ["ms"] = ("time", 0.001), ["min"] = ("time", 60), ["h"] = ("time", 3600), ["d"] = ("time", 86400), ["week"] = ("time", 604800), ["year"] = ("time", 31557600),
        ["с"] = ("time", 1), ["мин"] = ("time", 60), ["ч"] = ("time", 3600), ["сут"] = ("time", 86400),
        // данные → байты
        ["b"] = ("data", 1), ["kb"] = ("data", 1e3), ["mb"] = ("data", 1e6), ["gb"] = ("data", 1e9), ["tb"] = ("data", 1e12),
        ["kib"] = ("data", 1024), ["mib"] = ("data", 1048576), ["gib"] = ("data", 1073741824), ["tib"] = ("data", 1099511627776), ["bit"] = ("data", 0.125),
        // давление → Па
        ["pa"] = ("pressure", 1), ["kpa"] = ("pressure", 1e3), ["mpa"] = ("pressure", 1e6), ["bar"] = ("pressure", 1e5), ["atm"] = ("pressure", 101325), ["psi"] = ("pressure", 6894.757293168), ["mmhg"] = ("pressure", 133.322387415),
        // энергия → Дж
        ["j"] = ("energy", 1), ["kj"] = ("energy", 1e3), ["cal"] = ("energy", 4.184), ["kcal"] = ("energy", 4184), ["wh"] = ("energy", 3600), ["kwh"] = ("energy", 3.6e6), ["ev"] = ("energy", 1.602176634e-19),
    };

    public static string Convert(double value, string from, string to)
    {
        from = Normalize(from);
        to = Normalize(to);
        if (IsTemperature(from) && IsTemperature(to))
            return Format(FromKelvin(ToKelvin(value, from), to), to);
        if (!Units.TryGetValue(from, out var f)) throw new ArgumentException("Unknown unit: " + from);
        if (!Units.TryGetValue(to, out var t)) throw new ArgumentException("Unknown unit: " + to);
        if (f.dim != t.dim) throw new ArgumentException($"Incompatible units: {from} ({f.dim}) → {to} ({t.dim})");
        return Format(value * f.factor / t.factor, to);
    }

    private static string Normalize(string u) => u.Trim().Replace("²", "2").Replace("³", "3").Replace("µ", "u").Replace("°", string.Empty).ToLowerInvariant();

    private static bool IsTemperature(string u) => u is "c" or "f" or "k" or "celsius" or "fahrenheit" or "kelvin";

    private static double ToKelvin(double v, string u) => u switch
    {
        "c" or "celsius" => v + 273.15,
        "f" or "fahrenheit" => (v - 32) * 5 / 9 + 273.15,
        _ => v,
    };

    private static double FromKelvin(double k, string u) => u switch
    {
        "c" or "celsius" => k - 273.15,
        "f" or "fahrenheit" => (k - 273.15) * 9 / 5 + 32,
        _ => k,
    };

    private static string Format(double v, string unit) => v.ToString("G12", CultureInfo.InvariantCulture) + " " + unit;

    public static string SupportedUnits => string.Join(", ", Units.Keys.Where(k => k.All(c => c < 128)).Concat(new[] { "C", "F", "K" }));
}
