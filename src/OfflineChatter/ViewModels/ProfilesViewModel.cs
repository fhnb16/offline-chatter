using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Сколько слоёв модели держать в видеопамяти.</summary>
public enum GpuOffloadMode
{
    /// <summary>llama.cpp подбирает сам (--fit) или по оценке программы.</summary>
    Auto,
    /// <summary>Все слои на GPU (-ngl all / 999).</summary>
    All,
    /// <summary>Часть слоёв на GPU, остальные на CPU.</summary>
    Partial,
    /// <summary>Только CPU (-ngl 0).</summary>
    CpuOnly,
}

public sealed record GpuRatioOption(int Percent)
{
    public string Title => Percent.ToString(CultureInfo.CurrentCulture) + " %";
}

/// <summary>Редактор профилей запуска: параметры, превью командной строки, оценка памяти, сравнение, запуск.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _previewTimer;
    private bool _loading;

    public ProfilesViewModel(AppServices services)
    {
        _s = services;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            await UpdatePreviewAsync();
        };
        Reload();
        _s.Engine.Server.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => StartCommand.NotifyCanExecuteChanged()));
    }

    public ObservableCollection<LaunchProfile> Profiles { get; } = new();
    public ObservableCollection<ProfileDifference> Differences { get; } = new();

    public IReadOnlyList<string> FlashAttentionOptions { get; } = new[] { "auto", "on", "off" };
    public IReadOnlyList<string> CacheTypes { get; } = new[] { "", "f16", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0", "f32" };
    public IReadOnlyList<string> ReasoningFormats { get; } = new[] { "deepseek", "none", "auto" };
    public IReadOnlyList<int> ContextOptions => MemoryEstimator.ContextPresets;

    [ObservableProperty] private LaunchProfile? _selected;
    [ObservableProperty] private LaunchProfile? _compareWith;

    // ----- редактируемые поля (строки: пусто = «авто») -----
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _contextSize = string.Empty;
    [ObservableProperty] private string _gpuLayers = string.Empty;
    [ObservableProperty] private string _threads = string.Empty;
    [ObservableProperty] private string _batchSize = string.Empty;
    [ObservableProperty] private string _uBatchSize = string.Empty;
    [ObservableProperty] private string _parallel = "1";
    [ObservableProperty] private string _flashAttention = "auto";
    [ObservableProperty] private string _cacheTypeK = string.Empty;
    [ObservableProperty] private string _cacheTypeV = string.Empty;
    [ObservableProperty] private bool _mlock;
    [ObservableProperty] private bool _noMmap;
    [ObservableProperty] private bool _useFit = true;
    [ObservableProperty] private string _reasoningFormat = "deepseek";
    [ObservableProperty] private string _draftModelPath = string.Empty;
    [ObservableProperty] private string _extraArgs = string.Empty;
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private double _topP = 0.95;
    [ObservableProperty] private int _topK = 40;
    [ObservableProperty] private double _minP = 0.05;
    [ObservableProperty] private double _repeatPenalty = 1.0;
    [ObservableProperty] private int _maxTokens = -1;

    [ObservableProperty] private string? _preview;
    [ObservableProperty] private string? _estimateText;
    [ObservableProperty] private FitVerdict _verdict = FitVerdict.Fits;
    [ObservableProperty] private string? _skippedText;
    [ObservableProperty] private bool _isDefaultForAll;

    public string ActiveModelName => _s.ActiveModel?.DisplayName ?? L.Get("Model_NotSelected");

    // ----- GPU: слои в видеопамяти -----
    private HardwareInfo? _hw;
    private int? _blockCount;
    private long _modelSize;

    public IReadOnlyList<GpuOffloadMode> GpuModes { get; } = Enum.GetValues(typeof(GpuOffloadMode)).Cast<GpuOffloadMode>().ToList();
    public IReadOnlyList<GpuRatioOption> GpuRatios { get; } = new[] { new GpuRatioOption(25), new GpuRatioOption(50), new GpuRatioOption(75) };

    [ObservableProperty] private GpuOffloadMode _gpuMode;
    [ObservableProperty] private bool _strictGpu;

    public bool IsGpuEngine => _s.Engine.Registry.Active is not { Manifest.Backend: EngineBackend.Cpu };
    public bool IsAutoMode => GpuMode == GpuOffloadMode.Auto;
    public bool IsAllMode => GpuMode == GpuOffloadMode.All;
    public bool IsPartialMode => GpuMode == GpuOffloadMode.Partial;

    /// <summary>Слоёв, которые llama.cpp может выгрузить: блоки модели + выходной слой.</summary>
    public int LayerCount => _blockCount is { } b ? b + 1 : Math.Max(ParsedGpuLayers ?? 0, 100);
    public bool HasLayerCount => _blockCount != null;

    private int? ParsedGpuLayers => int.TryParse(GpuLayers.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public double GpuLayersSlider
    {
        get => ParsedGpuLayers is { } n and > 0 ? n : 1;
        set => GpuLayers = Math.Max(1, (int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
    }

    public string GpuLayersText
    {
        get
        {
            var n = ParsedGpuLayers is { } v and > 0 ? v : 1;
            if (!HasLayerCount) return L.F("Profiles_GpuLayersN", n);
            var pct = (int)Math.Round(100.0 * Math.Min(n, LayerCount) / LayerCount);
            return L.F("Profiles_GpuLayersOf", n, LayerCount, pct);
        }
    }

    public string GpuModeHint
    {
        get
        {
            if (!IsGpuEngine) return L.Get("Profiles_GpuHint_CpuEngine");
            var hint = L.Get("Profiles_GpuHint_" + GpuMode);
            if (GpuMode == GpuOffloadMode.Partial && _blockCount is { } b && b > 0 && _modelSize > 0)
                hint += " " + L.F("Profiles_GpuPerLayer", Units.FormatBytes(_modelSize / (b + 1)));
            return hint;
        }
    }

    public bool ShowStrictHint => IsAllMode && StrictGpu;

    partial void OnGpuModeChanged(GpuOffloadMode value)
    {
        OnPropertyChanged(nameof(IsAutoMode));
        OnPropertyChanged(nameof(IsAllMode));
        OnPropertyChanged(nameof(IsPartialMode));
        OnPropertyChanged(nameof(GpuModeHint));
        OnPropertyChanged(nameof(ShowStrictHint));
        if (_loading) return;
        GpuLayers = value switch
        {
            GpuOffloadMode.Auto => string.Empty,
            GpuOffloadMode.All => "-1",
            GpuOffloadMode.CpuOnly => "0",
            _ => ParsedGpuLayers is { } n and > 0 && n < LayerCount ? GpuLayers : Math.Max(1, LayerCount / 2).ToString(CultureInfo.InvariantCulture),
        };
    }

    partial void OnStrictGpuChanged(bool value) => OnPropertyChanged(nameof(ShowStrictHint));

    partial void OnGpuLayersChanged(string value)
    {
        OnPropertyChanged(nameof(GpuLayersSlider));
        OnPropertyChanged(nameof(GpuLayersText));
    }

    [RelayCommand]
    private void SetGpuRatio(GpuRatioOption? ratio)
    {
        if (ratio == null) return;
        GpuMode = GpuOffloadMode.Partial;
        var n = (int)Math.Round(LayerCount * ratio.Percent / 100.0);
        GpuLayers = Math.Max(1, Math.Min(LayerCount - 1, n)).ToString(CultureInfo.InvariantCulture);
    }

    private static GpuOffloadMode ModeOf(int? ngl) => ngl switch
    {
        null => GpuOffloadMode.Auto,
        < 0 or >= 99 => GpuOffloadMode.All,
        0 => GpuOffloadMode.CpuOnly,
        _ => GpuOffloadMode.Partial,
    };

    // ----- CPU: потоки -----
    public int MaxThreads => _hw is { LogicalCores: > 0 } h ? h.LogicalCores : Environment.ProcessorCount;
    public int PhysicalCores => _hw is { PhysicalCores: > 0 } h ? h.PhysicalCores : MaxThreads;

    private int? ParsedThreads => int.TryParse(Threads.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    /// <summary>0 — авто (по числу физических ядер).</summary>
    public double ThreadsSlider
    {
        get => ParsedThreads ?? 0;
        set => Threads = value < 0.5 ? string.Empty : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
    }

    public string ThreadsText => ParsedThreads is { } n ? L.F("Profiles_ThreadsOf", n, MaxThreads) : L.F("Profiles_ThreadsAuto", PhysicalCores);

    public string? ThreadsWarning => ParsedThreads is { } n && n > PhysicalCores ? L.F("Profiles_ThreadsOverPhysical", PhysicalCores) : null;

    partial void OnThreadsChanged(string value)
    {
        OnPropertyChanged(nameof(ThreadsSlider));
        OnPropertyChanged(nameof(ThreadsText));
        OnPropertyChanged(nameof(ThreadsWarning));
    }

    /// <summary>Свойства только для показа: их изменение не сохраняет профиль.</summary>
    private static readonly HashSet<string> DisplayOnly = new(StringComparer.Ordinal)
    {
        nameof(Preview), nameof(EstimateText), nameof(Verdict), nameof(SkippedText), nameof(Selected), nameof(CompareWith),
        nameof(IsDefaultForAll), nameof(ActiveModelName), nameof(IsGpuEngine), nameof(IsAutoMode), nameof(IsAllMode),
        nameof(IsPartialMode), nameof(LayerCount), nameof(HasLayerCount), nameof(GpuLayersSlider), nameof(GpuLayersText),
        nameof(GpuModeHint), nameof(ShowStrictHint), nameof(MaxThreads), nameof(PhysicalCores), nameof(ThreadsSlider),
        nameof(ThreadsText), nameof(ThreadsWarning), nameof(GpuMode),
    };

    private void Reload(string? selectId = null)
    {
        Profiles.Clear();
        foreach (var p in _s.Profiles.List()) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault(p => p.Id == (selectId ?? _s.Settings.Current.Models.DefaultProfileId)) ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedChanged(LaunchProfile? value)
    {
        if (value == null) return;
        _loading = true;
        string S(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Name = value.Name;
        ContextSize = S(value.ContextSize);
        GpuMode = ModeOf(value.GpuLayers);
        // Старые профили с 99 / 999 — это «все слои»
        GpuLayers = GpuMode == GpuOffloadMode.All ? "-1" : S(value.GpuLayers);
        StrictGpu = value.StrictGpu;
        Threads = S(value.Threads);
        BatchSize = S(value.BatchSize);
        UBatchSize = S(value.UBatchSize);
        Parallel = S(value.Parallel);
        FlashAttention = value.FlashAttention;
        CacheTypeK = value.CacheTypeK ?? string.Empty;
        CacheTypeV = value.CacheTypeV ?? string.Empty;
        Mlock = value.Mlock;
        NoMmap = value.NoMmap;
        UseFit = value.UseFit;
        ReasoningFormat = value.ReasoningFormat ?? "none";
        DraftModelPath = value.DraftModelPath ?? string.Empty;
        ExtraArgs = value.ExtraArgs ?? string.Empty;
        Temperature = value.Sampling.Temperature;
        TopP = value.Sampling.TopP;
        TopK = value.Sampling.TopK;
        MinP = value.Sampling.MinP;
        RepeatPenalty = value.Sampling.RepeatPenalty;
        MaxTokens = value.Sampling.MaxTokens;
        IsDefaultForAll = _s.Settings.Current.Models.DefaultProfileId == value.Id;
        _loading = false;
        RefreshDiff();
        SchedulePreview();
    }

    partial void OnCompareWithChanged(LaunchProfile? value) => RefreshDiff();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || Selected == null) return;
        if (e.PropertyName == null || DisplayOnly.Contains(e.PropertyName)) return;
        Apply(Selected);
        _s.Profiles.Save(Selected);
        RefreshDiff();
        SchedulePreview();
    }

    partial void OnIsDefaultForAllChanged(bool value)
    {
        if (_loading || Selected == null || !value) return;
        _s.Settings.Current.Models.DefaultProfileId = Selected.Id;
        _s.Settings.Save();
    }

    private void Apply(LaunchProfile p)
    {
        static int? I(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        p.Name = string.IsNullOrWhiteSpace(Name) ? p.Name : Name.Trim();
        p.ContextSize = I(ContextSize);
        p.GpuLayers = I(GpuLayers);
        p.StrictGpu = StrictGpu && GpuMode == GpuOffloadMode.All;
        p.Threads = I(Threads);
        p.BatchSize = I(BatchSize);
        p.UBatchSize = I(UBatchSize);
        p.Parallel = I(Parallel);
        p.FlashAttention = FlashAttention;
        p.CacheTypeK = string.IsNullOrEmpty(CacheTypeK) ? null : CacheTypeK;
        p.CacheTypeV = string.IsNullOrEmpty(CacheTypeV) ? null : CacheTypeV;
        p.Mlock = Mlock;
        p.NoMmap = NoMmap;
        p.UseFit = UseFit;
        p.ReasoningFormat = ReasoningFormat == "none" ? null : ReasoningFormat;
        p.DraftModelPath = string.IsNullOrWhiteSpace(DraftModelPath) ? null : DraftModelPath.Trim();
        p.ExtraArgs = string.IsNullOrWhiteSpace(ExtraArgs) ? null : ExtraArgs.Trim();
        p.Sampling.Temperature = Temperature;
        p.Sampling.TopP = TopP;
        p.Sampling.TopK = TopK;
        p.Sampling.MinP = MinP;
        p.Sampling.RepeatPenalty = RepeatPenalty;
        p.Sampling.MaxTokens = MaxTokens;
    }

    private void RefreshDiff()
    {
        Differences.Clear();
        if (Selected == null || CompareWith == null || CompareWith.Id == Selected.Id) return;
        foreach (var d in ProfileDiff.Compare(Selected, CompareWith)) Differences.Add(d);
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    public async Task UpdatePreviewAsync()
    {
        OnPropertyChanged(nameof(ActiveModelName));
        if (_hw == null)
        {
            _hw = await _s.Engine.GetHardwareAsync();
            OnPropertyChanged(nameof(MaxThreads));
            OnPropertyChanged(nameof(PhysicalCores));
            OnThreadsChanged(Threads);
        }
        OnPropertyChanged(nameof(IsGpuEngine));
        OnPropertyChanged(nameof(GpuModeHint));
        var model = _s.ActiveModel;
        if (Selected == null || model == null || _s.Engine.Registry.Active == null)
        {
            Preview = L.Get(model == null ? "Profiles_NoModel" : "Profiles_NoEngine");
            EstimateText = null;
            SkippedText = null;
            return;
        }
        var resolved = await _s.ResolveLaunchAsync(model, Selected);
        if (resolved == null)
        {
            Preview = L.Get("Profiles_NoModel");
            return;
        }
        if (_blockCount != resolved.Info?.BlockCount || _modelSize != model.SizeBytes)
        {
            _blockCount = resolved.Info?.BlockCount;
            _modelSize = model.SizeBytes;
            OnPropertyChanged(nameof(LayerCount));
            OnPropertyChanged(nameof(HasLayerCount));
            OnPropertyChanged(nameof(GpuModeHint));
            OnGpuLayersChanged(GpuLayers);
        }
        var flags = await _s.Engine.GetFlagsAsync(_s.Engine.Registry.Active);
        var skipped = new List<string>();
        var args = resolved.Options.BuildArgs("127.0.0.1", 8080, null, flags, skipped);
        Preview = "llama-server.exe " + ServerLaunchOptions.JoinCommandLine(args);
        SkippedText = skipped.Count > 0 ? L.F("Profiles_Skipped", string.Join(", ", skipped)) : null;
        var e = resolved.Estimate;
        Verdict = resolved.Verdict;
        EstimateText = L.F("Profiles_Estimate", Units.FormatBytes(e.Total), Units.FormatBytes(e.Weights), Units.FormatBytes(e.KvCache),
                           Units.FormatBytes(e.Vram), resolved.Options.ContextSize ?? 0) +
                       " · " + L.Get("Enum_FitVerdict_" + resolved.Verdict) +
                       (resolved.Notes.Count > 0 ? " (" + string.Join("; ", resolved.Notes) + ")" : string.Empty);
    }

    [RelayCommand]
    private void CopyAsBat()
    {
        var engine = _s.Engine.Registry.Active;
        if (Preview == null || engine == null || !Preview.StartsWith("llama-server", StringComparison.Ordinal)) return;
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem Offline Chatter — " + (Selected?.Name ?? string.Empty));
        sb.AppendLine("cd /d " + ServerLaunchOptions.Quote(engine.Directory));
        sb.AppendLine(Preview);
        sb.AppendLine("pause");
        Clipboard.SetText(sb.ToString());
    }

    [RelayCommand]
    private void SaveAsNew()
    {
        if (Selected == null) return;
        var copy = Selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        copy.BuiltIn = false;
        copy.Name = Selected.Name + " (" + L.Get("Profiles_Copy") + ")";
        _s.Profiles.Save(copy);
        Reload(copy.Id);
    }

    [RelayCommand]
    private void ResetOrDelete()
    {
        if (Selected == null) return;
        if (Selected.BuiltIn)
        {
            _s.Profiles.Reset(Selected.Id);
            Reload(Selected.Id);
        }
        else
        {
            _s.Profiles.Delete(Selected);
            Reload();
        }
    }

    private bool CanStart() => _s.ActiveModel != null && _s.Engine.Registry.Active != null &&
                               _s.Engine.Server.State is not (ServerState.Starting or ServerState.Loading);

    /// <summary>Запуск или перезапуск сервера с активной моделью и этим профилем.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var model = _s.ActiveModel;
        if (model == null || Selected == null) return;
        model.ProfileId = Selected.Id;
        _s.Models.AddOrUpdate(model);
        _s.Engine.Server.Stop();
        await _s.EnsureServerAsync(model, CancellationToken.None);
    }
}
