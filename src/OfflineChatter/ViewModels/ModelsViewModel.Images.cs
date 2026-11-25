using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Images;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Файл репозитория модели изображений: скачивать ли и в какой роли.</summary>
public sealed partial class ImageFileOption : ObservableObject
{
    public ImageFileOption(HfFile file, ImageComponent role, bool selected)
    {
        File = file;
        _role = role;
        _isSelected = selected;
    }

    public HfFile File { get; }
    public string Name => Path.GetFileName(File.Path);
    public string Folder => Path.GetDirectoryName(File.Path)?.Replace('\\', '/') ?? string.Empty;
    public string Size => Units.FormatBytes(File.Size);
    public string? Quant => QuantCatalog.FromFileName(Name)?.Name;

    [ObservableProperty] private ImageComponent _role;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Готовый набор в списке: размер, хватит ли видеопамяти, ход загрузки, установлен ли.</summary>
public sealed partial class ImageKitItem : ObservableObject
{
    public ImageKitItem(ImageKit kit, string fitText, bool fits)
    {
        Kit = kit;
        FitText = fitText;
        Fits = fits;
    }

    public ImageKit Kit { get; }
    public string Title => Kit.Title;
    public string Description => L.Get("ImgKit_" + Kit.Id.Replace('-', '_'));
    public string Family => L.Get("Enum_ImageFamily_" + Kit.Family);
    public string Size => Units.FormatBytes(Kit.TotalSize);
    public string Files => string.Join(" + ", Kit.Files.Select(f => L.Get("Enum_ImageComponent_" + f.Role)));
    public string FitText { get; }
    public bool Fits { get; }
    public bool Recommended => Kit.Recommended;
    public bool SupportsEdit => Kit.Preset.SupportsRefImages;

    public string Group => "kit:" + Kit.Id;

    [ObservableProperty] private DownloadSummary? _progress;
    [ObservableProperty] private bool _isInstalled;

    public bool IsDownloading => Progress is { Active: true };
    partial void OnProgressChanged(DownloadSummary? value) => OnPropertyChanged(nameof(IsDownloading));
}

public sealed partial class ModelsViewModel
{
    // ---------------- Найденная модель изображений ----------------

    public ObservableCollection<ImageFileOption> ImageFiles { get; } = new();

    public IReadOnlyList<ImageComponent> ImageRoles { get; } = Enum.GetValues(typeof(ImageComponent)).Cast<ImageComponent>().ToList();
    public IReadOnlyList<ImageFamily> ImageFamilies { get; } = ImagePresets.All.Select(p => p.Family).ToList();

    /// <summary>Выбранная модель — генератор изображений (по тегу или по выбранной задаче поиска).</summary>
    public bool IsImageResult => SelectedResult != null &&
                                 (HfTasks.RuntimeFor(SelectedResult.PipelineTag) == HfRuntime.StableDiffusion ||
                                  (IsImageTask && HfTasks.RuntimeFor(SelectedResult.PipelineTag) != HfRuntime.Llama));

    /// <summary>Для задачи модели нет встроенного движка (TTS, видео, эмбеддинги).</summary>
    public bool IsUnsupportedResult => SelectedResult != null && !IsImageResult && HfTasks.RuntimeFor(SelectedResult.PipelineTag) == HfRuntime.None;

