using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Engine;

/// <summary>Описание установленного движка (engine.json в его папке).</summary>
public sealed class EngineManifest
{
    public string Tag { get; set; } = string.Empty;
    public EngineBackend Backend { get; set; } = EngineBackend.Cpu;
    public string? BackendVersion { get; set; }
    public CpuArch Arch { get; set; } = CpuArch.X64;
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Строка из --version: "version: 0.9.0-dev (build 12176, commit 8820b088d)".</summary>
    public string? VersionText { get; set; }
    public int? Build { get; set; }

    /// <summary>Репозиторий GitHub, откуда скачана сборка (null — официальный ggml-org/llama.cpp). Обновления ищутся там же.</summary>
    public string? SourceRepo { get; set; }
}

/// <summary>Откуда скачивать сборки llama.cpp.</summary>
public enum EngineSource
{
    /// <summary>ggml-org/llama.cpp — официальные сборки (Windows 10+).</summary>
    Official,

    /// <summary>Win7-сборки из репозитория программы (теги llama-*, workflow llama-win7.yml).</summary>
    AppWin7,

    /// <summary>Свой репозиторий GitHub owner/repo (например, форк с поддержкой Win7). Сторонний код — на ответственности пользователя.</summary>
    Custom,
}

public static class EngineSources
{
    private static readonly System.Text.RegularExpressions.Regex RepoPattern = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");

    /// <summary>owner/repo из строки: принимает и ссылку https://github.com/owner/repo.</summary>
    public static string? NormalizeRepo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text!.Trim().TrimEnd('/');
        const string prefix = "github.com/";
        var i = t.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) t = t.Substring(i + prefix.Length);
        if (t.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
        var parts = t.Split('/');
        if (parts.Length >= 2) t = parts[0] + "/" + parts[1];
        return RepoPattern.IsMatch(t) ? t : null;
    }

    /// <summary>Репозиторий и нужен ли фильтр тегов llama-* (в репозитории программы рядом лежат её собственные релизы).</summary>
    public static (string? repo, bool onlyLlamaTags) Resolve(EngineSource source, string? customRepo, string appRepo) => source switch
    {
        EngineSource.AppWin7 => (appRepo, true),
        EngineSource.Custom => (NormalizeRepo(customRepo), false),
        _ => (null, false),
    };
}

/// <summary>Установленный или подключённый движок.</summary>
public sealed class EngineInstall
{
    public EngineInstall(string directory, EngineManifest manifest, bool isExternal)
    {
        Directory = directory;
        Manifest = manifest;
        IsExternal = isExternal;
    }

    /// <summary>Идентификатор = полный путь к папке (в нижнем регистре).</summary>
    public string Id => Path.GetFullPath(Directory).TrimEnd('\\').ToLowerInvariant();
    public string Directory { get; }
    public EngineManifest Manifest { get; }
    public bool IsExternal { get; }

    public string ServerExe => Path.Combine(Directory, "llama-server.exe");
    public string BenchExe => Path.Combine(Directory, "llama-bench.exe");
    public bool Exists => File.Exists(ServerExe);

    public string DisplayName
    {
        get
        {
            var backend = Manifest.Backend + (Manifest.BackendVersion != null ? " " + Manifest.BackendVersion : string.Empty);
            var tag = string.IsNullOrEmpty(Manifest.Tag) ? (Manifest.Build is { } b ? "b" + b : "?") : Manifest.Tag;
            return $"llama.cpp {tag} · {backend} · {Manifest.Arch.ToString().ToLowerInvariant()}";
        }
    }
}

public sealed class EngineSettings
{
    /// <summary>Id активного движка (путь к папке).</summary>
    public string? ActiveEngineId { get; set; }

    /// <summary>Подключённые пользователем папки со своими сборками llama.cpp.</summary>
    public List<string> ExternalEngines { get; set; } = new();

    /// <summary>Устарело: заменено <see cref="ListenAddress"/> (true переносится в "0.0.0.0").</summary>
    public bool AllowLan { get; set; }

    /// <summary>
    /// Адрес, который слушает публичный API: "127.0.0.1" — только этот ПК (плюс [::1] через пересылку),
    /// "0.0.0.0" — все интерфейсы (локальная сеть, Docker, WSL), либо IPv4 конкретного адаптера.
    /// Без публичного API сервер всегда слушает 127.0.0.1.
    /// </summary>
    public string ListenAddress { get; set; } = Loopback;

    public const string Loopback = "127.0.0.1";
    public const string AnyAddress = "0.0.0.0";

    /// <summary>0 — выбрать свободный порт автоматически.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Публичный OpenAI-совместимый API для других приложений: постоянный порт и секрет вместо случайных,
    /// сервер запускается вместе с программой.
    /// </summary>
    public bool ExposeApi { get; set; }

