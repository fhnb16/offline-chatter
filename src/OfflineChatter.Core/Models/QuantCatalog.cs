using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Models;

/// <summary>Сведения о кванте: примерные биты на вес и место в шкале качества.</summary>
public sealed record QuantInfo(string Name, double BitsPerWeight, int QualityRank, string Tier)
{
    /// <summary>Tier: "lossless", "high", "balanced", "small", "tiny".</summary>
    public bool IsRecommendedDefault => Name is "Q4_K_M" or "UD-Q4_K_XL" or "Q4_K_XL";
}

/// <summary>
/// Определение кванта по имени файла и человеческие описания.
/// Шкала (качество ↑): IQ1 &lt; IQ2 &lt; Q2_K &lt; IQ3 &lt; Q3_K &lt; IQ4 &lt; Q4 &lt; Q5 &lt; Q6_K &lt; Q8_0 &lt; BF16/F16 &lt; F32.
/// </summary>
public static class QuantCatalog
{
    private static readonly Regex QuantRx = new(
        @"(?<![A-Za-z0-9])(?<ud>UD-)?(?<q>IQ[1-4]_(?:XXS|XS|S|M|NL|L)|Q[2-8]_K(?:_(?:XXS|XS|S|M|L|XL))?|Q[2-8]_[01]|Q4_0_4_[48]|TQ[12]_0|MXFP4(?:_MOE)?|BF16|F16|F32|FP16|FP32)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, (double bpw, int rank)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IQ1_S"] = (1.56, 1), ["IQ1_M"] = (1.75, 2),
        ["IQ2_XXS"] = (2.06, 3), ["IQ2_XS"] = (2.31, 4), ["IQ2_S"] = (2.5, 5), ["IQ2_M"] = (2.7, 6),
        ["TQ1_0"] = (1.69, 2), ["TQ2_0"] = (2.06, 3),
        ["Q2_K"] = (2.96, 7), ["Q2_K_S"] = (2.9, 7), ["Q2_K_XL"] = (3.1, 8),
        ["IQ3_XXS"] = (3.06, 8), ["IQ3_XS"] = (3.3, 9), ["IQ3_S"] = (3.44, 10), ["IQ3_M"] = (3.66, 11),
        ["Q3_K_S"] = (3.5, 10), ["Q3_K_M"] = (3.91, 12), ["Q3_K_L"] = (4.27, 13), ["Q3_K_XL"] = (4.3, 13),
        ["IQ4_XS"] = (4.25, 14), ["IQ4_NL"] = (4.5, 15),
        ["Q4_0"] = (4.55, 15), ["Q4_1"] = (5.0, 16), ["Q4_K_S"] = (4.58, 16), ["Q4_K_M"] = (4.85, 17), ["Q4_K_L"] = (5.0, 17), ["Q4_K_XL"] = (5.0, 18),
        ["MXFP4"] = (4.25, 16), ["MXFP4_MOE"] = (4.25, 16),
        ["Q5_0"] = (5.54, 18), ["Q5_1"] = (6.0, 19), ["Q5_K_S"] = (5.54, 19), ["Q5_K_M"] = (5.69, 20), ["Q5_K_L"] = (5.8, 20), ["Q5_K_XL"] = (5.9, 21),
        ["Q6_K"] = (6.59, 22), ["Q6_K_L"] = (6.7, 22), ["Q6_K_XL"] = (6.8, 23),
        ["Q8_0"] = (8.5, 24), ["Q8_K_XL"] = (8.7, 25),
        ["BF16"] = (16, 26), ["F16"] = (16, 26), ["FP16"] = (16, 26),
        ["F32"] = (32, 27), ["FP32"] = (32, 27),
    };

    /// <summary>Квант из имени файла: "Qwen3-8B-UD-Q4_K_XL.gguf" → "UD-Q4_K_XL".</summary>
    public static QuantInfo? FromFileName(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        Match? best = null;
        foreach (Match m in QuantRx.Matches(name)) best = m; // последнее совпадение — квант (в имени модели бывают похожие куски)
        if (best == null) return null;
        var q = best.Groups["q"].Value.ToUpperInvariant();
        if (q == "FP16") q = "F16";
        if (q == "FP32") q = "F32";
        var ud = best.Groups["ud"].Success;
        var (bpw, rank) = Known.TryGetValue(q, out var k) ? k : (4.5, 15);
        var display = (ud ? "UD-" : string.Empty) + q;
        if (ud) rank++;
        return new QuantInfo(display, bpw, rank, Tier(rank));
    }

    private static string Tier(int rank) => rank switch
    {
        >= 26 => "lossless",
        >= 22 => "high",
        >= 16 => "balanced",
        >= 10 => "small",
        _ => "tiny",
    };

    /// <summary>Номер шарда: "model-00001-of-00003.gguf" → (1, 3).</summary>
    public static (int index, int count)? Shard(string fileName)
    {
        var m = Regex.Match(fileName, @"-(?<i>\d{5})-of-(?<n>\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return (int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture));
    }

    public static bool IsMmproj(string fileName) =>
        System.IO.Path.GetFileName(fileName).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
        || fileName.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Предпочтение для mmproj: F16 → BF16 → Q8_0 → остальные.</summary>
    public static int MmprojPreference(string fileName)
    {
        var n = fileName.ToUpperInvariant();
        if (n.Contains("F16") && !n.Contains("BF16")) return 0;
        if (n.Contains("BF16")) return 1;
        if (n.Contains("Q8")) return 2;
        if (n.Contains("F32")) return 3;
        return 4;
    }

    /// <summary>Параметры из метки размера "0.8B", "7B", "8x7B", "235B-A22B".</summary>
    public static double? ParamsFromLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return null;
        var m = Regex.Match(label, @"(?:(?<x>\d+)x)?(?<n>\d+(?:\.\d+)?)(?<u>[BMK])", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var mult = m.Groups["u"].Value.ToUpperInvariant() switch { "B" => 1e9, "M" => 1e6, _ => 1e3 };
        var x = m.Groups["x"].Success ? int.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture) : 1;
        return n * mult * x;
    }

    public static IEnumerable<string> KnownNames => Known.Keys.OrderBy(k => Known[k].rank);
}
