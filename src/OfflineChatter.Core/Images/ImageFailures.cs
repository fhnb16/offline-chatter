using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Images;

/// <summary>Причина неудачного задания по выводу sd-server.</summary>
public enum ImageFailureKind
{
    Unknown,
    /// <summary>Менеджер памяти sd.cpp не нашёл места в ОЗУ («RAM weight/graph capacity exceeded») — в подкачку он не уходит.</summary>
    RamExhausted,
    /// <summary>Не хватило видеопамяти.</summary>
    VramExhausted,
}

/// <summary>
/// Объяснение ошибки задания. Сервер возвращает лишь «prompt encoding failed» / «VAE encode failed»,
/// а причина видна только в его выводе: «[E] RAM weight capacity exceeded. Requested … bytes, … available».
/// </summary>
public static class ImageFailures
{
    private static readonly Regex Capacity = new(@"(?<dev>\S+)\s+(?:weight|graph|compute|cache|workspace)?\s*capacity exceeded",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DeviceOom = new(@"out of memory|cudaMalloc failed|ErrorOutOfDeviceMemory|failed to allocate .*(CUDA|Vulkan|VRAM)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex HostOom = new(@"failed to allocate .*(CPU|host)|std::bad_alloc",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ImageFailureKind Classify(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = Capacity.Match(line);
            if (m.Success)
            {
                var dev = m.Groups["dev"].Value.TrimEnd(':');
                return string.Equals(dev, "RAM", System.StringComparison.OrdinalIgnoreCase) || dev.StartsWith("CPU", System.StringComparison.OrdinalIgnoreCase)
                    ? ImageFailureKind.RamExhausted
                    : ImageFailureKind.VramExhausted;
            }
            if (HostOom.IsMatch(line)) return ImageFailureKind.RamExhausted;
            if (DeviceOom.IsMatch(line)) return ImageFailureKind.VramExhausted;
        }
        return ImageFailureKind.Unknown;
    }
}
