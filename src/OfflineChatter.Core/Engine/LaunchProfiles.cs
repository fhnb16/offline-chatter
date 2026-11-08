using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Engine;

/// <summary>Параметры сэмплинга по умолчанию (уходят в каждый запрос чата, могут переопределяться в чате).</summary>
public sealed class SamplingSettings
{
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 40;
    public double MinP { get; set; } = 0.05;
    public double RepeatPenalty { get; set; } = 1.0;
    /// <summary>-1 — без ограничения.</summary>
    public int MaxTokens { get; set; } = -1;
    public int? Seed { get; set; }

    public SamplingSettings Clone() => (SamplingSettings)MemberwiseClone();
}

/// <summary>Профиль запуска llama-server. null в полях = «авто».</summary>
public sealed class LaunchProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Profile";
    public bool BuiltIn { get; set; }

    /// <summary>Пресет, от которого вычисляются значения «авто»: fast / balanced / quality.</summary>
    public string Preset { get; set; } = "balanced";

    public int? ContextSize { get; set; }
    /// <summary>null — авто (--fit или своя оценка), -1 — все слои на GPU, 0 — только CPU.</summary>
    public int? GpuLayers { get; set; }
    /// <summary>
    /// Строго на GPU (только вместе с «все слои»): без --fit, KV-кэш в видеопамяти, а если модель не поместилась
    /// целиком — сервер останавливается с ошибкой, а не работает медленнее с частью слоёв на CPU.
    /// </summary>
    public bool StrictGpu { get; set; }
    public int? Threads { get; set; }
    public int? BatchSize { get; set; }
    public int? UBatchSize { get; set; }
    public int? Parallel { get; set; } = 1;
    public string FlashAttention { get; set; } = "auto";
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public bool Mlock { get; set; }
    public bool NoMmap { get; set; }
    /// <summary>Использовать встроенный автоподбор llama.cpp (--fit), если версия его поддерживает.</summary>
    public bool UseFit { get; set; } = true;
    public string? ReasoningFormat { get; set; } = "deepseek";
    public string? DraftModelPath { get; set; }
    public string? ExtraArgs { get; set; }

    public SamplingSettings Sampling { get; set; } = new();

    public LaunchProfile Clone()
    {
        var p = (LaunchProfile)MemberwiseClone();
        p.Sampling = Sampling.Clone();
        return p;
    }

    public static IReadOnlyList<LaunchProfile> CreateBuiltIns() => new[]
    {
        new LaunchProfile
        {
            Id = "fast", Name = "Fast", BuiltIn = true, Preset = "fast",
            CacheTypeK = "q8_0", CacheTypeV = "q8_0", FlashAttention = "on",
            Sampling = new SamplingSettings { Temperature = 0.7, MaxTokens = 2048 },
        },
        new LaunchProfile { Id = "balanced", Name = "Balanced", BuiltIn = true, Preset = "balanced" },
        new LaunchProfile
        {
            Id = "quality", Name = "Quality", BuiltIn = true, Preset = "quality",
            Sampling = new SamplingSettings { Temperature = 0.6, TopP = 0.95, MinP = 0.0 },
        },
    };
}

/// <summary>Профили в config/profiles/*.json. Встроенные создаются при первом запуске и могут правиться.</summary>
public sealed class ProfileStore
{
    private readonly string _dir;

    public ProfileStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var p in LaunchProfile.CreateBuiltIns())
            if (!File.Exists(PathOf(p.Id))) Save(p);
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".json");

    public IReadOnlyList<LaunchProfile> List()
    {
        var list = new List<LaunchProfile>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var p = JsonFile.Read<LaunchProfile>(f);
                if (p == null) continue;
                p.Id = Path.GetFileNameWithoutExtension(f);
                p.Sampling ??= new SamplingSettings();
                list.Add(p);
            }
            catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException)
            {
                Log.Warn("Profiles", "Bad profile " + f, ex);
            }
        }
        var order = new[] { "fast", "balanced", "quality" };
        return list.OrderBy(p => Array.IndexOf(order, p.Id) is var i && i >= 0 ? i : 99).ThenBy(p => p.Name).ToList();
    }

    public LaunchProfile Get(string? id) =>
        List().FirstOrDefault(p => p.Id == id) ?? List().FirstOrDefault(p => p.Id == "balanced") ?? LaunchProfile.CreateBuiltIns()[1];

    public void Save(LaunchProfile p) => JsonFile.WriteAtomic(PathOf(p.Id), p);

    public void Delete(LaunchProfile p)
    {
        if (p.BuiltIn) return;
        File.Delete(PathOf(p.Id));
    }

    /// <summary>Встроенный профиль вернуть к заводским значениям.</summary>
    public LaunchProfile Reset(string id)
    {
        var p = LaunchProfile.CreateBuiltIns().First(x => x.Id == id);
        Save(p);
        return p;
    }
}

public sealed record ProfileDifference(string Field, string? Left, string? Right);