    public bool IsChatResult => SelectedResult != null && !IsImageResult && !IsUnsupportedResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageMissingText))]
    private ImageFamily _imageFamily = ImageFamily.Custom;

    private void OnImageResultChanged()
    {
        OnPropertyChanged(nameof(IsImageResult));
        OnPropertyChanged(nameof(IsUnsupportedResult));
        OnPropertyChanged(nameof(IsChatResult));
        OnPropertyChanged(nameof(ImageMissingText));
        DownloadImageCommand.NotifyCanExecuteChanged();
    }

    private void FillImageFiles(HfModelSummary model, IReadOnlyList<HfFile> files)
    {
        ImageFiles.Clear();
        var family = files.Select(f => ImagePresets.Detect(Path.GetFileName(f.Path))).FirstOrDefault(f => f != null)
                     ?? ImagePresets.Detect(model.Id) ?? ImageFamily.Custom;
        ImageFamily = family;

        // По умолчанию отмечаем один файл основной модели (рекомендованный квант или самый маленький safetensors)
        // и все сопутствующие файлы, которые лежат в этом же репозитории
        var options = files.Select(f => new ImageFileOption(f, ImagePresets.GuessComponent(f.Path, family), false)).ToList();
        var mains = options.Where(o => o.Role is ImageComponent.DiffusionModel or ImageComponent.Checkpoint).ToList();
        var main = mains.FirstOrDefault(o => QuantCatalog.FromFileName(o.Name)?.IsRecommendedDefault == true)
                   ?? mains.Where(o => o.Quant != null).OrderBy(o => Math.Abs((QuantCatalog.FromFileName(o.Name)?.QualityRank ?? 99) - 16)).FirstOrDefault()
                   ?? mains.OrderBy(o => o.File.Size).FirstOrDefault();
        if (main != null) main.IsSelected = true;
        foreach (var group in options.Where(o => o.Role is not (ImageComponent.DiffusionModel or ImageComponent.Checkpoint)).GroupBy(o => o.Role))
            group.OrderBy(o => o.File.Size).First().IsSelected = true;
        foreach (var o in options)
        {
            o.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ImageMissingText));
                OnPropertyChanged(nameof(ImageSelectedSize));
                DownloadImageCommand.NotifyCanExecuteChanged();
            };
            ImageFiles.Add(o);
        }
        OnImageResultChanged();
        OnPropertyChanged(nameof(ImageSelectedSize));
    }

    public string ImageSelectedSize => Units.FormatBytes(ImageFiles.Where(f => f.IsSelected).Sum(f => f.File.Size));

    /// <summary>Каких обязательных для семейства файлов нет среди отмеченных — подскажем взять их из набора.</summary>
    public string? ImageMissingText
    {
        get
        {
            if (!IsImageResult || ImageFiles.Count == 0) return null;
            var have = ImageFiles.Where(f => f.IsSelected).Select(f => f.Role).ToHashSet();
            var missing = ImagePresets.Get(ImageFamily).Required.Where(r => !have.Contains(r)).ToList();
            if (missing.Count == 0) return null;
            return L.F("Models_ImageMissing", string.Join(", ", missing.Select(c => L.Get("Enum_ImageComponent_" + c))));
        }
    }

    private bool CanDownloadImage() => IsImageResult && ImageFiles.Any(f => f.IsSelected) && !IsSelectedDownloading;

    /// <summary>Скачать отмеченные файлы и добавить модель изображений (роли — как выбраны в списке).</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadImage))]
    private async Task DownloadImageAsync()
    {
        if (SelectedResult == null) return;
        var repo = SelectedResult.Id;
        var family = ImageFamily;
        var picked = ImageFiles.Where(f => f.IsSelected).ToList();
        var folder = Path.Combine(_s.ModelsFolder, repo.Replace('/', Path.DirectorySeparatorChar));
        var headers = _s.Hf.AuthHeaders();
        var group = "image-model:" + repo;
        var jobs = picked.Select(o => (o.Role, job: EnqueueHf(repo, o.File.Path, o.File.Size, o.File.Sha256, folder, group, headers))).ToList();
        RefreshSelectedDownload();
        var ok = await Task.WhenAll(jobs.Select(j => j.job?.WhenFinished ?? Task.FromResult(true)));
        if (!ok.All(x => x)) return;

        var model = new ImageModel { Family = family.ToString(), HfRepo = repo };
        foreach (var (role, job) in jobs)
        {
            var path = Path.Combine(folder, Path.GetFileName(picked.First(p => p.Role == role).File.Path));
            if (!model.Files.ContainsKey(role)) model.Files[role] = job?.TargetPath ?? path;
        }
        model.DisplayName = Path.GetFileNameWithoutExtension(model.MainFile ?? repo);
        RegisterImageModel(model);
    }

    /// <summary>Поставить файл в очередь; null — уже скачан (тот же размер).</summary>
    private DownloadJob? EnqueueHf(string repo, string path, long size, string? sha, string folder, string group, IDictionary<string, string> headers)
    {
        var target = Path.Combine(folder, Path.GetFileName(path));
        if (System.IO.File.Exists(target) && (size <= 0 || new FileInfo(target).Length == size)) return null;
        var job = new DownloadJob(_s.Hf.FileUrl(repo, path), target, Path.GetFileName(path))
        {
            ExpectedSize = size > 0 ? size : null,
            ExpectedSha256 = sha,
            Group = group,
        };
        foreach (var h in headers) job.Headers[h.Key] = h.Value;
        return _s.Downloads.Enqueue(job);
    }

    private void RegisterImageModel(ImageModel model)
    {
        model = _s.ImageModels.AddOrUpdate(model);
        var images = _s.Settings.Current.Images;
        if (images.ActiveModelId == null || _s.ImageModels.Find(images.ActiveModelId)?.IsReady != true)
        {
            images.ActiveModelId = model.Id;
            _s.NotifyImageSetupChanged();
        }
        Log.Info("Images", "Image model added", new { model.DisplayName, model.Family, files = model.Files.Count });
    }

    // ---------------- Готовые наборы ----------------

    public ObservableCollection<ImageKitItem> Kits { get; } = new();

    private void InitImages()
    {
        RefreshImageModels();
        _ = BuildKitsAsync();
    }

    private async Task BuildKitsAsync()
    {
        var hw = await _s.Engine.GetHardwareAsync();
        var vram = hw.BestGpu?.DedicatedMemory ?? 0;
        Kits.Clear();
        foreach (var kit in ImageKits.All)
        {
            var fits = vram >= kit.MinVram;
            var text = vram > 0
                ? L.F(fits ? "ImgKit_FitsGpu" : "ImgKit_Offload", Units.FormatBytes(kit.MinVram))
                : L.Get("ImgKit_CpuOnly");
            // Веса, выгруженные из видеопамяти, живут в ОЗУ: при нехватке начинается подкачка и всё встаёт
            if (kit.MinRam > 0 && hw.TotalRam > 0 && hw.TotalRam < kit.MinRam * 0.95)
                text += " · " + L.F("ImgKit_NeedsRam", Units.FormatBytes(kit.MinRam));
            Kits.Add(new ImageKitItem(kit, text, fits));
        }
        RefreshKitDownloads();
    }

    private void RefreshKitDownloads()
    {
        var installed = _s.ImageModels.Models.Where(m => m.KitId != null && m.IsReady).Select(m => m.KitId).ToHashSet();
        foreach (var k in Kits)
        {
            k.Progress = Downloads.Summarize(k.Group);
            k.IsInstalled = installed.Contains(k.Kit.Id);
        }
    }

    /// <summary>Скачать все файлы набора (уже скачанные пропускаются) и добавить готовую модель.</summary>
    [RelayCommand]
    private async Task DownloadKitAsync(ImageKitItem? item)
    {
        if (item == null || item.IsDownloading) return;
        var kit = item.Kit;
        var headers = _s.Hf.AuthHeaders();
        var jobs = kit.Files.Select(f =>
        {
            var folder = Path.Combine(_s.ModelsFolder, f.Repo.Replace('/', Path.DirectorySeparatorChar));
            return (f, target: Path.Combine(folder, Path.GetFileName(f.Path)), job: EnqueueHf(f.Repo, f.Path, f.Size, null, folder, item.Group, headers));
        }).ToList();
        RefreshKitDownloads();
        var ok = await Task.WhenAll(jobs.Select(j => j.job?.WhenFinished ?? Task.FromResult(true)));
        RefreshKitDownloads();
        if (!ok.All(x => x)) return;
        var model = new ImageModel { Family = kit.Family.ToString(), KitId = kit.Id, DisplayName = kit.Title, HfRepo = kit.Files[0].Repo };
        foreach (var (f, target, _) in jobs) model.Files[f.Role] = target;
        RegisterImageModel(model);
        RefreshKitDownloads();
    }

    [RelayCommand]
    private void CancelKit(ImageKitItem? item)
    {
        if (item == null) return;
        Downloads.CancelGroup(item.Group);
        RefreshKitDownloads();
    }

    // ---------------- Мои модели изображений ----------------

    public ObservableCollection<ImageModelItem> ImageModels { get; } = new();

    public bool HasImageModels => ImageModels.Count > 0;

    private void RefreshImageModels()
    {
        var active = _s.ActiveImageModel?.Id;
        ImageModels.Clear();
        foreach (var m in _s.ImageModels.Models.OrderByDescending(m => m.AddedAt))
        {
            var item = new ImageModelItem(m, m.Id == active);
            // Семейство поменяли в карточке (имя файла его не выдало) — сохраняем; список перестроится по Changed
            item.FamilyChanged += (_, _) =>
            {
                _s.ImageModels.AddOrUpdate(item.Model);
                _s.NotifyImageSetupChanged();
            };
            ImageModels.Add(item);
        }
        OnPropertyChanged(nameof(HasImageModels));
        RefreshKitDownloads();
    }

    /// <summary>Открыть «Модели» на поиске генераторов изображений (кнопка со страницы «Изображения»).</summary>
    public void ShowImageModels()
    {
        if (!IsImageTask) SearchTask = HfTask.TextToImage;
    }

    /// <summary>Добавить свою модель изображений: один или несколько файлов (модель, VAE, кодировщики) с любого диска.</summary>
    [RelayCommand]
    private void AddImageFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("Models_PickImageFiles"),
            Filter = L.Get("Models_ImageWeightsFilter") + "|*.gguf;*.safetensors;*.sft;*.ckpt",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        RegisterImageModel(ImageModelRegistry.Describe(dlg.FileNames));
    }

    [RelayCommand]
    private void UseImageModel(ImageModelItem? item)
    {
        if (item == null) return;
        _s.Settings.Current.Images.ActiveModelId = item.Model.Id;
        _s.NotifyImageSetupChanged();
        RefreshImageModels();
        _main.Navigate(PageKind.Images);
    }

    [RelayCommand]
    private void RemoveImageModel(ImageModelItem? item)
    {
        if (item == null) return;
        var res = MessageBox.Show(L.F("Models_ConfirmRemove", item.Title, item.Model.MainFile ?? string.Empty), "Offline Chatter",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return;
        if (_s.ImageServer.Model?.Id == item.Model.Id) _s.ImageServer.Stop();
        _s.ImageModels.Remove(item.Model, deleteFiles: res == MessageBoxResult.Yes);
        if (_s.Settings.Current.Images.ActiveModelId == item.Model.Id) _s.Settings.Current.Images.ActiveModelId = null;
        _s.NotifyImageSetupChanged();
    }

    /// <summary>Указать файл для роли (например, VAE или кодировщик из другой папки).</summary>
    [RelayCommand]
    private void PickComponent(ImageComponentRow? row)
    {
        if (row == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.F("Models_PickComponent", row.Title),
            Filter = L.Get("Models_ImageWeightsFilter") + "|*.gguf;*.safetensors;*.sft;*.ckpt",
            CheckFileExists = true,
        };
        if (row.Path != null && Path.GetDirectoryName(row.Path) is { } dir && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() != true) return;
        row.Owner.Model.Files[row.Role] = dlg.FileName;
        // Главный файл одного вида: чекпойнт и диффузионная модель взаимоисключающие
        if (row.Role == ImageComponent.Checkpoint) row.Owner.Model.Files.Remove(ImageComponent.DiffusionModel);
        if (row.Role == ImageComponent.DiffusionModel) row.Owner.Model.Files.Remove(ImageComponent.Checkpoint);
        _s.ImageModels.AddOrUpdate(row.Owner.Model);
        _s.NotifyImageSetupChanged();
    }

    [RelayCommand]
    private void ClearComponent(ImageComponentRow? row)
    {
        if (row == null || !row.Owner.Model.Files.Remove(row.Role)) return;
        _s.ImageModels.AddOrUpdate(row.Owner.Model);
        _s.NotifyImageSetupChanged();
    }
}
