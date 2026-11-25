using System;
using OfflineChatter.Core.Engine;

namespace OfflineChatter.Core.Images;

public static class ImageMemoryPolicy
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    public static void Configure(ImageServerOptions options, ImageLaunchSettings settings, FlagCatalog? flags,
        bool exposeApi, long totalRam, bool recoveringRam)
    {
        if (exposeApi) return;

        options.CompletedJobTtl = 60;
        options.WeightStagingTtl = 15;
        var limitedRam = totalRam > 0 && totalRam <= 32 * GiB;
        options.WeightResidencyTtl = limitedRam ? 45 : 90;
        if (flags?.Supports("--conditioning-cache-size") == true)
        {
            if (recoveringRam) options.ConditioningCacheSize = Math.Min(options.ConditioningCacheSize ?? 1, 1);
            else if (limitedRam && settings.ConditioningCacheSize == null)
                options.ConditioningCacheSize = Math.Min(options.ConditioningCacheSize ?? 2, 2);
        }

        if (!recoveringRam) return;
        if (settings.Mmap != false && flags?.Supports("--mmap") == true) options.Mmap = true;
        // Для Qwen-Image 2.1 профиль экономии ОЗУ уже использует тот же backend для текстового кодировщика.
        if (options.ParamsBackend == null && options.Files.ContainsKey(ImageComponent.Llm)
            && options.Files.ContainsKey(ImageComponent.LlmVision) && flags?.Supports("--params-backend") == true)
            options.ParamsBackend = "te=disk";
    }

    /// <summary>Освобождаем память работающего процесса, если он занимает существенный объём и у системы мало запаса.</summary>
    public static bool ShouldRelease(long totalRam, long availableRam, long serverPrivateBytes)
    {
        if (totalRam <= 0 || availableRam < 0 || serverPrivateBytes < 768 * MiB) return false;
        var reserve = Math.Max(512 * MiB, Math.Min(2 * GiB, totalRam / 16));
        return availableRam < reserve;
    }
}
