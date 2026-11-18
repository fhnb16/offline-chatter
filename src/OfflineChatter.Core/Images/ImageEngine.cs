using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Images;

/// <summary>
/// Разбор ассетов релизов stable-diffusion.cpp (leejet/stable-diffusion.cpp).
/// Имена: sd-master-532d2c1-bin-win-{cpu|vulkan|cuda12|rocm-10.1.0}-x64.zip, рантайм CUDA — cudart-sd-bin-win-cu12-x64.zip.
/// В старых релизах CPU-сборки делились по набору инструкций (avx, avx2, avx512, noavx).
/// </summary>
public static class SdAssetMatcher
{
    public const string Repo = "leejet/stable-diffusion.cpp";

    private static readonly Regex MainAsset = new(
        @"^sd-.+?-bin-win-(?<backend>cpu|noavx|avx|avx2|avx512|vulkan|cuda(?<ver>\d+(?:\.\d+)?)|cu(?<ver>\d+(?:\.\d+)?)|rocm-(?<ver>[\d.]+)|hip(?:-[\w.]+)?)-(?<arch>x64|arm64)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CudaRuntime = new(
        @"^cudart-sd-bin-win-cu(?:da-?)?(?<ver>\d+(?:\.\d+)?)-(?<arch>x64|arm64)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<EngineVariant> WindowsVariants(EngineRelease release)
    {
        var runtimes = new Dictionary<(string, string), ReleaseAsset>();
        foreach (var a in release.Assets)
        {
            var m = CudaRuntime.Match(a.Name);
            if (m.Success) runtimes[(MajorOf(m.Groups["ver"].Value), m.Groups["arch"].Value.ToLowerInvariant())] = a;
        }

        var list = new List<EngineVariant>();
        foreach (var asset in release.Assets)
        {
            var m = MainAsset.Match(asset.Name);
            if (!m.Success) continue;
            var b = m.Groups["backend"].Value.ToLowerInvariant();
            var ver = m.Groups["ver"].Success ? m.Groups["ver"].Value : null;
            var archText = m.Groups["arch"].Value.ToLowerInvariant();
            var arch = archText == "arm64" ? CpuArch.Arm64 : CpuArch.X64;
            EngineBackend backend;
            switch (b)
            {
                case "cpu":
                    backend = EngineBackend.Cpu;
                    break;
                case "noavx" or "avx" or "avx2" or "avx512":
                    backend = EngineBackend.Cpu;
                    ver = b;
                    break;
                case "vulkan":
                    backend = EngineBackend.Vulkan;
                    break;
                default:
                    backend = b.StartsWith("cu", StringComparison.Ordinal) ? EngineBackend.Cuda
                        : b.StartsWith("rocm", StringComparison.Ordinal) || b.StartsWith("hip", StringComparison.Ordinal) ? EngineBackend.Rocm
                        : EngineBackend.Custom;
                    break;
            }
            ReleaseAsset? rt = null;
            if (backend == EngineBackend.Cuda && ver != null) runtimes.TryGetValue((MajorOf(ver), archText), out rt);
            list.Add(new EngineVariant(backend, arch, ver, asset, rt));
        }
        return list.OrderBy(v => v.Backend).ThenBy(v => v.Arch).ThenByDescending(v => v.BackendVersion).ToList();
    }

    private static string MajorOf(string ver) => ver.Split('.')[0];

    /// <summary>CPU-вариант по набору инструкций: avx512 требует AVX-512 и т. д.</summary>
    public static bool IsApplicable(EngineVariant v, HardwareInfo hw, bool isWin7)
    {
        if (v.Backend == EngineBackend.Cpu)
        {
            var arch = hw.IsArm64 ? CpuArch.Arm64 : CpuArch.X64;
            if (v.Arch != arch) return false;
            return v.BackendVersion switch
            {
                "avx512" => hw.Avx512,
                "avx2" => hw.Avx2,
                "avx" => hw.Avx,
                _ => true,
            };
        }
        return AssetMatcher.IsApplicable(v, hw, isWin7);
    }

    /// <summary>
    /// Рекомендация: CUDA для NVIDIA с ≥ 4 ГБ видеопамяти, Vulkan для другой видеокарты, иначе лучшая CPU-сборка.
    /// Генерация изображений на CPU идёт минутами — поэтому здесь, в отличие от llama.cpp, рекомендуется GPU.
    /// </summary>
    public static EngineVariant? Recommend(IReadOnlyList<EngineVariant> variants, HardwareInfo hw, bool isWin7)
    {
        var ok = variants.Where(v => IsApplicable(v, hw, isWin7)).ToList();
        var gpu = hw.BestGpu;
        const long fourGb = 4L << 30;
        if (gpu is { Vendor: GpuVendor.Nvidia } && gpu.DedicatedMemory >= fourGb)
        {
            var cuda = ok.FirstOrDefault(v => v.Backend == EngineBackend.Cuda);
            if (cuda != null) return cuda;
        }
        if (gpu != null && gpu.DedicatedMemory >= fourGb && hw.VulkanAvailable)
        {
            var vk = ok.FirstOrDefault(v => v.Backend == EngineBackend.Vulkan);
            if (vk != null) return vk;
        }
        var rank = new[] { "avx512", "avx2", "avx", null, "noavx" };
        return ok.Where(v => v.Backend == EngineBackend.Cpu).OrderBy(v => Array.IndexOf(rank, v.BackendVersion) is var i && i >= 0 ? i : 9).FirstOrDefault();
    }

    /// <summary>Номер сборки из тега "master-1106-532d2c1" (1106) — для сортировки и обновлений.</summary>
    public static int BuildNumber(string tag)
    {
        var m = Regex.Match(tag, @"^master-(\d+)-", RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}

/// <summary>Установленная или подключённая сборка stable-diffusion.cpp (папка с sd-server.exe).</summary>
public sealed class SdEngineInstall
{
    public const string ServerExeName = "sd-server.exe";

    public SdEngineInstall(string directory, EngineManifest manifest, bool isExternal)
    {
        Directory = directory;
        Manifest = manifest;
        IsExternal = isExternal;
    }

    public string Id => Path.GetFullPath(Directory).TrimEnd('\\').ToLowerInvariant();
    public string Directory { get; }
    public EngineManifest Manifest { get; }
    public bool IsExternal { get; }

    public string ServerExe => Path.Combine(Directory, ServerExeName);
    public bool Exists => File.Exists(ServerExe);

    public string DisplayName
    {
        get
        {
            var backend = Manifest.Backend + (Manifest.BackendVersion != null ? " " + Manifest.BackendVersion : string.Empty);
            var tag = string.IsNullOrEmpty(Manifest.Tag) ? "?" : Manifest.Tag;
            return $"stable-diffusion.cpp {tag} · {backend} · {Manifest.Arch.ToString().ToLowerInvariant()}";
        }
    }
}

/// <summary>Реестр сборок stable-diffusion.cpp: папки engine-sd/* с engine.json и внешние папки из настроек.</summary>
public sealed class SdEngineRegistry
{
    private readonly string _root;
    private readonly ImageSettings _settings;

    public SdEngineRegistry(string root, ImageSettings settings)
    {
        _root = root;
        _settings = settings;
    }

    public string Root => _root;

    public IReadOnlyList<SdEngineInstall> List()
    {
        var list = new List<SdEngineInstall>();
        if (System.IO.Directory.Exists(_root))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(_root))
            {
                if (Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal)) continue;
                var manifest = EngineRegistry.ReadManifest(dir);
                if (manifest != null && File.Exists(Path.Combine(dir, SdEngineInstall.ServerExeName)))
                    list.Add(new SdEngineInstall(dir, manifest, false));
            }
        }
        foreach (var ext in _settings.ExternalEngines.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = EngineRegistry.ReadManifest(ext) ?? new EngineManifest { Backend = GuessBackend(ext) };
            list.Add(new SdEngineInstall(ext, manifest, true));
        }
        return list.OrderBy(e => e.IsExternal).ThenByDescending(e => SdAssetMatcher.BuildNumber(e.Manifest.Tag)).ToList();
    }

