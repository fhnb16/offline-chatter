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
        GpuLayers = S(value.GpuLayers);
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
        if (e.PropertyName is nameof(Preview) or nameof(EstimateText) or nameof(Verdict) or nameof(SkippedText) or nameof(Selected)
            or nameof(CompareWith) or nameof(IsDefaultForAll) or nameof(ActiveModelName))
            return;
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