public static class ProfileDiff
{
    /// <summary>Различающиеся поля двух профилей (включая сэмплинг).</summary>
    public static IReadOnlyList<ProfileDifference> Compare(LaunchProfile a, LaunchProfile b)
    {
        var list = new List<ProfileDifference>();
        void Walk(object x, object y, string prefix)
        {
            foreach (var prop in x.GetType().GetProperties())
            {
                if (prop.Name is nameof(LaunchProfile.Id) or nameof(LaunchProfile.Name) or nameof(LaunchProfile.BuiltIn)) continue;
                var vx = prop.GetValue(x);
                var vy = prop.GetValue(y);
                if (prop.PropertyType == typeof(SamplingSettings))
                {
                    Walk(vx!, vy!, prop.Name + ".");
                    continue;
                }
                var sx = Convert.ToString(vx, System.Globalization.CultureInfo.InvariantCulture);
                var sy = Convert.ToString(vy, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(sx, sy, StringComparison.Ordinal)) list.Add(new ProfileDifference(prefix + prop.Name, sx, sy));
            }
        }
        Walk(a, b, string.Empty);
        return list;
    }
}

public sealed record ResolvedLaunch(ServerLaunchOptions Options, MemoryEstimate Estimate, FitVerdict Verdict, IReadOnlyList<string> Notes,
    GgufInfo? Info = null);

/// <summary>
/// Превращает профиль + модель + железо + возможности версии llama.cpp в конкретные аргументы.
/// «Авто»: контекст по памяти и пресету, слои GPU — через --fit (если есть) или по оценке VRAM,
/// потоки — по числу физических ядер.
/// </summary>
public static class ProfileResolver
{
    public static ResolvedLaunch Resolve(LaunchProfile p, LocalModel model, GgufInfo? info, HardwareInfo hw, EngineInstall engine, FlagCatalog? flags)
    {
        var notes = new List<string>();
        var mmBytes = model.HasVision ? new FileInfo(model.MmprojPath!).Length : 0;
        var gpuCapable = engine.Manifest.Backend != EngineBackend.Cpu;
        var vram = gpuCapable ? hw.BestGpu?.DedicatedMemory ?? 0 : 0;
        var ram = hw.AvailableRam > 0 ? hw.AvailableRam : hw.TotalRam;

        // ----- контекст -----
        var maxCtx = info?.ContextLength is > 0 and var mc ? mc : 32768;
        var presetCap = p.Preset switch { "fast" => 4096, "quality" => 65536, _ => 16384 };
        int ctx;
        if (p.ContextSize is { } fixedCtx)
        {
            ctx = fixedCtx;
        }
        else
        {
            var budget = gpuCapable && vram > 0 ? Math.Max(vram, ram) : ram;
            ctx = Math.Min(presetCap, MemoryEstimator.SuggestContext(info, model.SizeBytes, budget, mmBytes));
            ctx = Math.Min(ctx, maxCtx);
            notes.Add($"ctx auto = {ctx}");
        }

        // ----- слои на GPU -----
        var fitSupported = flags?.Supports("--fit") == true;
        int? ngl = p.GpuLayers;
        bool? fit = null;
        var strict = false;
        if (!gpuCapable)
        {
            ngl = flags == null || flags.Supports("--n-gpu-layers") ? 0 : null;
        }
        else if (p.StrictGpu && ngl is < 0)
        {
            // --fit при нехватке памяти сам переносит часть модели на CPU — в строгом режиме это запрещено
            strict = true;
            if (fitSupported) fit = false;
            notes.Add("strict GPU");
        }
        else if (ngl == null)
        {
            if (p.UseFit && fitSupported)
            {
                fit = true;
                notes.Add("ngl auto = llama.cpp --fit");
            }
            else
            {
                ngl = MemoryEstimator.SuggestGpuLayers(info, model.SizeBytes, ctx, vram, mmBytes);
                notes.Add($"ngl auto = {ngl}");
            }
        }

        // ----- потоки -----
        // Генерация упирается в пропускную способность памяти: больше потоков, чем физических ядер, только мешает
        var threads = p.Threads ?? Math.Max(1, hw.PhysicalCores);

        var options = new ServerLaunchOptions
        {
            ModelPath = model.Path,
            MmprojPath = model.HasVision ? model.MmprojPath : null,
            Alias = Path.GetFileNameWithoutExtension(model.Path),
            ContextSize = ctx,
            GpuLayers = ngl,
            Fit = fit,
            Threads = threads,
            BatchSize = p.BatchSize,
            UBatchSize = p.UBatchSize,
            Parallel = p.Parallel,
            FlashAttention = p.FlashAttention,
            CacheTypeK = p.CacheTypeK,
            CacheTypeV = p.CacheTypeV,
            Mlock = p.Mlock,
            NoMmap = p.NoMmap,
            ReasoningFormat = p.ReasoningFormat,
            DraftModelPath = string.IsNullOrWhiteSpace(p.DraftModelPath) ? null : p.DraftModelPath,
            ExtraArgs = p.ExtraArgs,
            RequireFullOffload = strict,
        };

        var estimate = MemoryEstimator.Estimate(info, model.SizeBytes, ctx, p.CacheTypeK, p.CacheTypeV,
            gpuCapable ? (ngl ?? -1) : 0, mmBytes, p.Parallel ?? 1);
        FitVerdict verdict;
        if (gpuCapable && vram > 0)
        {
            var vramOk = MemoryEstimator.Verdict(estimate.Vram, vram);
            var ramOk = MemoryEstimator.Verdict(estimate.Ram, ram);
            verdict = (FitVerdict)Math.Max((int)vramOk, (int)ramOk);
            if (fit == true && verdict == FitVerdict.DoesNotFit) verdict = FitVerdict.Tight; // --fit сам выгрузит часть на CPU
        }
        else
        {
            verdict = MemoryEstimator.Verdict(estimate.Total, ram);
        }
        return new ResolvedLaunch(options, estimate, verdict, notes, info);
    }
}
