using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineChatter.Core.Models;

public sealed record MemoryEstimate(long Weights, long KvCache, long Overhead, long Mmproj, int GpuLayers, int TotalLayers)
{
    public long Total => Weights + KvCache + Overhead + Mmproj;

    /// <summary>Доля, которая окажется в VRAM при заданном числе слоёв на GPU.</summary>
    public long Vram => TotalLayers <= 0 || GpuLayers <= 0 ? 0
        : (long)((Weights + KvCache) * Math.Min(1.0, (double)GpuLayers / TotalLayers)) + Overhead + Mmproj;

    public long Ram => Total - Vram + (GpuLayers > 0 ? 256L << 20 : 0);
}

public enum FitVerdict
{
    Fits,
    Tight,
    DoesNotFit,
}

/// <summary>
/// Оценка памяти: веса (≈ размер файла) + KV-кэш + рабочие буферы.
/// KV = 2 · слоёв_с_вниманием · ctx · n_head_kv · head_dim · байт_на_элемент.
/// У гибридных моделей (full_attention_interval) KV есть только у каждого N-го слоя.
/// </summary>
public static class MemoryEstimator
{
    /// <summary>Байт на элемент KV для типов кэша llama.cpp.</summary>
    public static double KvBytes(string? cacheType) => cacheType?.ToLowerInvariant() switch
    {
        "f32" => 4,
        "q8_0" => 34.0 / 32,
        "q5_0" or "q5_1" => 22.0 / 32,
        "q4_0" or "q4_1" or "iq4_nl" => 18.0 / 32,
        _ => 2, // f16 / bf16 по умолчанию
    };

    public static MemoryEstimate Estimate(GgufInfo? info, long fileBytes, int context, string? cacheTypeK = null, string? cacheTypeV = null,
        int? gpuLayers = null, long mmprojBytes = 0, int parallel = 1)
    {
        var layers = info?.BlockCount ?? 32;
        var kv = 0L;
        if (info is { BlockCount: { } n, EmbeddingLength: { } emb, HeadCount: { } heads } && heads > 0)
        {
            var kvHeads = info.HeadCountKv ?? heads;
            var keyDim = info.KeyLength ?? emb / heads;
            var valDim = info.ValueLength ?? emb / heads;
            var attnLayers = info.FullAttentionInterval is > 1 and var k ? (int)Math.Ceiling(n / (double)k) : n;
            var ctxForKv = info.SlidingWindow is > 0 and var w && w < context ? Math.Max(w, context / 4) : context; // SWA экономит часть слоёв
            kv = (long)(attnLayers * (double)ctxForKv * kvHeads * (keyDim * KvBytes(cacheTypeK) + valDim * KvBytes(cacheTypeV)));
        }
        else
        {
            // Нет метаданных — грубая оценка: ~ 0.5 МБ на 1k контекста на 1 ГБ весов
            kv = (long)(fileBytes / (double)(1L << 30) * (context / 1024.0) * (512 * 1024));
        }

        // Рабочие буферы: базовые ~300 МБ + растут с контекстом и числом слотов
        var overhead = (300L << 20) + (long)(context * 0.02 * (1 << 20) / 8) * Math.Max(1, parallel);
        var ngl = gpuLayers is null ? 0 : Math.Min(gpuLayers.Value < 0 ? layers + 1 : gpuLayers.Value, layers + 1);
        return new MemoryEstimate(fileBytes, kv, overhead, mmprojBytes, ngl, layers + 1);
    }

    public static FitVerdict Verdict(long needed, long available)
    {
        if (available <= 0) return FitVerdict.Tight;
        if (needed <= available * 0.85) return FitVerdict.Fits;
        if (needed <= available) return FitVerdict.Tight;
        return FitVerdict.DoesNotFit;
    }

    /// <summary>Наибольший «круглый» контекст, который помещается в доступную память (и не больше обучающего).</summary>
    public static int SuggestContext(GgufInfo? info, long fileBytes, long availableBytes, long mmprojBytes = 0)
    {
        var max = info?.ContextLength is > 0 and var c ? c : 32768;
        var candidates = new[] { 262144, 131072, 65536, 32768, 16384, 8192, 4096, 2048 };
        foreach (var ctx in candidates.Where(x => x <= max))
        {
            var e = Estimate(info, fileBytes, ctx, mmprojBytes: mmprojBytes);
            if (e.Total <= availableBytes * 0.8) return ctx;
        }
        return Math.Min(max, 2048);
    }

    /// <summary>Сколько слоёв поместится в VRAM (−1 — все).</summary>
    public static int SuggestGpuLayers(GgufInfo? info, long fileBytes, int context, long freeVram, long mmprojBytes = 0)
    {
        if (freeVram <= 0) return 0;
        var full = Estimate(info, fileBytes, context, gpuLayers: -1, mmprojBytes: mmprojBytes);
        if (full.Vram <= freeVram * 0.9) return -1;
        var perLayer = (full.Weights + full.KvCache) / (double)full.TotalLayers;
        var budget = freeVram * 0.9 - full.Overhead - mmprojBytes;
        return Math.Max(0, (int)(budget / perLayer));
    }

    /// <summary>Приблизительный размер файла по числу параметров и кванту (для моделей без размера в API).</summary>
    public static long SizeFromParams(double parameters, double bitsPerWeight) => (long)(parameters * bitsPerWeight / 8);

    public static IReadOnlyList<int> ContextPresets { get; } = new[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144 };
}
