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

/// <summary>Картинка-референс или исходник: исходные байты без сжатия, размер, номер для тега &lt;image:N&gt;.</summary>
public sealed partial class RefImageItem : ObservableObject
{
    public RefImageItem(GenImage image, string name, BitmapSource? thumb)
    {
        Image = image;
        Name = name;
        Thumb = thumb;
    }

    public GenImage Image { get; }
    public byte[] Data => Image.Data;
    public string Name { get; }
    public BitmapSource? Thumb { get; }
    public string SizeText => $"{Image.Width} × {Image.Height}";
    public string Tooltip => Name + " · " + SizeText + (Image.HasAlpha ? " · RGBA" : string.Empty);

    /// <summary>Номер в списке (с 1) — он же ID для &lt;image:N&gt; в промпте.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tag), nameof(IndexText))]
    private int _index;

    public string IndexText => Index.ToString(CultureInfo.InvariantCulture);
    public string Tag => PromptImageTags.Tag(IndexText);

    /// <summary>«→ 1536 × 1152», если картинка будет уменьшена перед отправкой.</summary>
    [ObservableProperty] private string? _sentText;

    public static RefImageItem Create(GenImage image, string name) => new(image, name, ImagePrep.Thumbnail(image.Data, 176));
}

/// <summary>Готовая картинка в галерее (файл на диске + параметры из .json или из самой картинки).</summary>
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

    /// <summary>Что было до правки (исходник или первый референс) — для сравнения «до / после»; только для картинок этого сеанса.</summary>
    public BitmapSource? Before { get; set; }
}

public sealed record AspectOption(string Label, int Width, int Height)
{
    public string Tip => $"{Width} × {Height}";
}

/// <summary>Значение со своим названием для списков (кэш шагов, превью, LoRA).</summary>
public sealed record ValueOption(string Value, string Title);

/// <summary>Сохранённая генерация: параметры + что было на самом деле (модель, время, эндпоинт, параметры сервера).</summary>
public sealed class ImageSidecar
{
    public ImageGenParams Params { get; set; } = new();
    public string? Model { get; set; }
    public double Seconds { get; set; }
    public string? Endpoint { get; set; }
    public int RefImages { get; set; }
    public bool Mask { get; set; }
    public double? ConditioningSeconds { get; set; }
    public double? SecondsPerStep { get; set; }
    /// <summary>Фактическая программа генерации от sd-server (с итоговым зерном).</summary>
    public string? GenerationInfo { get; set; }
}

/// <summary>
/// Раздел «Изображения»: правка по референсам (с картинками в промпте), перерисовка с маской, генерация из текста — на одной
/// загруженной модели, без перезапуска генератора между режимами. Живое превью по шагам, отмена на сервере, сравнение
/// «до / после», галерея с параметрами внутри PNG. Генератор (sd-server) запускается сам при первой генерации
/// и перезапускается только при смене модели или параметров запуска.
/// </summary>
public sealed partial class ImagesViewModel : PageViewModel
{
    private const int GalleryLimit = 80;
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private readonly ConditioningCacheTracker _condCache = new();
    private DateTime? _lastConditioningUse;
    private CancellationTokenSource? _cts;
    private bool _resetGeneration;
    private DateTime _startedAt;
    private bool _loading;
    private bool _nativeProgress;
    private bool _loadingGallery;
    private CancellationTokenSource? _previewLoad;

