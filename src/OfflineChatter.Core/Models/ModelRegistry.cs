using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Models;

public enum ModelSource
{
    HuggingFace,
    LocalFile,
    Scanned,
}

/// <summary>Запись о модели в config/models.json. Файл может лежать где угодно — он не копируется.</summary>
public sealed class LocalModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Основной файл (для шардированных — первый шард).</summary>
    public string Path { get; set; } = string.Empty;
    public string? MmprojPath { get; set; }
    public ModelSource Source { get; set; }
    public string? HfRepo { get; set; }
    public string? Quant { get; set; }
    public long SizeBytes { get; set; }
    public long? Parameters { get; set; }
    public string? Architecture { get; set; }
    public int? ContextLength { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Профиль запуска по умолчанию для этой модели (этап 5).</summary>
    public string? ProfileId { get; set; }

    [Newtonsoft.Json.JsonIgnore] public bool Exists => File.Exists(Path);
    [Newtonsoft.Json.JsonIgnore] public bool HasVision => !string.IsNullOrEmpty(MmprojPath) && File.Exists(MmprojPath);
}

public sealed class ModelsSettings
{
    public string? ActiveModelId { get; set; }

    /// <summary>Куда сохранять скачанные модели (пусто — папка models рядом с программой).</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>Дополнительные папки для поиска GGUF.</summary>
    public List<string> ScanFolders { get; set; } = new();

    /// <summary>Токен Hugging Face (для gated-моделей), зашифрован через Secrets.</summary>
    public string HfTokenProtected { get; set; } = string.Empty;

    /// <summary>Профиль запуска по умолчанию (если у модели свой не задан).</summary>
    public string DefaultProfileId { get; set; } = "balanced";
}

/// <summary>Список моделей пользователя (config/models.json).</summary>
public sealed class ModelRegistry
{
    private readonly string _file;
    private readonly object _gate = new();
    private List<LocalModel> _models = new();

