using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Net;

namespace OfflineChatter.Core.Engine;

public enum EngineBackend
{
    Cpu,
    Vulkan,
    Cuda,
    Rocm,
    Sycl,
    OpenVino,
    OpenClAdreno,
    Custom,
}

public enum CpuArch
{
    X64,
    Arm64,
}

public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Sha256);

public sealed record EngineRelease(string Tag, DateTime PublishedAt, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    public int BuildNumber
    {
        get
        {
            // "b12176" или "llama-b12176-win7"
            var m = Regex.Match(Tag, @"b(\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
    }
}

/// <summary>Вариант сборки llama.cpp для Windows внутри релиза.</summary>
public sealed record EngineVariant(
    EngineBackend Backend,
    CpuArch Arch,
    string? BackendVersion,
    ReleaseAsset Asset,
    ReleaseAsset? CudaRuntime)
{
    public long TotalSize => Asset.Size + (CudaRuntime?.Size ?? 0);

    /// <summary>Ключ для папки: cpu-x64, cuda-12.4-x64…</summary>
    public string Key => (Backend.ToString().ToLowerInvariant() + (BackendVersion != null ? "-" + BackendVersion : string.Empty) + "-" + Arch.ToString().ToLowerInvariant());
}

/// <summary>Разбор имён ассетов релиза (регулярные выражения, а не зашитые имена: формат меняется).</summary>
public static class AssetMatcher
{
    private static readonly Regex MainAsset = new(
        @"^llama-(?<tag>b\d+)-bin-win7?-(?<backend>cpu|vulkan|cuda-(?<ver>[\d.]+)|rocm-(?<ver>[\d.]+)|hip(?:-radeon)?|sycl|openvino-(?<ver>[\d.]+)|opencl-adreno)-(?<arch>x64|arm64)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CudaRuntime = new(
        @"^cudart-llama-(?:b\d+-)?bin-win-cuda-(?<ver>[\d.]+)-(?<arch>x64|arm64)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Win7CpuAsset = new(
        @"^llama\.cpp-v\d+\.\d+\.\d+-win7-x64\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<EngineVariant> WindowsVariants(EngineRelease release)
    {
        var runtimes = release.Assets
            .Select(a => (a, m: CudaRuntime.Match(a.Name)))
            .Where(x => x.m.Success)
            .ToDictionary(x => (x.m.Groups["ver"].Value, x.m.Groups["arch"].Value.ToLowerInvariant()), x => x.a);

        var list = new List<EngineVariant>();
        foreach (var asset in release.Assets)
        {
            if (Win7CpuAsset.IsMatch(asset.Name))
            {
                list.Add(new EngineVariant(EngineBackend.Cpu, CpuArch.X64, null, asset, null));
                continue;
            }

            var m = MainAsset.Match(asset.Name);
            if (!m.Success) continue;
            var b = m.Groups["backend"].Value.ToLowerInvariant();
            var ver = m.Groups["ver"].Success ? m.Groups["ver"].Value : null;
            var archText = m.Groups["arch"].Value.ToLowerInvariant();
            var arch = archText == "arm64" ? CpuArch.Arm64 : CpuArch.X64;
            var backend = b switch
            {
                "cpu" => EngineBackend.Cpu,
                "vulkan" => EngineBackend.Vulkan,
                "sycl" => EngineBackend.Sycl,
                "opencl-adreno" => EngineBackend.OpenClAdreno,
                _ when b.StartsWith("cuda", StringComparison.Ordinal) => EngineBackend.Cuda,
                _ when b.StartsWith("rocm", StringComparison.Ordinal) || b.StartsWith("hip", StringComparison.Ordinal) => EngineBackend.Rocm,
                _ when b.StartsWith("openvino", StringComparison.Ordinal) => EngineBackend.OpenVino,
                _ => EngineBackend.Custom,
            };
            ReleaseAsset? rt = null;
            if (backend == EngineBackend.Cuda && ver != null) runtimes.TryGetValue((ver, archText), out rt);
            list.Add(new EngineVariant(backend, arch, ver, asset, rt));
        }
        return list.OrderBy(v => v.Backend).ThenBy(v => v.Arch).ThenByDescending(v => v.BackendVersion).ToList();
    }

    /// <summary>
    /// Какие варианты имеют смысл на этой машине: архитектура, ОС (на Win7 — только CPU и Vulkan),
    /// версия CUDA драйвера NVIDIA.
    /// </summary>
    public static bool IsApplicable(EngineVariant v, HardwareInfo hw, bool isWin7)
    {
        var arch = hw.IsArm64 ? CpuArch.Arm64 : CpuArch.X64;
        if (v.Arch != arch) return false;
        if (isWin7 && v.Backend is not (EngineBackend.Cpu or EngineBackend.Vulkan)) return false;
        return v.Backend switch
        {
            EngineBackend.Cuda => hw.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia) && CudaFits(v.BackendVersion, hw.CudaDriverVersion),
            EngineBackend.Rocm => hw.Gpus.Any(g => g.Vendor == GpuVendor.Amd),
            EngineBackend.Sycl or EngineBackend.OpenVino => hw.Gpus.Any(g => g.Vendor == GpuVendor.Intel),
            EngineBackend.OpenClAdreno => hw.Gpus.Any(g => g.Vendor == GpuVendor.Qualcomm),
            _ => true,
        };
    }

    /// <summary>Драйвер должен поддерживать CUDA не ниже версии сборки (12040 = 12.4).</summary>
    public static bool CudaFits(string? buildVersion, int? driverCuda)
    {
        if (driverCuda == null || buildVersion == null) return false;
        var parts = buildVersion.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mi) ? mi : 0;
        return driverCuda.Value >= major * 1000 + minor * 10;
    }

    /// <summary>
    /// Рекомендация: CUDA (самая новая подходящая) для NVIDIA с ≥ 4 ГБ VRAM, иначе Vulkan при дискретной видеокарте,
    /// иначе CPU. По ТЗ «по умолчанию CPU» — рекомендация только подсвечивается, выбор остаётся за пользователем.
    /// </summary>
    public static EngineVariant? Recommend(IReadOnlyList<EngineVariant> variants, HardwareInfo hw, bool isWin7)
    {
        var ok = variants.Where(v => IsApplicable(v, hw, isWin7)).ToList();
        var gpu = hw.BestGpu;
        const long fourGb = 4L << 30;
        if (gpu is { Vendor: GpuVendor.Nvidia } && gpu.DedicatedMemory >= fourGb)
        {
            var cuda = ok.Where(v => v.Backend == EngineBackend.Cuda).OrderByDescending(v => Version.TryParse(v.BackendVersion, out var x) ? x : new Version()).FirstOrDefault();
            if (cuda != null) return cuda;
        }
        if (gpu != null && gpu.DedicatedMemory >= fourGb && hw.VulkanAvailable)
        {
            var vk = ok.FirstOrDefault(v => v.Backend == EngineBackend.Vulkan);
            if (vk != null) return vk;
        }
        return ok.FirstOrDefault(v => v.Backend == EngineBackend.Cpu);
    }
}

/// <summary>Список релизов llama.cpp через GitHub API. Все релизы сейчас помечены как prerelease — /releases/latest не подходит.</summary>
public sealed class GitHubReleases
{
    public const string Repo = "ggml-org/llama.cpp";

    /// <summary>Сборка, проверенная при разработке (закреплённый канал).</summary>
    public const string KnownGoodTag = "b12176";

    private readonly HttpService _http;

    public GitHubReleases(HttpService http) => _http = http;

    /// <param name="repo">По умолчанию официальный ggml-org/llama.cpp; иначе — репозиторий программы или свой.</param>
    /// <param name="onlyLlamaTags">Оставить только теги llama-* (в репозитории программы рядом лежат её собственные релизы).</param>
    public async Task<IReadOnlyList<EngineRelease>> ListAsync(int count, CancellationToken ct, string? repo = null, bool onlyLlamaTags = true)
    {
        var api = _http.Settings.GitHubApiEndpoint.TrimEnd('/');
        var json = await _http.GetJsonAsync($"{api}/repos/{repo ?? Repo}/releases?per_page={Math.Min(100, count)}", ct).ConfigureAwait(false);
        var list = ((JArray)json).Select(Parse).ToList();
        // В репозитории программы рядом лежат релизы самой программы — берём только сборки llama.cpp
        return repo == null || !onlyLlamaTags ? list : list.Where(r => r.Tag.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<EngineRelease> GetByTagAsync(string tag, CancellationToken ct)
    {
        var api = _http.Settings.GitHubApiEndpoint.TrimEnd('/');
        var json = await _http.GetJsonAsync($"{api}/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
        return Parse(json);
    }

    internal static EngineRelease Parse(JToken r)
    {
        var assets = (r["assets"] as JArray ?? new JArray()).Select(a =>
        {
            var digest = (string?)a["digest"];
            var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : null;
            return new ReleaseAsset((string)a["name"]!, (long?)a["size"] ?? 0, (string)a["browser_download_url"]!, sha);
        }).ToList();
        return new EngineRelease((string)r["tag_name"]!, (DateTime?)r["published_at"] ?? DateTime.MinValue, (bool?)r["prerelease"] ?? false, assets);
    }

    /// <summary>Применяет префикс-зеркало для скачивания с GitHub, если он задан.</summary>
    public string MirrorUrl(string url)
    {
        var prefix = _http.Settings.GitHubDownloadPrefix?.Trim();
        return string.IsNullOrEmpty(prefix) ? url : prefix!.TrimEnd('/') + "/" + url;
    }
}