    public ImagesViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => UpdateElapsed();
        _s.ImageModels.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshModels));
        _s.ImageSetupChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshSetup();
            RefreshLoras();
        }));
        _s.ImageServer.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshSetup));
        _s.ImageServer.ProgressChanged += (_, p) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnProgress(p)));
        RefImages.CollectionChanged += (_, _) => OnRefsChanged();

        var settings = _s.Settings.Current.Images;
        _previewMode = settings.PreviewMode;
        _limitInputSize = settings.LimitInputSize;

        _loading = true;
        var last = settings.LastParams;
        if (last != null) LoadParams(last);
        _loading = false;
        RefreshModels();
        RefreshLoras();
    }

    public override PageKind Kind => PageKind.Images;

    public MainViewModel Main => _main;

    // ---------------- Модель ----------------

    public ObservableCollection<ImageModelItem> Models { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(ShowNegative), nameof(Modes), nameof(ShowFlowShift), nameof(PresetHint), nameof(CanEdit),
        nameof(ShowTransparent), nameof(CanFinal2K), nameof(ShowPromptTags), nameof(VisionMissing), nameof(ShowRefs))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(Final2KCommand))]
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
        // Своё семейство — свои шаги, CFG, сигмы и размер; промпт и картинки остаются
        var lastFamily = _s.Settings.Current.Images.LastParams?.Family;
        if (!_loading || lastFamily != value.Model.Family) ResetToPreset();
        if (!Modes.Contains(Mode)) Mode = ImageMode.TextToImage;
        UpdateRefInfo();
        UpdateWarnings();
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
        OnPropertyChanged(nameof(VisionMissing));
        GenerateCommand.NotifyCanExecuteChanged();
        Final2KCommand.NotifyCanExecuteChanged();
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopServerCommand), nameof(GenerateCommand), nameof(Final2KCommand), nameof(CancelCommand))]
    private bool _isReleasingMemory;

    private bool CanStopServer() => _s.ImageServer.IsRunning && !IsReleasingMemory;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServerAsync()
    {
        IsReleasingMemory = true;
        Status = L.Get("Img_ReleasingMemory");
        _resetGeneration = _cts != null;
        _cts?.Cancel();
        try
        {
            if (await Task.Run(() => _s.StopImageServerAsync(CancellationToken.None)))
            {
                _condCache.Clear();
                ConditioningText = null;
                Status = L.Get("Img_MemoryReleased");
            }
            else Status = null;
        }
        finally
        {
            IsReleasingMemory = false;
            RefreshSetup();
        }
    }

    // ---------------- Параметры ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptTagsText))]
    private string _prompt = string.Empty;

    [ObservableProperty] private string _negativePrompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditMode), nameof(IsImg2ImgMode), nameof(ModeHint), nameof(ShowSizeFromRef), nameof(ShowRefs), nameof(ShowPromptTags))]
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
    /// <summary>Свои сигмы через запятую; пусто — расписание сэмплера.</summary>
    [ObservableProperty] private string _sigmas = string.Empty;
    [ObservableProperty] private string _cacheMode = string.Empty;
    [ObservableProperty] private bool _transparent;

    public IReadOnlyList<string> Samplers => ImagePresets.Samplers;
    public IReadOnlyList<string> Schedulers => ImagePresets.Schedulers;
    public IReadOnlyList<int> BatchOptions { get; } = new[] { 1, 2, 3, 4 };

    public IReadOnlyList<ValueOption> CacheModes { get; } = new[] { string.Empty, "easycache", "dbcache", "taylorseer", "cache-dit", "ucache", "spectrum" }
        .Select(v => new ValueOption(v, v.Length == 0 ? L.Get("Img_CacheOff") : v)).ToList();

    public IReadOnlyList<ImageMode> Modes
    {
        get
        {
            // Порядок вкладок: правка (главное у моделей с референсами), перерисовка, из текста
            var list = new List<ImageMode>();
            if (Preset?.SupportsRefImages == true) list.Add(ImageMode.Edit);
            if (Preset?.SupportsImg2Img != false) list.Add(ImageMode.ImageToImage);
            list.Add(ImageMode.TextToImage);
            return list;
        }
    }

    public bool IsEditMode => Mode == ImageMode.Edit;
    public bool IsImg2ImgMode => Mode == ImageMode.ImageToImage;
    public bool CanEdit => Preset?.SupportsRefImages == true;
    public string ModeHint => L.Get("Img_ModeHint_" + Mode);
    public bool ShowNegative => Preset?.UsesNegativePrompt != false;
    public bool ShowFlowShift => Preset?.FlowShift != null || !string.IsNullOrEmpty(FlowShift);
    public bool ShowTransparent => Preset?.SupportsAlpha == true;

    /// <summary>Кодировщику со зрением можно вставлять картинки прямо в промпт.</summary>
    public bool ShowPromptTags => Preset?.SupportsPromptImages == true && Mode != ImageMode.TextToImage;

    /// <summary>Правка без mmproj у GGUF-кодировщика: sd.cpp откажет — предупреждаем заранее.</summary>
    public bool VisionMissing => Mode == ImageMode.Edit && SelectedModel?.Model.NeedsVisionForEdit == true;

    public string SizeText => $"{Width} × {Height}";

    partial void OnWidthChanged(int value)
    {
        OnPropertyChanged(nameof(SizeText));
        UpdateRefInfo();
        UpdateWarnings();
    }

    partial void OnHeightChanged(int value)
    {
        OnPropertyChanged(nameof(SizeText));
        UpdateRefInfo();
        UpdateWarnings();
    }

    partial void OnModeChanged(ImageMode value)
    {
        GenerateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisionMissing));
        ApplySizeFromSource();
        UpdateRefInfo();
        UpdateWarnings();
    }

    partial void OnCfgChanged(double value) => UpdateWarnings();
    partial void OnStepsChanged(int value) => UpdateWarnings();
    partial void OnSigmasChanged(string value) => UpdateWarnings();
    partial void OnCacheModeChanged(string value) => UpdateWarnings();

    public ObservableCollection<AspectOption> Aspects { get; } = new();

    private static readonly (int w, int h)[] Ratios = { (1, 1), (4, 3), (3, 4), (3, 2), (2, 3), (16, 9), (9, 16) };

    private void RebuildAspects()
    {
        Aspects.Clear();
        var p = Preset ?? ImagePresets.Get(ImageFamily.Custom);
        foreach (var (w, h) in Ratios)
        {
            var (pw, ph) = ImagePresets.SizeFor(p, w, h, Is2K ? 2.0 : 1.0);
            Aspects.Add(new AspectOption($"{w}:{h}", pw, ph));
        }
    }

    [RelayCommand]
    private void SetAspect(AspectOption? a)
    {
        if (a == null) return;
        SizeFromRef = false;
        Width = a.Width;
        Height = a.Height;
    }

    [RelayCommand]
    private void SwapSize() => (Width, Height) = (Height, Width);

    // ----- Размер по исходной картинке и масштаб 1K / 2K -----

    [ObservableProperty] private bool _sizeFromRef = true;

    /// <summary>2K: площадь ×4. Родной размер правки Qwen-Image 2.1; плитки VAE включаются сами.</summary>
    [ObservableProperty] private bool _is2K;

    public bool ShowSizeFromRef => Mode != ImageMode.TextToImage;

    partial void OnSizeFromRefChanged(bool value) => ApplySizeFromSource();

    partial void OnIs2KChanged(bool value)
    {
        RebuildAspects();
        UpdateRefInfo();
        // При загрузке параметров размер уже 2K — не масштабировать повторно
        if (_loading) return;
        if (SizeFromRef && SizeSource() != null)
        {
            ApplySizeFromSource();
            return;
        }
        // Без исходника — масштабируем текущий размер, сохраняя пропорции
        var multiple = Preset?.RoundTo ?? 64;
        var k = value ? 2.0 : 0.5;
        Width = ImageInputPolicy.Align(Width * k, multiple);
        Height = ImageInputPolicy.Align(Height * k, multiple);
    }

    private GenImage? SizeSource() => Mode switch
    {
        ImageMode.Edit => RefImages.FirstOrDefault()?.Image,
        ImageMode.ImageToImage => InitImage?.Image,
        _ => null,
    };

    private void ApplySizeFromSource()
    {
        if (!SizeFromRef || _loading || SizeSource() is not { } src || Preset is not { } p) return;
        var side = (int)Math.Round(Math.Sqrt(p.BaseArea)) * (Is2K ? 2 : 1);
        var (w, h) = ImageInputPolicy.SizeForSource(src.Width, src.Height, side, p.RoundTo);
        Width = w;
        Height = h;
    }

    /// <summary>Вернуть шаги, CFG, сэмплер, сигмы и размер, рекомендованные для семейства модели.</summary>
    [RelayCommand]
    private void ResetToPreset()
    {
        if (Preset is not { } p) return;
        var g = CurrentParams();
        g.ApplyPreset(p);
        LoadParams(g, keepPrompt: true);
        if (Is2K)
        {
            var (w, h) = ImagePresets.SizeFor(p, 1, 1, 2.0);
            Width = w;
            Height = h;
        }
        ApplySizeFromSource();
    }

    [RelayCommand]
    private void RandomSeed() => Seed = string.Empty;

    private void LoadParams(ImageGenParams g, bool keepPrompt = false)
    {
        var wasLoading = _loading;
        _loading = true;
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
        Sigmas = g.Sigmas ?? string.Empty;
        CacheMode = g.CacheMode ?? string.Empty;
        Transparent = g.Transparent;
        InitFit = g.InitFit;
        InitCanny = g.InitCanny;
        MaskInvert = g.MaskInvert;
        SizeFromRef = g.SizeFromRef;
        Is2K = g.SizeScale >= 2;
        var lora = g.Loras.FirstOrDefault();
        SelectedLora = lora?.Path ?? string.Empty;
        LoraStrength = lora?.Multiplier ?? 1.0;
        _loading = wasLoading;
        RebuildAspects();
        OnPropertyChanged(nameof(ShowFlowShift));
        UpdateWarnings();
    }

    private ImageGenParams CurrentParams()
    {
        static double? D(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var multiple = Preset?.SizeMultiple ?? 8;
        var p = new ImageGenParams
        {
            Prompt = Prompt.Trim(),
            NegativePrompt = ShowNegative ? NegativePrompt.Trim() : string.Empty,
            Mode = Mode,
            Width = ImageInputPolicy.AlignDown(Math.Max(64, Width), multiple),
            Height = ImageInputPolicy.AlignDown(Math.Max(64, Height), multiple),
            Steps = Math.Max(1, Steps),
            Cfg = Cfg,
            Sampler = Sampler,
            Scheduler = Scheduler ?? string.Empty,
            FlowShift = D(FlowShift),
            Seed = long.TryParse(Seed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) && seed >= 0 ? seed : -1,
            BatchCount = Math.Max(1, BatchCount),
            Strength = Strength,
            ClipSkip = int.TryParse(ClipSkip.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) && cs > 0 ? cs : null,
            Sigmas = ImageSigmas.Parse(Sigmas) is { } sig ? ImageSigmas.Format(sig) : string.Empty,
            CacheMode = CacheMode ?? string.Empty,
            Transparent = ShowTransparent && Transparent,
            InitFit = InitFit,
            InitCanny = InitCanny,
            MaskInvert = MaskInvert,
            SizeFromRef = SizeFromRef,
            SizeScale = Is2K ? 2 : 1,
            ModelName = SelectedModel?.Title,
            Family = Preset?.Id,
        };
        if (HasLoraDir && !string.IsNullOrEmpty(SelectedLora)) p.Loras.Add(new ImageLora { Path = SelectedLora, Multiplier = LoraStrength });
        return p;
    }

    // ---------------- Предупреждения о сочетании параметров ----------------

    public ObservableCollection<string> Warnings { get; } = new();

    public bool HasWarnings => Warnings.Count > 0;

    private void UpdateWarnings()
    {
        if (_loading) return;
        Warnings.Clear();
        if (Preset is { } preset)
        {
            var p = new ImageGenParams { Mode = Mode, Cfg = Cfg, Steps = Steps, Sigmas = Sigmas, CacheMode = CacheMode, Width = Width, Height = Height };
            // Про mmproj предупреждает строка у референсов — здесь не дублируем
            foreach (var w in ImageParamChecks.Check(preset, p))
                Warnings.Add(L.Get("ImgWarn_" + w));
        }
        if (ImageSigmas.Parse(Sigmas) == null && !string.IsNullOrWhiteSpace(Sigmas)) Warnings.Add(L.Get("ImgWarn_BadSigmas"));
        OnPropertyChanged(nameof(HasWarnings));
    }

    // ---------------- LoRA ----------------

    public ObservableCollection<ValueOption> Loras { get; } = new();

    [ObservableProperty] private string _selectedLora = string.Empty;
    [ObservableProperty] private double _loraStrength = 1.0;

    public bool HasLoraDir => !string.IsNullOrWhiteSpace(_s.Settings.Current.Images.Launch.LoraDir);

    /// <summary>Файлы LoRA из папки сервера (--lora-model-dir): относительный путь с прямыми слешами — так их называет sd-server.</summary>
    private void RefreshLoras()
    {
        var dir = _s.Settings.Current.Images.Launch.LoraDir;
        var keep = SelectedLora;
        Loras.Clear();
        Loras.Add(new ValueOption(string.Empty, L.Get("Img_LoraNone")));
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            try
            {
                var root = Path.GetFullPath(dir!).TrimEnd('\\') + "\\";
                foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                             .Where(f => ImagePresets.IsWeightFile(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(500))
                {
                    var rel = f.Substring(root.Length).Replace('\\', '/');
                    Loras.Add(new ValueOption(rel, rel));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Images", "Cannot list LoRA folder", ex);
            }
        }
        SelectedLora = Loras.Any(l => l.Value == keep) ? keep : string.Empty;
        OnPropertyChanged(nameof(HasLoraDir));
    }

    // ---------------- Превью и входные картинки: настройки ----------------

    public IReadOnlyList<ValueOption> PreviewModes { get; } = new[] { "auto", "tae", "proj", "vae", "none" }
        .Select(v => new ValueOption(v, L.Get("Img_Preview_" + v))).ToList();

    [ObservableProperty] private string _previewMode;

    partial void OnPreviewModeChanged(string value)
    {
        _s.Settings.Current.Images.PreviewMode = string.IsNullOrEmpty(value) ? "auto" : value;
        _s.Settings.Save();
    }

    /// <summary>Уменьшать крупные референсы перед отправкой (защита видеопамяти); выключено — «отправить как есть».</summary>
    [ObservableProperty] private bool _limitInputSize;

    partial void OnLimitInputSizeChanged(bool value)
    {
        _s.Settings.Current.Images.LimitInputSize = value;
        _s.Settings.Save();
        UpdateRefInfo();
    }

    /// <summary>Режим превью для запроса: «авто» — TAE, если сервер запущен с ним, иначе проекция латентов (бесплатно по скорости).</summary>
    private string ResolvePreview()
    {
        var mode = string.IsNullOrEmpty(PreviewMode) ? "auto" : PreviewMode;
        if (mode != "auto") return mode;
        var files = _s.ImageServer.Options?.Files;
        return files != null && files.ContainsKey(ImageComponent.Tae) ? "tae" : "proj";
    }

    // ---------------- Генерация ----------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(CancelCommand), nameof(StopServerCommand), nameof(Final2KCommand))]
    [NotifyPropertyChangedFor(nameof(ShowLivePreview), nameof(ShowCompare), nameof(ShowSingle), nameof(ShowResultTools),
        nameof(ShowPreviewPlaceholder), nameof(PreviewPlaceholderText))]
    private bool _isGenerating;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _lastStats;

    /// <summary>Сколько кодировался промпт с картинками — или что он взят из кэша (зерно и сила меняются за секунды).</summary>
    [ObservableProperty] private string? _conditioningText;

    /// <summary>Шаг слишком медленный для этого размера: подсказка про системную память драйвера NVIDIA или число референсов.</summary>
    [ObservableProperty] private string? _slowHint;

    /// <summary>Свежий кадр превью во время генерации.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLivePreview), nameof(ShowCompare), nameof(ShowSingle), nameof(ShowResultTools), nameof(ShowPreviewPlaceholder))]
    private BitmapSource? _livePreview;

    public bool ShowLivePreview => IsGenerating && LivePreview != null;

    /// <summary>Сервер будет присылать кадры превью в текущей генерации.</summary>
    private bool _livePreviewOn = true;

    /// <summary>Область превью пуста: ни кадра генерации, ни выбранной картинки.</summary>
    public bool ShowPreviewPlaceholder => !ShowLivePreview && !ShowCompare && !ShowSingle;

    public string PreviewPlaceholderText =>
        IsGenerating ? L.Get(_livePreviewOn ? "Img_PreviewWaiting" : "Img_PreviewOff")
            : L.Get(HasSelectedImage ? "Img_ResultPreviewUnavailable" : "Img_NoResults");

    private bool CanGenerate() =>
        !IsGenerating && !IsReleasingMemory && SelectedModel?.IsReady == true && HasEngine &&
        (Mode != ImageMode.Edit || RefImages.Count > 0) && (Mode != ImageMode.ImageToImage || InitImage != null);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync() => RunGenerationAsync(null);

    public bool CanFinal2K => Preset?.NativeEditSide != null;

    private bool CanRunFinal2K() => CanFinal2K && CanGenerate();

    /// <summary>
    /// «Финал в 2K»: то же зерно и промпт, размер с теми же пропорциями при площади 2048². Подбирать правку быстрее в 1K,
    /// а родной размер правки Qwen-Image 2.1 — 2K; плитки VAE включатся сами.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunFinal2K))]
    private Task Final2KAsync()
    {
        if (Preset is not { } p) return Task.CompletedTask;
        var seed = SelectedImage?.Params?.Seed is { } s and >= 0 ? s : long.TryParse(Seed, out var cur) ? cur : -1;
        var (w, h) = ImageInputPolicy.SizeForSource(Width, Height, p.NativeEditSide ?? 2048, p.RoundTo);
        return RunGenerationAsync(g =>
        {
            g.Width = w;
            g.Height = h;
            g.SizeScale = 2;
            if (seed >= 0) g.Seed = seed;
        });
    }

    private async Task RunGenerationAsync(Action<ImageGenParams>? adjust)
    {
        if (SelectedModel == null) return;
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            Error = L.Get("Img_EmptyPrompt");
            return;
        }
        var model = SelectedModel.Model;
        var p = CurrentParams();
        adjust?.Invoke(p);
        // Случайное зерно выбираем сами — чтобы его можно было сохранить и повторить картинку
        if (p.Seed < 0) p.Seed = new Random().Next(0, int.MaxValue);
        if (CheckPromptTags() is { } tagError)
        {
            Error = tagError;
            return;
        }
        _s.Settings.Current.Images.LastParams = p.Clone();
        _s.Settings.Save();

        var cts = _cts = new CancellationTokenSource();
        _resetGeneration = false;
        _livePreviewOn = ResolvePreview() != "none";
        IsGenerating = true;
        _s.ImageBusy = true;
        _nativeProgress = false;
        Status = null;
        Error = null;
        LastStats = null;
        ConditioningText = null;
        SlowHint = null;
        LivePreview = null;
        ProgressValue = 0;
        ProgressIndeterminate = true;
        ProgressText = null;
        _startedAt = DateTime.Now;
        UpdateElapsed();
        _clock.Start();
        long outputMark = -1;
        DateTime? sessionStarted = null;
        var failure = ImageFailureKind.Unknown;
        try
        {
            await RememberPromptAsync(p, cts.Token);
            var previousSession = _s.ImageServer.StartedAt;
            if (!(_s.ImageServer.State == ServerState.Ready && _s.ImageServer.Model?.Id == model.Id))
                Status = L.F("Img_LoadingModel", model.DisplayName);
            var ok = await Task.Run(() => _s.EnsureImageServerAsync(model, cts.Token, forGeneration: true), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ok)
            {
                if (_s.ImageServer.State == ServerState.Failed && _s.ImageServer.StartedAt != previousSession)
                {
                    sessionStarted = _s.ImageServer.StartedAt;
                    failure = ImageFailures.Classify(_s.ImageServer.TailSince(0));
                }
                Status = null;
                Error = L.F("Img_ServerFailed", _s.ImageServerError ?? "?") + FailureHint(failure);
                return;
            }
            sessionStarted = _s.ImageServer.StartedAt;
            Status = L.Get("Img_Preparing");
            _s.ImageServer.ResetProgress();
            _startedAt = DateTime.Now;

            var inputs = SnapshotInputs(p);
            var request = await Task.Run(() => BuildRequest(p, inputs), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            request.Preview = ResolvePreview();
            request.PreviewInterval = Math.Max(1, _s.Settings.Current.Images.PreviewInterval);

            // Кэш кондиционирования: тот же промпт и те же байты картинок для той же модели не кодируются заново
            _condCache.Capacity = _s.ImageServer.Options?.ConditioningCacheSize ?? 4;
            if (_s.ImageServer.Options?.WeightResidencyTtl is { } ttl && _lastConditioningUse is { } used
                && DateTime.UtcNow - used >= TimeSpan.FromSeconds(ttl))
                _condCache.Clear();
            var session = $"{_s.ImageServer.Port}|{_s.ImageServer.StartedAt?.Ticks}|{model.Id}";
            var condKey = ConditioningCacheTracker.Key(session, ImageClient.EffectivePrompt(p), p.NegativePrompt,
                request.RefImages.Concat(request.PromptImages.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)));
            var expectCached = _condCache.Contains(condKey);

            Status = L.Get("Img_Generating");
            var client = new ImageClient(_s.LocalHttp, _s.ImageServer.BaseUrl);
            var progress = new Progress<ImageJobProgress>(j =>
            {
                if (_cts == cts && !cts.IsCancellationRequested) OnJobProgress(j, p);
            });
            outputMark = _s.ImageServer.LineMark;
            var result = await Task.Run(() => client.GenerateAsync(request, cts.Token, progress));
            _condCache.Touch(condKey);
            _lastConditioningUse = DateTime.UtcNow;

            var (saved, before) = await Task.Run(() =>
            {
                var items = Save(result, p, model, request);
                return (items, ComparisonPreview(request, inputs));
            });
            foreach (var item in saved)
            {
                item.Before = before;
                Gallery.Insert(0, item);
            }
            while (Gallery.Count > GalleryLimit) Gallery.RemoveAt(Gallery.Count - 1);
            OnPropertyChanged(nameof(HasGallery));
            SelectedImage = saved.FirstOrDefault();
            IsComparing = before != null;
            LastStats = L.F("Img_Stats", result.Images.Count, result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture),
                p.Seed.ToString(CultureInfo.InvariantCulture), result.Endpoint);
            ConditioningText = DescribeConditioning(result, expectCached, request);
            if (result.SecondsPerStep is { } spi && ImageInputPolicy.IsSlowStep(spi, p.Width, p.Height)) SlowHint = SlowStepText();
            if (!_resetGeneration) Status = null;
            Log.Info("Images", "Generated", new
            {
                count = result.Images.Count, ms = (long)result.Elapsed.TotalMilliseconds, result.Endpoint, family = model.Family, mode = p.Mode,
                refs = request.RefImages.Count, promptImages = request.PromptImages.Count, mask = request.MaskImage != null, tiling = request.VaeTiling,
                condMs = (long?)result.ConditioningTime?.TotalMilliseconds, spi = result.SecondsPerStep,
            });
        }
        catch (OperationCanceledException)
        {
            if (!_resetGeneration) Status = L.Get("Img_Canceled");
        }
        catch (Exception ex) when (ex is ImageApiException or System.Net.Http.HttpRequestException or IOException or TimeoutException or JsonException
                                       or NotSupportedException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            if (cts.IsCancellationRequested)
            {
                if (!_resetGeneration) Status = L.Get("Img_Canceled");
            }
            else
            {
                failure = ex is OutOfMemoryException ? ImageFailureKind.RamExhausted
                    : outputMark >= 0 ? ImageFailures.Classify(_s.ImageServer.TailSince(outputMark)) : ImageFailureKind.Unknown;
                Error = L.F("Img_Failed", ex.GetBaseException().Message) + FailureHint(failure);
                Status = null;
                Log.Warn("Images", "Generation failed", ex, new { kind = failure });
            }
        }
        finally
        {
            _clock.Stop();
            ElapsedText = null;
            LivePreview = null;
            if (sessionStarted is { } started && !_resetGeneration)
            {
                var reclaimed = await Task.Run(() => _s.ReclaimImageMemoryAsync(started, failure, cts.IsCancellationRequested, CancellationToken.None));
                if (reclaimed)
                {
                    _condCache.Clear();
                    if (!_resetGeneration && !cts.IsCancellationRequested)
                        Status = L.Get(failure == ImageFailureKind.RamExhausted ? "Img_MemoryRecoveryReady" : "Img_MemoryReleased");
                }
            }
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

    private string? DescribeConditioning(ImageResult result, bool expectCached, ImageRequest request)
    {
        if (result.Endpoint != "/sdcpp/v1/img_gen") return null;
        var hasImages = request.RefImages.Count + request.PromptImages.Count > 0;
        if (result.ConditioningTime is { } t && t.TotalSeconds >= 0.3)
            return L.F(hasImages ? "Img_CondTimeImages" : "Img_CondTime", t.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture));
        return expectCached || result.ConditioningTime == null ? L.Get(hasImages ? "Img_CondCachedImages" : "Img_CondCached") : null;
    }

    private string SlowStepText() =>
        L.Get(_s.ImageEngines.Active?.Manifest.Backend == EngineBackend.Cuda ? "Img_SlowStepCuda" : "Img_SlowStep");

    /// <summary>Что делать, если серверу не хватило памяти: сам он сообщает лишь «prompt encoding failed».</summary>
    private static string FailureHint(ImageFailureKind kind)
    {
        switch (kind)
        {
            case ImageFailureKind.RamExhausted:
                var free = HardwareProbe.Memory().available / (double)(1L << 30);
                return Environment.NewLine + L.F("Img_FailRam", free.ToString("0.0", CultureInfo.CurrentCulture));
            case ImageFailureKind.VramExhausted:
                return Environment.NewLine + L.Get("Img_FailVram");
            default:
                return string.Empty;
        }
    }

    private bool CanCancel() => IsGenerating && !IsReleasingMemory;

    /// <summary>Отмена: родной API прерывает генерацию на сервере на ближайшем шаге; у старых сборок ответ просто перестаём ждать.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        Status = L.Get("Img_Canceling");
    }

    /// <summary>Ход по выводу сервера — только для старых сборок без /sdcpp (у родного API своя, более точная стадия).</summary>
    private void OnProgress(ImageProgress p)
    {
        if (!IsGenerating || _nativeProgress || _cts?.IsCancellationRequested == true) return;
        ProgressIndeterminate = false;
        ProgressValue = 100.0 * p.Step / p.Total;
        var eta = p.SecondsPerStep is { } spi ? TimeSpan.FromSeconds(spi * (p.Total - p.Step)) : (TimeSpan?)null;
        ProgressText = L.F("Img_Progress", p.Step, p.Total,
            p.SecondsPerStep?.ToString("0.00", CultureInfo.CurrentCulture) ?? "?",
            eta is { } e ? Math.Ceiling(e.TotalSeconds).ToString("0", CultureInfo.CurrentCulture) : "?");
        Status = L.Get("Img_Generating");
    }

    private void OnJobProgress(ImageJobProgress j, ImageGenParams p)
    {
        if (!IsGenerating) return;
        _nativeProgress = true;
        if (j.Preview != null) LivePreview = ImagePrep.Thumbnail(j.Preview, 768) ?? LivePreview;
        if (_cts?.IsCancellationRequested == true) return;

        var stage = L.TryGet("ImgStage_" + j.Stage) ?? j.Stage;
        if (j.Status == "queued" && j.QueuePosition > 0)
        {
            Status = L.F("Img_Queued", j.QueuePosition);
            ProgressIndeterminate = true;
            return;
        }
        Status = stage.Length > 0 ? stage : L.Get("Img_Generating");
        if (j.Stage == "sampling" && j.Total is > 0 && j.Completed is { } done)
        {
            ProgressIndeterminate = false;
            ProgressValue = 100.0 * done / j.Total.Value;
            var spi = done > 0 ? j.StageElapsedMs / 1000.0 / done : (double?)null;
            var eta = spi is { } v ? v * (j.Total.Value - done) : (double?)null;
            ProgressText = L.F("Img_Progress", done, j.Total.Value,
                spi?.ToString("0.00", CultureInfo.CurrentCulture) ?? "?",
                eta is { } e ? Math.Ceiling(e).ToString("0", CultureInfo.CurrentCulture) : "?")
                + (j.Pass > 1 ? " · " + L.F("Img_Pass", j.Pass) : string.Empty);
            if (spi is { } s && done >= 2 && SlowHint == null && ImageInputPolicy.IsSlowStep(s, p.Width, p.Height)) SlowHint = SlowStepText();
        }
        else
        {
            ProgressIndeterminate = true;
            ProgressText = j.StageElapsedMs >= 1000 ? L.F("Img_StageElapsed", stage, (j.StageElapsedMs / 1000.0).ToString("0", CultureInfo.CurrentCulture)) : null;
        }
    }

    [ObservableProperty] private string? _elapsedText;

    private void UpdateElapsed()
    {
        if (!IsGenerating) return;
        ElapsedText = L.F("Img_Elapsed", (DateTime.Now - _startedAt).TotalSeconds.ToString("0", CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// PNG + .json с параметрами: images/гггг-мм-дд/ччммсс-зерно-N.png. Те же параметры пишутся и внутрь PNG (tEXt):
    /// перетащите картинку обратно — они восстановятся, даже без .json рядом.
    /// </summary>
    private List<GalleryItem> Save(ImageResult result, ImageGenParams p, ImageModel model, ImageRequest request)
    {
        var dir = System.IO.Path.Combine(_s.ImagesFolder, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var list = new List<GalleryItem>();
        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        for (var i = 0; i < result.Images.Count; i++)
        {
            var path = System.IO.Path.Combine(dir, $"{stamp}-{p.Seed}-{i + 1}.png");
            var meta = new ImageSidecar
            {
                Params = p,
                Model = model.DisplayName,
                Seconds = result.Elapsed.TotalSeconds,
                Endpoint = result.Endpoint,
                RefImages = request.RefImages.Count,
                Mask = request.MaskImage != null,
                ConditioningSeconds = result.ConditioningTime?.TotalSeconds,
                SecondsPerStep = result.SecondsPerStep,
                GenerationInfo = i < result.GenerationInfo.Count ? result.GenerationInfo[i] : null,
            };
            var bytes = result.Images[i];
            try
            {
                var json = JsonConvert.SerializeObject(new ImageSidecar { Params = p, Model = meta.Model }, new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii });
                bytes = PngMeta.WithText(bytes, json);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                Log.Warn("Images", "Cannot embed parameters", ex);
            }
            File.WriteAllBytes(path, bytes);
            Core.Storage.JsonFile.WriteAtomic(System.IO.Path.ChangeExtension(path, ".json"), meta);
            list.Add(new GalleryItem(path, LoadThumb(path), p, null));
        }
        return list;
    }

    // ---------------- Галерея ----------------

    public ObservableCollection<GalleryItem> Gallery { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedImage), nameof(CanCompare), nameof(ShowCompare), nameof(ShowSingle), nameof(ShowResultTools),
        nameof(ShowPreviewPlaceholder), nameof(PreviewPlaceholderText))]
    private GalleryItem? _selectedImage;

    public bool HasSelectedImage => SelectedImage != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompare), nameof(ShowSingle), nameof(ShowPreviewPlaceholder))]
    private BitmapSource? _selectedPreview;

    public bool HasGallery => Gallery.Count > 0;

    /// <summary>Сравнение «до / после» доступно для правок этого сеанса.</summary>
    public bool CanCompare => SelectedImage?.Before != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCompare), nameof(ShowSingle), nameof(ShowPreviewPlaceholder))]
    private bool _isComparing;

    public bool ShowCompare => SelectedPreview != null && ComparingActive && !ShowLivePreview;
    public bool ShowSingle => SelectedPreview != null && !ComparingActive && !ShowLivePreview;
    public bool ShowResultTools => HasSelectedImage && !ShowLivePreview;
    private bool ComparingActive => IsComparing && CanCompare;

    public override void OnNavigatedTo()
    {
        RefreshModels();
        RefreshLoras();
        _ = EnsurePromptHistoryLoadedAsync();
        if (Gallery.Count == 0 && !_loadingGallery) _ = LoadGalleryAsync();
    }

    private async Task LoadGalleryAsync()
    {
        _loadingGallery = true;
        try
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
            }, CancellationToken.None);
            var paths = new HashSet<string>(Gallery.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (Gallery.Count >= GalleryLimit) break;
                if (paths.Add(item.Path)) Gallery.Add(item);
            }
            OnPropertyChanged(nameof(HasGallery));

            await EnsurePromptHistoryLoadedAsync();
            PromptHistory = ImagePromptHistory.Recent(PromptHistory.Concat(items.Select(item => ImagePromptEntry.FromParams(item.Params))));
        }
        finally
        {
            _loadingGallery = false;
        }
    }

    /// <summary>Параметры картинки: из соседнего .json, иначе из tEXt-чанка внутри PNG.</summary>
    private static ImageSidecar? ReadSidecar(string png)
    {
        try
        {
            var json = System.IO.Path.ChangeExtension(png, ".json");
            if (File.Exists(json)) return Core.Storage.JsonFile.Read<ImageSidecar>(json);
            using var fs = File.OpenRead(png);
            var head = new byte[Math.Min(fs.Length, 4L << 20)];
            var read = fs.Read(head, 0, head.Length);
            var text = PngMeta.ReadText(read == head.Length ? head : head.Take(read).ToArray());
            return text == null ? null : JsonConvert.DeserializeObject<ImageSidecar>(text);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
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
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException
                                       or FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException)
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

    /// <summary>Продолжить правку: готовая картинка становится референсом (или исходником, если модель правку не умеет).</summary>
    [RelayCommand]
    private void UseAsRef(GalleryItem? item)
    {
        if (item == null) return;
        if (CanEdit)
        {
            Mode = ImageMode.Edit;
            RefImages.Clear();
        }
        else if (Modes.Contains(ImageMode.ImageToImage))
        {
            Mode = ImageMode.ImageToImage;
        }
        _ = AddFilesAsync(new[] { item.Path }, restoreParams: false);
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

    partial void OnSelectedImageChanged(GalleryItem? value)
    {
        _previewLoad?.Cancel();
        _previewLoad = null;
        SelectedPreview = null;
        OnPropertyChanged(nameof(HasGallery));
        if (value != null) _ = LoadSelectedPreviewAsync(value);
    }

    private async Task LoadSelectedPreviewAsync(GalleryItem item)
    {
        using var load = new CancellationTokenSource();
        _previewLoad = load;
        try
        {
            var preview = await Task.Run(() => LoadThumb(item.Path, 1024), load.Token);
            if (!load.IsCancellationRequested) SelectedPreview = preview ?? item.Thumb;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally
        {
            if (_previewLoad == load) _previewLoad = null;
        }
    }

    // ---------------- Параметры из перетащенной картинки ----------------

    /// <summary>Параметры, найденные в перетащенном PNG (внутри или в .json рядом) — можно восстановить одной кнопкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDroppedParams))]
    private ImageGenParams? _droppedParams;

    public bool HasDroppedParams => DroppedParams != null;

    [RelayCommand]
    private void RestoreDroppedParams()
    {
        if (DroppedParams is not { } p) return;
        LoadParams(p);
        Seed = p.Seed >= 0 ? p.Seed.ToString(CultureInfo.InvariantCulture) : string.Empty;
        DroppedParams = null;
        Status = L.Get("Img_ParamsRestored");
    }

    [RelayCommand]
    private void DismissDroppedParams() => DroppedParams = null;
}