    public ModelRegistry(string file)
    {
        _file = file;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<LocalModel> Models
    {
        get { lock (_gate) return _models.ToList(); }
    }

    public LocalModel? Find(string? id) => id == null ? null : Models.FirstOrDefault(m => m.Id == id);

    public LocalModel? FindByPath(string path) =>
        Models.FirstOrDefault(m => string.Equals(System.IO.Path.GetFullPath(m.Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        try
        {
            _models = JsonFile.Read<List<LocalModel>>(_file) ?? new List<LocalModel>();
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Error("Models", "models.json unreadable", ex);
            _models = new List<LocalModel>();
        }
    }

    public void Save()
    {
        lock (_gate) JsonFile.WriteAtomic(_file, _models);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public LocalModel AddOrUpdate(LocalModel model)
    {
        lock (_gate)
        {
            var existing = _models.FirstOrDefault(m => string.Equals(m.Path, model.Path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.MmprojPath = model.MmprojPath ?? existing.MmprojPath;
                existing.SizeBytes = model.SizeBytes;
                existing.HfRepo ??= model.HfRepo;
                existing.Quant ??= model.Quant;
                existing.Parameters ??= model.Parameters;
                existing.Architecture ??= model.Architecture;
                existing.ContextLength ??= model.ContextLength;
                model = existing;
            }
            else
            {
                _models.Add(model);
            }
        }
        Save();
        return model;
    }

    public void Remove(LocalModel model, bool deleteFiles)
    {
        lock (_gate) _models.RemoveAll(m => m.Id == model.Id);
        if (deleteFiles)
        {
            foreach (var f in ShardFiles(model.Path).Concat(model.MmprojPath != null ? new[] { model.MmprojPath } : Array.Empty<string>()))
            {
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Models", "Cannot delete " + f, ex); }
            }
        }
        Save();
    }

    /// <summary>
    /// Описание модели по локальному файлу: метаданные GGUF, квант, размер всех шардов, mmproj рядом.
    /// Файл не копируется — модель может лежать на любом диске.
    /// </summary>
    public static LocalModel Describe(string path, ModelSource source, string? hfRepo = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var shards = ShardFiles(path).ToList();
        if (shards.Count > 0) path = shards[0];
        GgufInfo? info = null;
        try { info = GgufReader.ReadFile(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Warn("Models", "Cannot read GGUF header " + path, ex);
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var shard = QuantCatalog.Shard(path);
        if (shard != null) name = name.Substring(0, name.Length - "-00001-of-00003".Length);
        return new LocalModel
        {
            // Имя файла информативнее general.name (там часто «model» или внутреннее имя конвертации)
            DisplayName = name,
            Path = path,
            MmprojPath = FindMmproj(path),
            Source = source,
            HfRepo = hfRepo,
            Quant = QuantCatalog.FromFileName(path)?.Name,
            SizeBytes = shards.Sum(f => new FileInfo(f).Length),
            Parameters = (long?)QuantCatalog.ParamsFromLabel(info?.SizeLabel),
            Architecture = info?.Architecture,
            ContextLength = info?.ContextLength,
        };
    }

    /// <summary>Все шарды модели (или сам файл).</summary>
    public static IEnumerable<string> ShardFiles(string path)
    {
        var shard = QuantCatalog.Shard(path);
        if (shard == null)
        {
            if (File.Exists(path)) yield return path;
            yield break;
        }
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var baseName = System.IO.Path.GetFileName(path);
        var prefix = baseName.Substring(0, baseName.Length - "00001-of-00003.gguf".Length);
        for (var i = 1; i <= shard.Value.count; i++)
        {
            var f = System.IO.Path.Combine(dir, $"{prefix}{i:00000}-of-{shard.Value.count:00000}.gguf");
            if (File.Exists(f)) yield return f;
        }
    }

    /// <summary>mmproj рядом с моделью: в той же папке, с предпочтением F16.</summary>
    public static string? FindMmproj(string modelPath)
    {
        var dir = System.IO.Path.GetDirectoryName(modelPath);
        if (dir == null || !Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.gguf")
            .Where(f => QuantCatalog.IsMmproj(f) && !string.Equals(f, modelPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(QuantCatalog.MmprojPreference)
            .FirstOrDefault();
    }
}

/// <summary>Поиск уже скачанных GGUF: папка models, кэш Hugging Face, LM Studio, пользовательские папки.</summary>
public static class LocalScanner
{
    public static IEnumerable<string> DefaultRoots(string appModels)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return appModels;
        yield return Environment.GetEnvironmentVariable("HF_HUB_CACHE") ?? System.IO.Path.Combine(home, ".cache", "huggingface", "hub");
        yield return System.IO.Path.Combine(home, ".lmstudio", "models");
        yield return System.IO.Path.Combine(home, ".cache", "lm-studio", "models");
        var llamaCache = Environment.GetEnvironmentVariable("LLAMA_CACHE") ??
                         System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "llama.cpp");
        yield return llamaCache;
    }

    /// <summary>Модели (без mmproj; шарды — только первый), не глубже 6 уровней, с защитой от недоступных папок.</summary>
    public static List<string> Scan(IEnumerable<string> roots, int maxDepth = 6)
    {
        var found = new List<string>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Walk(root, 0, maxDepth, found);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(string dir, int depth, int maxDepth, List<string> found)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.gguf"))
            {
                if (QuantCatalog.IsMmproj(f)) continue;
                var shard = QuantCatalog.Shard(f);
                if (shard != null && shard.Value.index != 1) continue;
                // Кэш HF: файлы в snapshots — ссылки на blobs; берём путь из snapshots
                found.Add(f);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = System.IO.Path.GetFileName(d);
                if (name is "blobs" or ".git" or "node_modules") continue;
                Walk(d, depth + 1, maxDepth, found);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }
}