    /// <summary>Секрет (Bearer-ключ) публичного API, зашифрован через Secrets.</summary>
    public string ApiKeyProtected { get; set; } = string.Empty;

    /// <summary>Проверять при запуске, нет ли новой сборки llama.cpp для активного движка.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Откуда скачивать сборки в разделе «Движок».</summary>
    public EngineSource BuildSource { get; set; } = EngineSource.Official;

    /// <summary>Свой репозиторий сборок (owner/repo) для <see cref="EngineSource.Custom"/>.</summary>
    public string CustomBuildRepo { get; set; } = string.Empty;

    /// <summary>Версия, которую пользователь решил пропустить (предложим только более новую).</summary>
    public string? IgnoredUpdateTag { get; set; }
}

/// <summary>Реестр движков: папки engine/* с engine.json + внешние папки из настроек.</summary>
public sealed class EngineRegistry
{
    public const string ManifestFile = "engine.json";
    private readonly AppPaths _paths;
    private readonly EngineSettings _settings;

    public EngineRegistry(AppPaths paths, EngineSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public IReadOnlyList<EngineInstall> List()
    {
        var list = new List<EngineInstall>();
        if (Directory.Exists(_paths.Engine))
        {
            foreach (var dir in Directory.EnumerateDirectories(_paths.Engine))
            {
                if (Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal)) continue;
                var manifest = ReadManifest(dir);
                if (manifest != null && File.Exists(Path.Combine(dir, "llama-server.exe")))
                    list.Add(new EngineInstall(dir, manifest, false));
            }
        }

        foreach (var ext in _settings.ExternalEngines.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = ReadManifest(ext) ?? new EngineManifest { Backend = GuessBackend(ext) };
            list.Add(new EngineInstall(ext, manifest, true));
        }
        return list.OrderBy(e => e.IsExternal).ThenByDescending(e => e.Manifest.Build ?? 0).ToList();
    }

    public EngineInstall? Active
    {
        get
        {
            var all = List();
            return all.FirstOrDefault(e => e.Id == _settings.ActiveEngineId?.ToLowerInvariant() && e.Exists)
                   ?? all.FirstOrDefault(e => e.Exists);
        }
    }

    public void SetActive(EngineInstall engine) => _settings.ActiveEngineId = engine.Id;

    /// <summary>
    /// Подключить свою папку llama.cpp. Принимает папку или путь к llama-server.exe.
    /// Возвращает null, если llama-server.exe там нет.
    /// </summary>
    public EngineInstall? AddExternal(string path)
    {
        var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!File.Exists(Path.Combine(dir, "llama-server.exe")))
        {
            // Частый случай: выбрали корень сборки, а exe лежит в build/bin/Release
            var nested = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (nested == null) return null;
            dir = Path.GetDirectoryName(nested)!;
        }
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        if (!_settings.ExternalEngines.Contains(dir, StringComparer.OrdinalIgnoreCase))
            _settings.ExternalEngines.Add(dir);
        return List().FirstOrDefault(e => e.Id == dir.ToLowerInvariant());
    }

    /// <summary>Удалить: скачанный — вместе с папкой; внешний — только из списка.</summary>
    public void Remove(EngineInstall engine)
    {
        if (engine.IsExternal)
        {
            _settings.ExternalEngines.RemoveAll(p => string.Equals(Path.GetFullPath(p).TrimEnd('\\'), engine.Directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Directory.Delete(engine.Directory, recursive: true);
        }
        if (_settings.ActiveEngineId == engine.Id) _settings.ActiveEngineId = null;
    }

    public static EngineManifest? ReadManifest(string dir)
    {
        try
        {
            return JsonFile.Read<EngineManifest>(Path.Combine(dir, ManifestFile));
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Engine", "Bad engine manifest in " + dir, ex);
            return null;
        }
    }

    public static void WriteManifest(string dir, EngineManifest manifest) => JsonFile.WriteAtomic(Path.Combine(dir, ManifestFile), manifest);

    /// <summary>Бэкенд внешней сборки по набору DLL.</summary>
    public static EngineBackend GuessBackend(string dir)
    {
        bool Has(string pattern) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
        if (Has("ggml-cuda*.dll")) return EngineBackend.Cuda;
        if (Has("ggml-vulkan*.dll")) return EngineBackend.Vulkan;
        if (Has("ggml-hip*.dll")) return EngineBackend.Rocm;
        if (Has("ggml-sycl*.dll")) return EngineBackend.Sycl;
        if (Has("ggml-openvino*.dll")) return EngineBackend.OpenVino;
        if (Has("ggml-opencl*.dll")) return EngineBackend.OpenClAdreno;
        return EngineBackend.Cpu;
    }
}
