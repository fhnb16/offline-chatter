using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Images;

/// <summary>
/// Модель для генерации изображений: набор файлов по ролям (основная модель, VAE, кодировщики) и семейство.
/// Файлы не копируются — хранятся пути (config/image-models.json).
/// </summary>
public sealed class ImageModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Семейство (<see cref="ImageFamily"/> строкой — переживает переименования значений).</summary>
    public string Family { get; set; } = nameof(ImageFamily.Custom);

    public Dictionary<ImageComponent, string> Files { get; set; } = new();
    public string? HfRepo { get; set; }
    public string? KitId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public ImagePreset Preset => ImagePresets.Get(Family);

    /// <summary>Путь основного файла: чекпойнт или диффузионная модель.</summary>
    [JsonIgnore]
    public string? MainFile => Files.TryGetValue(ImageComponent.Checkpoint, out var c) ? c
                              : Files.TryGetValue(ImageComponent.DiffusionModel, out var d) ? d : null;

    /// <summary>Каких обязательных файлов семейства нет (не назначены или пропали с диска).</summary>
    public IReadOnlyList<ImageComponent> Missing()
    {
        var list = Preset.Required.Where(r => !Files.TryGetValue(r, out var p) || !File.Exists(p)).ToList();
        // Свой набор: нужен хотя бы один основной файл
        if (Preset.Family == ImageFamily.Custom && MainFile == null) list.Add(ImageComponent.DiffusionModel);
        list.AddRange(Files.Where(f => !File.Exists(f.Value) && !list.Contains(f.Key)).Select(f => f.Key));
        return list;
    }

    [JsonIgnore] public bool IsReady => MainFile != null && Missing().Count == 0;

    [JsonIgnore]
    public long SizeBytes => Files.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Sum(f => new FileInfo(f).Length);
}

/// <summary>Список моделей изображений пользователя.</summary>
public sealed class ImageModelRegistry
{
    private readonly string _file;
    private readonly object _gate = new();
    private List<ImageModel> _models = new();

    public ImageModelRegistry(string file)
    {
        _file = file;
        try
        {
            _models = JsonFile.Read<List<ImageModel>>(_file) ?? new List<ImageModel>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Images", "image-models.json unreadable", ex);
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ImageModel> Models
    {
        get { lock (_gate) return _models.ToList(); }
    }

    public ImageModel? Find(string? id) => id == null ? null : Models.FirstOrDefault(m => m.Id == id);

    public void Save()
    {
        lock (_gate) JsonFile.WriteAtomic(_file, _models);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Добавить или обновить: модель с тем же основным файлом считается той же.</summary>
    public ImageModel AddOrUpdate(ImageModel model)
    {
        lock (_gate)
        {
            var main = model.MainFile;
            var existing = _models.FirstOrDefault(m => m.Id == model.Id) ??
                           (main == null ? null : _models.FirstOrDefault(m => string.Equals(m.MainFile, main, StringComparison.OrdinalIgnoreCase)));
            if (existing != null && !ReferenceEquals(existing, model))
            {
                foreach (var f in model.Files) existing.Files[f.Key] = f.Value;
                existing.Family = model.Family;
                existing.HfRepo ??= model.HfRepo;
                existing.KitId ??= model.KitId;
                model = existing;
            }
            else if (existing == null)
            {
                _models.Add(model);
            }
        }
        Save();
        return model;
    }

    /// <summary>Убрать из списка; <paramref name="deleteFiles"/> — удалить файлы, которыми не пользуются другие модели.</summary>
    public void Remove(ImageModel model, bool deleteFiles)
    {
        List<string> shared;
        lock (_gate)
        {
            _models.RemoveAll(m => m.Id == model.Id);
            shared = _models.SelectMany(m => m.Files.Values).ToList();
        }
        if (deleteFiles)
        {
            foreach (var f in model.Files.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (shared.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Images", "Cannot delete " + f, ex); }
            }
        }
        Save();
    }

    /// <summary>
    /// Модель из набора файлов: семейство по именам (или заданное), роли — по именам файлов.
    /// Второй файл той же роли (например, два кванта) не перезаписывает первый.
    /// </summary>
    public static ImageModel Describe(IEnumerable<string> paths, ImageFamily? family = null, string? hfRepo = null)
    {
        var list = paths.Select(Path.GetFullPath).ToList();
        var fam = family ?? list.Select(p => ImagePresets.Detect(Path.GetFileName(p))).FirstOrDefault(f => f != null)
                  ?? ImagePresets.Detect(hfRepo) ?? ImageFamily.Custom;
        var model = new ImageModel { Family = fam.ToString(), HfRepo = hfRepo };
        foreach (var p in list)
        {
            var role = ImagePresets.GuessComponent(p, fam);
            if (!model.Files.ContainsKey(role)) model.Files[role] = p;
        }
        var main = model.MainFile ?? list.FirstOrDefault();
        model.DisplayName = main != null ? Path.GetFileNameWithoutExtension(main) : "image model";
        return model;
    }
}
