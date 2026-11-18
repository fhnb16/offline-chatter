using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Images;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Rendering;

namespace OfflineChatter.ViewModels;

/// <summary>Модель изображений в списке выбора и в «Моделях».</summary>
public sealed partial class ImageModelItem : ObservableObject
{
    public ImageModelItem(ImageModel model, bool active)
    {
        Model = model;
        _isActive = active;
    }

    public ImageModel Model { get; }
    public string Title => Model.DisplayName;
    public string FamilyTitle => L.Get("Enum_ImageFamily_" + Model.Preset.Family);
    public bool IsReady => Model.IsReady;
    public bool SupportsEdit => Model.Preset.SupportsRefImages;

    public string Details
    {
        get
        {
            var parts = new List<string> { FamilyTitle };
            var size = Model.SizeBytes;
            if (size > 0) parts.Add(Units.FormatBytes(size));
            if (Model.HfRepo != null) parts.Add(Model.HfRepo);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>«Не хватает: VAE, LLM» — или null, если всё на месте.</summary>
    public string? MissingText
    {
        get
        {
            var missing = Model.Missing();
            return missing.Count == 0 ? null : L.F("Img_Missing", string.Join(", ", missing.Select(c => L.Get("Enum_ImageComponent_" + c))));
        }
    }

    public IReadOnlyList<ImageComponentRow> Components => Model.Preset.AllComponents.Concat(Model.Files.Keys).Distinct()
        .Select(c => new ImageComponentRow(this, c)).ToList();

    [ObservableProperty] private bool _isActive;

    public IReadOnlyList<ImageFamily> Families { get; } = ImagePresets.All.Select(p => p.Family).ToList();

    /// <summary>Семейство: от него зависят нужные файлы, шаги и CFG.</summary>
    public ImageFamily Family
    {
        get => Model.Preset.Family;
        set
        {
            if (Model.Preset.Family == value) return;
            Model.Family = value.ToString();
            Refresh();
            FamilyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? FamilyChanged;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(MissingText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Components));
        OnPropertyChanged(nameof(FamilyTitle));
        OnPropertyChanged(nameof(SupportsEdit));
    }
}

/// <summary>Строка «роль — файл» в карточке модели изображений.</summary>
public sealed class ImageComponentRow
{
    public ImageComponentRow(ImageModelItem owner, ImageComponent role)
    {
        Owner = owner;
        Role = role;
    }

    public ImageModelItem Owner { get; }
    public ImageComponent Role { get; }
    public string Title => L.Get("Enum_ImageComponent_" + Role);
    public string Hint => L.Get("ImgComp_Hint_" + Role);
    public string Flag => ImageServerOptions.Flag(Role);
    public bool IsRequired => Owner.Model.Preset.Required.Contains(Role);
    public string? Path => Owner.Model.Files.TryGetValue(Role, out var p) ? p : null;
    public bool Exists => Path != null && File.Exists(Path);
    public string PathText => Path ?? L.Get(IsRequired ? "Img_FileNotSet" : "Img_FileOptional");
}

/// <summary>Картинка-референс или исходник для img2img.</summary>
public sealed class RefImageItem
{
    public RefImageItem(byte[] data, string name, BitmapSource? thumb)
    {
        Data = data;
        Name = name;
        Thumb = thumb;
    }

    public byte[] Data { get; }
    public string Name { get; }
    public BitmapSource? Thumb { get; }

    public static RefImageItem FromAttachment(ChatAttachment a) =>
        new(Convert.FromBase64String(a.Data), a.Name, ImageAttachments.Thumbnail(a, 160));
}

/// <summary>Готовая картинка в галерее (файл на диске + параметры из соседнего .json).</summary>
public sealed class GalleryItem
{
    public GalleryItem(string path, BitmapSource? thumb, ImageGenParams? p, string? caption)
    {
        Path = path;
        Thumb = thumb;
        Params = p;
        Caption = caption ?? System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path { get; }
    public BitmapSource? Thumb { get; }
    public ImageGenParams? Params { get; }
    public string Caption { get; }
    public string Tooltip => Params?.Prompt is { Length: > 0 } prompt ? prompt : Caption;
}

public sealed record AspectOption(string Label, int Width, int Height)
{
    public string Tip => $"{Width} × {Height}";
}

/// <summary>Сохранённая генерация: параметры + что было на самом деле (модель, время, эндпоинт).</summary>
public sealed class ImageSidecar
{
    public ImageGenParams Params { get; set; } = new();
    public string? Model { get; set; }
    public double Seconds { get; set; }
    public string? Endpoint { get; set; }
    public int RefImages { get; set; }
}

/// <summary>
/// Раздел «Изображения»: генерация из текста, редактирование по референсам, img2img; галерея результатов.
/// Генератор (sd-server) запускается сам при первой генерации и перезапускается при смене модели или параметров запуска.
/// </summary>
public sealed partial class ImagesViewModel : PageViewModel
{
    private const int GalleryLimit = 80;
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;
    private bool _loading;

    public ImagesViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => UpdateElapsed();
        _s.ImageModels.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshModels));
        _s.ImageSetupChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshSetup));
        _s.ImageServer.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshSetup));
        _s.ImageServer.ProgressChanged += (_, p) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnProgress(p)));

        _loading = true;
        var last = _s.Settings.Current.Images.LastParams;
        if (last != null) LoadParams(last);
        _loading = false;
        RefreshModels();
    }

    public override PageKind Kind => PageKind.Images;

    public MainViewModel Main => _main;

    // ---------------- Модель ----------------

    public ObservableCollection<ImageModelItem> Models { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(ShowNegative), nameof(Modes), nameof(ShowFlowShift), nameof(PresetHint), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private ImageModelItem? _selectedModel;

    public ImagePreset? Preset => SelectedModel?.Model.Preset;

    /// <summary>Пояснение к семейству: сколько шагов и какой CFG обычно нужен.</summary>
    public string? PresetHint => Preset is { } p ? L.Get("ImgFamily_Hint_" + p.Family) : null;

    public bool HasModels => Models.Count > 0;

    private void RefreshModels()
    {
        var activeId = _s.ActiveImageModel?.Id;
        var keep = SelectedModel?.Model.Id ?? activeId;
        _loading = true;
        Models.Clear();
        foreach (var m in _s.ImageModels.Models.OrderByDescending(m => m.AddedAt))
            Models.Add(new ImageModelItem(m, m.Id == activeId));
        SelectedModel = Models.FirstOrDefault(m => m.Model.Id == keep) ?? Models.FirstOrDefault(m => m.IsReady) ?? Models.FirstOrDefault();
        _loading = false;
        OnPropertyChanged(nameof(HasModels));
        RebuildAspects();
        RefreshSetup();
    }

    partial void OnSelectedModelChanged(ImageModelItem? value)
    {
        RebuildAspects();
        RefreshSetup();
        if (value == null) return;
        if (_s.Settings.Current.Images.ActiveModelId != value.Model.Id && !_loading)
        {
            _s.Settings.Current.Images.ActiveModelId = value.Model.Id;
            // «Движок» и «Модели» показывают модель по умолчанию — пусть обновятся
            _s.NotifyImageSetupChanged();
        }
        // Своё семейство — свои шаги, CFG и размер; промпт и референсы остаются
        var lastFamily = _s.Settings.Current.Images.LastParams?.Family;
        if (!_loading || lastFamily != value.Model.Family) ResetToPreset();
        if (!Modes.Contains(Mode)) Mode = ImageMode.TextToImage;
    }

    // ---------------- Состояние: движок, модель, сервер ----------------

    public bool HasEngine => _s.ImageEngines.Active != null;
    public bool HasReadyModel => SelectedModel?.IsReady == true;
    public bool IsSetupNeeded => !HasEngine || !HasReadyModel;

    public string ServerStateText
    {
        get
        {
            var srv = _s.ImageServer;
            var state = L.Get("Enum_ServerState_" + srv.State);
            return srv.State == ServerState.Ready && srv.Model != null ? state + " · " + srv.Model.DisplayName : state;
        }
    }

    public ServerState ServerState => _s.ImageServer.State;
    public bool IsServerRunning => _s.ImageServer.IsRunning;
    public string? ServerError => _s.ImageServer.State == ServerState.Failed ? _s.ImageServerError ?? _s.ImageServer.LastError : null;

    private void RefreshSetup()
    {
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(HasReadyModel));
        OnPropertyChanged(nameof(IsSetupNeeded));
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(ServerState));
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(ServerError));
        GenerateCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenEngine()
    {
        _main.GetPage<EngineViewModel>()?.ShowTab(EngineTab.Images);
        _main.Navigate(PageKind.Engine);
    }

    [RelayCommand]
    private void OpenModels()
    {
        _main.GetPage<ModelsViewModel>()?.ShowImageModels();
        _main.Navigate(PageKind.Models);
    }

    private bool CanStopServer() => _s.ImageServer.IsRunning && !IsGenerating;

    /// <summary>Остановить генератор и освободить видеопамять.</summary>
    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private void StopServer() => _s.ImageServer.Stop();

    // ---------------- Параметры ----------------

    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _negativePrompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditMode), nameof(IsImg2ImgMode), nameof(ModeHint))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private ImageMode _mode = ImageMode.TextToImage;

    [ObservableProperty] private int _width = 1024;
    [ObservableProperty] private int _height = 1024;
    [ObservableProperty] private int _steps = 20;
    [ObservableProperty] private double _cfg = 1.0;
    [ObservableProperty] private string _sampler = "euler";
    [ObservableProperty] private string _scheduler = string.Empty;
    /// <summary>Пусто — по умолчанию модели.</summary>
    [ObservableProperty] private string _flowShift = string.Empty;
    /// <summary>Пусто — случайное.</summary>
    [ObservableProperty] private string _seed = string.Empty;
    [ObservableProperty] private int _batchCount = 1;
    [ObservableProperty] private double _strength = 0.75;
    [ObservableProperty] private string _clipSkip = string.Empty;

    public IReadOnlyList<string> Samplers => ImagePresets.Samplers;
    public IReadOnlyList<string> Schedulers => ImagePresets.Schedulers;
    public IReadOnlyList<int> BatchOptions { get; } = new[] { 1, 2, 3, 4 };

    public IReadOnlyList<ImageMode> Modes
    {
        get
        {
            var list = new List<ImageMode> { ImageMode.TextToImage };
            if (Preset?.SupportsRefImages == true) list.Add(ImageMode.Edit);
            if (Preset?.SupportsImg2Img != false) list.Add(ImageMode.ImageToImage);
            return list;
        }
    }

    public bool IsEditMode => Mode == ImageMode.Edit;
    public bool IsImg2ImgMode => Mode == ImageMode.ImageToImage;
    public bool CanEdit => Preset?.SupportsRefImages == true;
    public string ModeHint => L.Get("Img_ModeHint_" + Mode);
    public bool ShowNegative => Preset?.UsesNegativePrompt != false;
    public bool ShowFlowShift => Preset?.FlowShift != null || !string.IsNullOrEmpty(FlowShift);

    public string SizeText => $"{Width} × {Height}";
    partial void OnWidthChanged(int value) => OnPropertyChanged(nameof(SizeText));
    partial void OnHeightChanged(int value) => OnPropertyChanged(nameof(SizeText));
    partial void OnModeChanged(ImageMode value) => GenerateCommand.NotifyCanExecuteChanged();

    public ObservableCollection<AspectOption> Aspects { get; } = new();

    private static readonly (int w, int h)[] Ratios = { (1, 1), (4, 3), (3, 4), (3, 2), (2, 3), (16, 9), (9, 16) };

    private void RebuildAspects()
    {
        Aspects.Clear();
        var p = Preset ?? ImagePresets.Get(ImageFamily.Custom);
        foreach (var (w, h) in Ratios)
        {
            var (pw, ph) = ImagePresets.SizeFor(p, w, h);
            Aspects.Add(new AspectOption($"{w}:{h}", pw, ph));
        }
    }

    [RelayCommand]
    private void SetAspect(AspectOption? a)
    {
        if (a == null) return;
        Width = a.Width;
        Height = a.Height;
    }

    [RelayCommand]
    private void SwapSize() => (Width, Height) = (Height, Width);

    /// <summary>Вернуть шаги, CFG, сэмплер и размер, рекомендованные для семейства модели.</summary>
    [RelayCommand]
    private void ResetToPreset()
    {
        if (Preset is not { } p) return;
        var g = CurrentParams();
        g.ApplyPreset(p);
        LoadParams(g, keepPrompt: true);
    }

    [RelayCommand]
    private void RandomSeed() => Seed = string.Empty;

    private void LoadParams(ImageGenParams g, bool keepPrompt = false)
    {
        if (!keepPrompt)
        {
            Prompt = g.Prompt;
            NegativePrompt = g.NegativePrompt;
        }
        Mode = g.Mode;
        Width = g.Width;
        Height = g.Height;
        Steps = g.Steps;
        Cfg = g.Cfg;
        Sampler = g.Sampler;
        Scheduler = g.Scheduler;
        FlowShift = g.FlowShift?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        BatchCount = Math.Max(1, g.BatchCount);
        Strength = g.Strength;
        ClipSkip = g.ClipSkip?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        OnPropertyChanged(nameof(ShowFlowShift));
    }

    private ImageGenParams CurrentParams()
    {
        static double? D(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new ImageGenParams
        {
            Prompt = Prompt.Trim(),
            NegativePrompt = ShowNegative ? NegativePrompt.Trim() : string.Empty,
            Mode = Mode,
            Width = Math.Max(64, Width / 8 * 8),
            Height = Math.Max(64, Height / 8 * 8),
            Steps = Math.Max(1, Steps),
            Cfg = Cfg,
            Sampler = Sampler,
            Scheduler = Scheduler ?? string.Empty,
            FlowShift = D(FlowShift),
            Seed = long.TryParse(Seed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) && seed >= 0 ? seed : -1,
            BatchCount = Math.Max(1, BatchCount),
            Strength = Strength,
            ClipSkip = int.TryParse(ClipSkip.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) && cs > 0 ? cs : null,
            ModelName = SelectedModel?.Title,
            Family = Preset?.Id,
        };
    }

    // ---------------- Референсы и исходник ----------------

    public ObservableCollection<RefImageItem> RefImages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private RefImageItem? _initImage;

    public bool HasRefImages => RefImages.Count > 0;

    /// <summary>Добавить картинки: в режиме img2img — исходник, иначе — референсы (и переключить на редактирование).</summary>
    public void AddImages(IEnumerable<ChatAttachment> images)
    {
        foreach (var a in images)
        {
            var item = RefImageItem.FromAttachment(a);
            if (Mode == ImageMode.ImageToImage)
            {
                InitImage = item;
                continue;
            }
            RefImages.Add(item);
        }
        if (RefImages.Count > 0 && Mode == ImageMode.TextToImage && CanEdit) Mode = ImageMode.Edit;
        OnPropertyChanged(nameof(HasRefImages));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var list = new List<ChatAttachment>();
        foreach (var p in paths)
        {
            try { list.Add(ImageAttachments.FromFile(p)); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Error = L.F("Img_CannotOpen", System.IO.Path.GetFileName(p), ex.Message);
            }
        }
        AddImages(list);
    }

    [RelayCommand]
    private void PickImages()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get(Mode == ImageMode.ImageToImage ? "Img_PickInit" : "Img_PickRefs"),
            Filter = L.Get("Img_ImageFilter") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
            Multiselect = Mode != ImageMode.ImageToImage,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    /// <summary>Вставить картинку из буфера (Ctrl+V на странице).</summary>
    [RelayCommand]
    public void PasteImage()
    {
        var bmp = ImageAttachments.GetClipboardImage();
        if (bmp == null)
        {
            if (Clipboard.ContainsFileDropList()) AddFiles(Clipboard.GetFileDropList().Cast<string>());
            return;
        }
        AddImages(new[] { ImageAttachments.FromBitmap(bmp, "clipboard.png") });
    }

    [RelayCommand]
    private void RemoveRef(RefImageItem? item)
    {
        if (item == null) return;
        RefImages.Remove(item);
        OnPropertyChanged(nameof(HasRefImages));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearInit() => InitImage = null;

    // ---------------- Генерация ----------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(CancelCommand), nameof(StopServerCommand))]
    private bool _isGenerating;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _lastStats;

    private bool CanGenerate() =>
        !IsGenerating && SelectedModel?.IsReady == true && HasEngine &&
        (Mode != ImageMode.Edit || RefImages.Count > 0) && (Mode != ImageMode.ImageToImage || InitImage != null);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (SelectedModel == null) return;
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            Error = L.Get("Img_EmptyPrompt");
            return;
        }
        var model = SelectedModel.Model;
        var p = CurrentParams();
        // Случайное зерно выбираем сами — чтобы его можно было сохранить и повторить картинку
        if (p.Seed < 0) p.Seed = new Random().Next(0, int.MaxValue);
        _s.Settings.Current.Images.LastParams = p.Clone();
        _s.Settings.Save();

        var cts = _cts = new CancellationTokenSource();
        IsGenerating = true;
        _s.ImageBusy = true;
        Error = null;
        LastStats = null;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressText = null;
        _startedAt = DateTime.Now;
        UpdateElapsed();
        _clock.Start();
        try
        {
            if (!(_s.ImageServer.State == ServerState.Ready && _s.ImageServer.Model?.Id == model.Id))
                Status = L.F("Img_LoadingModel", model.DisplayName);
            var ok = await Task.Run(() => _s.EnsureImageServerAsync(model, cts.Token));
            if (cts.IsCancellationRequested) return;
            if (!ok)
            {
                Error = L.F("Img_ServerFailed", _s.ImageServerError ?? "?");
                return;
            }
            Status = L.Get("Img_Generating");
            _s.ImageServer.ResetProgress();
            _startedAt = DateTime.Now;
            var request = new ImageRequest
            {
                Params = p,
                RefImages = Mode == ImageMode.Edit ? RefImages.Select(r => r.Data).ToList() : Array.Empty<byte[]>(),
                InitImage = Mode == ImageMode.ImageToImage ? InitImage?.Data : null,
            };
            var client = new ImageClient(_s.LocalHttp, _s.ImageServer.BaseUrl);
            var result = await Task.Run(() => client.GenerateAsync(request, cts.Token));
            var saved = await Task.Run(() => Save(result, p, model, request.RefImages.Count));
            foreach (var item in saved) Gallery.Insert(0, item);
            while (Gallery.Count > GalleryLimit) Gallery.RemoveAt(Gallery.Count - 1);
            OnPropertyChanged(nameof(HasGallery));
            SelectedImage = saved.FirstOrDefault();
            LastStats = L.F("Img_Stats", result.Images.Count, result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture),
                p.Seed.ToString(CultureInfo.InvariantCulture), result.Endpoint);
            Status = null;
            Log.Info("Images", "Generated", new { count = result.Images.Count, ms = (long)result.Elapsed.TotalMilliseconds, result.Endpoint, family = model.Family });
        }
        catch (OperationCanceledException)
        {
            Status = L.Get("Img_Canceled");
        }
        catch (Exception ex) when (ex is ImageApiException or System.Net.Http.HttpRequestException or IOException or TimeoutException or JsonException)
        {
            Error = L.F("Img_Failed", ex.GetBaseException().Message);
            Status = null;
            Log.Warn("Images", "Generation failed", ex);
        }
        finally
        {
            _clock.Stop();
            ElapsedText = null;
            IsGenerating = false;
            _s.ImageBusy = false;
            ProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressText = null;
            cts.Dispose();
            _cts = null;
            RefreshSetup();
        }
    }

    private bool CanCancel() => IsGenerating;

    /// <summary>
    /// Отмена: ответ больше не ждём. sd-server досчитает текущую картинку сам — чтобы освободить видеокарту сразу,
    /// есть «Остановить генератор».
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void OnProgress(ImageProgress p)
    {
        if (!IsGenerating) return;
        ProgressIndeterminate = false;
        ProgressValue = 100.0 * p.Step / p.Total;
        var eta = p.SecondsPerStep is { } spi ? TimeSpan.FromSeconds(spi * (p.Total - p.Step)) : (TimeSpan?)null;
        ProgressText = L.F("Img_Progress", p.Step, p.Total,
            p.SecondsPerStep?.ToString("0.00", CultureInfo.CurrentCulture) ?? "?",
            eta is { } e ? Math.Ceiling(e.TotalSeconds).ToString("0", CultureInfo.CurrentCulture) : "?");
        Status = L.Get("Img_Generating");
    }

    [ObservableProperty] private string? _elapsedText;

    private void UpdateElapsed()
    {
        if (!IsGenerating) return;
        ElapsedText = L.F("Img_Elapsed", (DateTime.Now - _startedAt).TotalSeconds.ToString("0", CultureInfo.CurrentCulture));
    }

    /// <summary>PNG + .json с параметрами: images/гггг-мм-дд/ччммсс-зерно-N.png.</summary>
    private List<GalleryItem> Save(ImageResult result, ImageGenParams p, ImageModel model, int refs)
    {
        var dir = System.IO.Path.Combine(_s.ImagesFolder, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var list = new List<GalleryItem>();
        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        for (var i = 0; i < result.Images.Count; i++)
        {
            var path = System.IO.Path.Combine(dir, $"{stamp}-{p.Seed}-{i + 1}.png");
            File.WriteAllBytes(path, result.Images[i]);
            var meta = new ImageSidecar { Params = p, Model = model.DisplayName, Seconds = result.Elapsed.TotalSeconds, Endpoint = result.Endpoint, RefImages = refs };
            Core.Storage.JsonFile.WriteAtomic(System.IO.Path.ChangeExtension(path, ".json"), meta);
            list.Add(new GalleryItem(path, LoadThumb(path), p, null));
        }
        return list;
    }

    // ---------------- Галерея ----------------

    public ObservableCollection<GalleryItem> Gallery { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedImage), nameof(SelectedPreview))]
    private GalleryItem? _selectedImage;

    public bool HasSelectedImage => SelectedImage != null;

    /// <summary>Крупный просмотр выбранной картинки (полный размер, декодируется по требованию).</summary>
    public BitmapSource? SelectedPreview => SelectedImage is { } g ? LoadThumb(g.Path, 1024) : null;

    public bool HasGallery => Gallery.Count > 0;

    public override void OnNavigatedTo()
    {
        RefreshModels();
        if (Gallery.Count == 0) _ = LoadGalleryAsync();
    }

    private async Task LoadGalleryAsync()
    {
        var folder = _s.ImagesFolder;
        var items = await Task.Run(() =>
        {
            if (!Directory.Exists(folder)) return new List<GalleryItem>();
            try
            {
                return Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(GalleryLimit)
                    .Select(f => new GalleryItem(f.FullName, LoadThumb(f.FullName), ReadSidecar(f.FullName)?.Params, null))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Images", "Cannot read gallery", ex);
                return new List<GalleryItem>();
            }
        });
        Gallery.Clear();
        foreach (var i in items) Gallery.Add(i);
        OnPropertyChanged(nameof(HasGallery));
    }

    private static ImageSidecar? ReadSidecar(string png)
    {
        try { return Core.Storage.JsonFile.Read<ImageSidecar>(System.IO.Path.ChangeExtension(png, ".json")); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static BitmapSource? LoadThumb(string path, int width = 256)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(path))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = width;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void OpenImage(GalleryItem? item)
    {
        if (item == null || !File.Exists(item.Path)) return;
        Views.ImagePreviewWindow.Show(new ChatAttachment
        {
            Kind = AttachmentKind.Image,
            Name = System.IO.Path.GetFileName(item.Path),
            MimeType = "image/png",
            Data = Convert.ToBase64String(File.ReadAllBytes(item.Path)),
        });
    }

    [RelayCommand]
    private void CopyImage(GalleryItem? item)
    {
        if (item == null || LoadThumb(item.Path, 0) is not { } full) return;
        try
        {
            Clipboard.SetImage(full);
            Status = L.Get("Chat_Copied");
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private void SaveImageAs(GalleryItem? item)
    {
        if (item == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileName(item.Path), Filter = "PNG|*.png" };
        if (dlg.ShowDialog() != true) return;
        try { File.Copy(item.Path, dlg.FileName, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; }
    }

    [RelayCommand]
    private void DeleteImage(GalleryItem? item)
    {
        if (item == null) return;
        try
        {
            File.Delete(item.Path);
            File.Delete(System.IO.Path.ChangeExtension(item.Path, ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return;
        }
        Gallery.Remove(item);
        if (ReferenceEquals(SelectedImage, item)) SelectedImage = Gallery.FirstOrDefault();
        OnPropertyChanged(nameof(HasGallery));
    }

    /// <summary>Добавить готовую картинку в референсы (продолжить редактирование).</summary>
    [RelayCommand]
    private void UseAsRef(GalleryItem? item)
    {
        if (item == null) return;
        if (CanEdit) Mode = ImageMode.Edit;
        else if (Modes.Contains(ImageMode.ImageToImage)) Mode = ImageMode.ImageToImage;
        AddFiles(new[] { item.Path });
    }

    /// <summary>Повторить: промпт, размер, шаги и зерно этой картинки.</summary>
    [RelayCommand]
    private void ReuseParams(GalleryItem? item)
    {
        if (item?.Params is not { } p) return;
        LoadParams(p);
        Seed = p.Seed >= 0 ? p.Seed.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    [RelayCommand]
    private void OpenFolder() => SettingsViewModel.OpenFolder(Directory.Exists(_s.ImagesFolder) ? _s.ImagesFolder : _s.Paths.Root);

    [RelayCommand]
    private void ShowInFolder(GalleryItem? item)
    {
        if (item == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warn("Images", "Cannot open explorer", ex); }
    }

    partial void OnSelectedImageChanged(GalleryItem? value) => OnPropertyChanged(nameof(HasGallery));
}