    public SdEngineInstall? Active
    {
        get
        {
            var all = List();
            return all.FirstOrDefault(e => e.Id == _settings.ActiveEngineId?.ToLowerInvariant() && e.Exists)
                   ?? all.FirstOrDefault(e => e.Exists);
        }
    }

    public void SetActive(SdEngineInstall engine) => _settings.ActiveEngineId = engine.Id;

    /// <summary>Подключить свою папку: принимает папку или путь к sd-server.exe; null — sd-server.exe не найден.</summary>
    public SdEngineInstall? AddExternal(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!File.Exists(Path.Combine(dir, SdEngineInstall.ServerExeName)))
        {
            var nested = System.IO.Directory.Exists(dir)
                ? System.IO.Directory.EnumerateFiles(dir, SdEngineInstall.ServerExeName, SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (nested == null) return null;
            dir = Path.GetDirectoryName(nested)!;
        }
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        if (!_settings.ExternalEngines.Contains(dir, StringComparer.OrdinalIgnoreCase))
            _settings.ExternalEngines.Add(dir);
        return List().FirstOrDefault(e => e.Id == dir.ToLowerInvariant());
    }

    public void Remove(SdEngineInstall engine)
    {
        if (engine.IsExternal)
            _settings.ExternalEngines.RemoveAll(p => string.Equals(Path.GetFullPath(p).TrimEnd('\\'), engine.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        else
            System.IO.Directory.Delete(engine.Directory, recursive: true);
        if (_settings.ActiveEngineId == engine.Id) _settings.ActiveEngineId = null;
    }

    public static EngineBackend GuessBackend(string dir)
    {
        bool Has(string pattern) => System.IO.Directory.Exists(dir) && System.IO.Directory.EnumerateFiles(dir, pattern).Any();
        if (Has("cudart64*.dll") || Has("cublas64*.dll") || Has("ggml-cuda*.dll")) return EngineBackend.Cuda;
        if (Has("ggml-vulkan*.dll") || Has("vulkan*.dll")) return EngineBackend.Vulkan;
        if (Has("amdhip64*.dll") || Has("ggml-hip*.dll")) return EngineBackend.Rocm;
        return EngineBackend.Cpu;
    }
}

/// <summary>
/// Установка stable-diffusion.cpp: zip (+ cudart для CUDA) с проверкой SHA-256 из GitHub API, распаковка,
/// поиск sd-server.exe (если архив вложенный — его папка поднимается в корень), самопроверка --help.
/// </summary>
public sealed class SdEngineInstaller
{
    private readonly string _root;
    private readonly string _cache;
    private readonly DownloadManager _downloads;
    private readonly GitHubReleases _releases;

    public SdEngineInstaller(AppPaths paths, DownloadManager downloads, GitHubReleases releases)
    {
        _root = paths.ImageEngine;
        _cache = paths.Cache;
        _downloads = downloads;
        _releases = releases;
    }

    public async Task<(SdEngineInstall install, SmokeResult smoke)> InstallAsync(EngineRelease release, EngineVariant variant,
        IProgress<InstallStage>? progress, CancellationToken ct)
    {
        var target = Path.Combine(_root, $"{release.Tag}-{variant.Key}");
        var downloadsDir = Path.Combine(_cache, "downloads");
        Directory.CreateDirectory(downloadsDir);
        Directory.CreateDirectory(_root);

        progress?.Report(InstallStage.Downloading);
        DownloadJob Enqueue(ReleaseAsset a) => _downloads.Enqueue(new DownloadJob(_releases.MirrorUrl(a.DownloadUrl), Path.Combine(downloadsDir, a.Name), a.Name)
        {
            ExpectedSize = a.Size > 0 ? a.Size : null,
            ExpectedSha256 = a.Sha256,
            Group = "sd-engine:" + release.Tag,
        });
        var main = Enqueue(variant.Asset);
        var runtime = variant.CudaRuntime != null ? Enqueue(variant.CudaRuntime) : null;
        using (ct.Register(() =>
               {
                   _downloads.Cancel(main);
                   if (runtime != null) _downloads.Cancel(runtime);
               }))
        {
            if (!await main.WhenFinished.ConfigureAwait(false)) throw new IOException(main.Error ?? "Download canceled");
            if (runtime != null && !await runtime.WhenFinished.ConfigureAwait(false)) throw new IOException(runtime.Error ?? "Download canceled");
        }
        ct.ThrowIfCancellationRequested();

        progress?.Report(InstallStage.Extracting);
        var temp = Path.Combine(_root, ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            await Task.Run(() =>
            {
                EngineInstaller.ExtractFlattened(main.TargetPath, temp);
                LiftServerFolder(temp);
                if (runtime != null) EngineInstaller.ExtractFlattened(runtime.TargetPath, temp);
            }, ct).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(temp, SdEngineInstall.ServerExeName)))
                throw new InvalidDataException("sd-server.exe not found in " + variant.Asset.Name);

            progress?.Report(InstallStage.Testing);
            var smoke = await SmokeTestAsync(Path.Combine(temp, SdEngineInstall.ServerExeName)).ConfigureAwait(false);
            EngineRegistry.WriteManifest(temp, new EngineManifest
            {
                Tag = release.Tag,
                Backend = variant.Backend,
                BackendVersion = variant.BackendVersion,
                Arch = variant.Arch,
                VersionText = smoke.VersionText,
                Build = SdAssetMatcher.BuildNumber(release.Tag),
                SourceRepo = SdAssetMatcher.Repo,
            });
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
            Log.Info("ImageEngine", "Installed " + Path.GetFileName(target), new { smoke.Health, smoke.ExitCode });
            TryDelete(main.TargetPath);
            if (runtime != null) TryDelete(runtime.TargetPath);
            progress?.Report(InstallStage.Done);
            return (new SdEngineInstall(target, EngineRegistry.ReadManifest(target)!, false), smoke);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); }
                catch (IOException ex) { Log.Warn("ImageEngine", "Cannot remove temp folder " + temp, ex); }
            }
        }
    }

    /// <summary>Архив мог содержать bin/Release/sd-server.exe — переносим содержимое этой папки в корень.</summary>
    internal static void LiftServerFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, SdEngineInstall.ServerExeName))) return;
        var nested = Directory.EnumerateFiles(dir, SdEngineInstall.ServerExeName, SearchOption.AllDirectories).FirstOrDefault();
        if (nested == null) return;
        var from = Path.GetDirectoryName(nested)!;
        foreach (var f in Directory.EnumerateFiles(from))
        {
            var to = Path.Combine(dir, Path.GetFileName(f));
            if (!File.Exists(to)) File.Move(f, to);
        }
    }

    /// <summary>
    /// Запускается ли sd-server на этой системе: --help должен завершиться без ошибок загрузки DLL.
    /// У sd-server нет --version, поэтому «версия» — первая строка справки, если похожа на неё.
    /// </summary>
    public static async Task<SmokeResult> SmokeTestAsync(string serverExe, TimeSpan? timeout = null)
    {
        if (!File.Exists(serverExe)) return new SmokeResult(EngineHealth.MissingFile, null, null, null, serverExe);
        var help = await RunAsync(serverExe, "--help", timeout ?? TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (help.timedOut) return new SmokeResult(EngineHealth.Timeout, null, null, null, help.output);
        var code = help.exitCode;
        var looksLikeHelp = help.output.IndexOf("--listen-port", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            help.output.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0;
        var health = code switch
        {
            unchecked((int)0xC0000135) or unchecked((int)0xC000007B) => EngineHealth.MissingDll,
            unchecked((int)0xC0000139) => EngineHealth.OsTooOld,
            unchecked((int)0xC000001D) => EngineHealth.IllegalInstruction,
            _ when looksLikeHelp => EngineHealth.Ok,
            0 => EngineHealth.Ok,
            _ => EngineHealth.Crashed,
        };
        var version = help.output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("stable-diffusion.cpp", StringComparison.OrdinalIgnoreCase) || l.StartsWith("version", StringComparison.OrdinalIgnoreCase));
        return new SmokeResult(health, code, version, null, help.output);
    }

    internal static async Task<(int? exitCode, string output, bool timedOut)> RunAsync(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        var old = NativeErrorMode.SetErrorMode(0x0001 | 0x0002 | 0x8000);
        try
        {
            try { p.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { return (ex.NativeErrorCode, ex.Message, false); }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var exited = await Task.Run(() => p.WaitForExit((int)timeout.TotalMilliseconds)).ConfigureAwait(false);
            if (!exited)
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return (null, output.ToString(), true);
            }
            p.WaitForExit();
            lock (output) return (p.ExitCode, output.ToString(), false);
        }
        finally
        {
            NativeErrorMode.SetErrorMode(old);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static class NativeErrorMode
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern uint SetErrorMode(uint mode);
    }
}

/// <summary>Справка sd-server: опции с отступом в два пробела — приводим к формату, который понимает <see cref="FlagCatalog"/>.</summary>
public static class SdHelp
{
    public static FlagCatalog? Parse(string? help)
    {
        if (string.IsNullOrWhiteSpace(help)) return null;
        var sb = new StringBuilder();
        foreach (var raw in help!.Replace("\r", string.Empty).Split('\n'))
        {
            var t = raw.TrimStart();
            // строка опции: «  -m, --model <string>   описание»; продолжения описаний идут с большим отступом
            if (t.StartsWith("-", StringComparison.Ordinal) && raw.Length - t.Length <= 8) sb.AppendLine(t);
            else if (raw.Length > 0 && !char.IsWhiteSpace(raw[0])) sb.AppendLine("----- " + raw.Trim().TrimEnd(':') + " -----"); // «Context Options:» — новая секция
            else sb.AppendLine(raw);
        }
        var catalog = FlagCatalog.Parse(sb.ToString());
        // Не удалось разобрать справку — не отбрасываем флаги вслепую
        return catalog.Flags.Count >= 8 ? catalog : null;
    }
}
