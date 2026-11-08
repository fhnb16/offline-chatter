using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OfflineChatter.Core.Engine;

/// <summary>Параметры запуска llama-server. Профили (этап 5) превращаются в этот объект.</summary>
public sealed class ServerLaunchOptions
{
    public string ModelPath { get; set; } = string.Empty;
    public string? MmprojPath { get; set; }
    public string? Alias { get; set; }

    public int? ContextSize { get; set; }
    /// <summary>
    /// null — не задавать (llama.cpp сам подберёт через --fit, если поддерживается); отрицательное — все слои.
    /// «-ngl -1» у llama.cpp означает «по умолчанию», а это в разных версиях то CPU, то автоподбор, — поэтому
    /// «все» передаётся как «all» (новые сборки) или 999 (старые).
    /// </summary>
    public int? GpuLayers { get; set; }
    public int? Threads { get; set; }
    public int? BatchSize { get; set; }
    public int? UBatchSize { get; set; }
    public int? Parallel { get; set; }

    /// <summary>"auto", "on", "off".</summary>
    public string? FlashAttention { get; set; }
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public bool Mlock { get; set; }
    public bool NoMmap { get; set; }

    /// <summary>Автоподбор параметров под память (--fit on), если версия поддерживает.</summary>
    public bool? Fit { get; set; }

    public string? ReasoningFormat { get; set; }
    public string? DraftModelPath { get; set; }
    public int? DraftGpuLayers { get; set; }

    /// <summary>
    /// Строго на GPU: если после загрузки не все слои оказались в видеопамяти, сервер останавливается с ошибкой
    /// (вместо тихой и медленной работы с частью модели на CPU). В аргументы не попадает.
    /// </summary>
    public bool RequireFullOffload { get; set; }

    public bool Metrics { get; set; } = true;
    public string? SlotSavePath { get; set; }

    /// <summary>Произвольные аргументы пользователя (строка как в командной строке).</summary>
    public string? ExtraArgs { get; set; }

    /// <summary>
    /// Собирает аргументы. Флаги, которых нет в <paramref name="flags"/> (если каталог известен), пропускаются
    /// и попадают в <paramref name="skipped"/> — чтобы UI мог предупредить.
    /// </summary>
    public List<string> BuildArgs(string host, int port, string? apiKey, FlagCatalog? flags, List<string>? skipped = null)
    {
        var args = new List<string>();

        void Add(string name, string? value = null, string? alt = null)
        {
            var n = name;
            if (flags != null && !flags.Supports(n))
            {
                if (alt != null && flags.Supports(alt)) n = alt;
                else
                {
                    skipped?.Add(name);
                    return;
                }
            }
            args.Add(n);
            if (value != null) args.Add(value);
        }

        string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        Add("--model", ModelPath, "-m");
        if (!string.IsNullOrEmpty(MmprojPath)) Add("--mmproj", MmprojPath);
        if (!string.IsNullOrEmpty(Alias)) Add("--alias", Alias);
        Add("--host", host);
        Add("--port", I(port));
        if (!string.IsNullOrEmpty(apiKey)) Add("--api-key", apiKey);
        if (ContextSize is { } c) Add("--ctx-size", I(c), "-c");
        if (GpuLayers is { } ngl) Add("--n-gpu-layers", ngl < 0 ? AllLayersValue(flags) : I(ngl), "-ngl");
        if (Threads is { } t) Add("--threads", I(t), "-t");
        if (BatchSize is { } b) Add("--batch-size", I(b), "-b");
        if (UBatchSize is { } ub) Add("--ubatch-size", I(ub), "-ub");
        if (Parallel is { } np) Add("--parallel", I(np), "-np");
        if (!string.IsNullOrEmpty(FlashAttention)) Add("--flash-attn", FlashAttention, "-fa");
        if (!string.IsNullOrEmpty(CacheTypeK)) Add("--cache-type-k", CacheTypeK, "-ctk");
        if (!string.IsNullOrEmpty(CacheTypeV)) Add("--cache-type-v", CacheTypeV, "-ctv");
        if (Mlock) Add("--mlock");
        if (NoMmap) Add("--no-mmap");
        if (Fit is { } fit) Add("--fit", fit ? "on" : "off");
        if (!string.IsNullOrEmpty(ReasoningFormat)) Add("--reasoning-format", ReasoningFormat);
        if (!string.IsNullOrEmpty(DraftModelPath))
        {
            Add("--model-draft", DraftModelPath, "-md");
            if (DraftGpuLayers is { } dngl) Add("--n-gpu-layers-draft", I(dngl), "-ngld");
        }
        if (Metrics) Add("--metrics");
        if (!string.IsNullOrEmpty(SlotSavePath)) Add("--slot-save-path", SlotSavePath);
        // Встроенный веб-интерфейс сервера не нужен — у нас свой
        if (flags == null || flags.Supports("--no-webui")) args.Add("--no-webui");

        if (!string.IsNullOrWhiteSpace(ExtraArgs)) args.AddRange(SplitCommandLine(ExtraArgs!));
        return args;
    }

    /// <summary>Значение «все слои на GPU» для этой версии: «all», если справка его упоминает, иначе 999.</summary>
    public static string AllLayersValue(FlagCatalog? flags)
    {
        var f = flags?.Find("--n-gpu-layers") ?? flags?.Find("-ngl");
        return f != null && f.Description.IndexOf("'all'", StringComparison.Ordinal) >= 0 ? "all" : "999";
    }

    /// <summary>Склейка аргументов по правилам CommandLineToArgvW.</summary>
    public static string JoinCommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Разбор строки дополнительных аргументов (кавычки, экранирование).</summary>
    public static List<string> SplitCommandLine(string s)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        var has = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\\' && i + 1 < s.Length && s[i + 1] == '"')
            {
                cur.Append('"');
                i++;
                has = true;
            }
            else if (ch == '"')
            {
                inQuotes = !inQuotes;
                has = true;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (has) result.Add(cur.ToString());
                cur.Clear();
                has = false;
            }
            else
            {
                cur.Append(ch);
                has = true;
            }
        }
        if (has) result.Add(cur.ToString());
        return result;
    }
}
