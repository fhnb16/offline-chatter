using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Images;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed partial class SdEngineItem : ObservableObject
{
    public SdEngineItem(SdEngineInstall install, bool isActive)
    {
        Install = install;
        _isActive = isActive;
    }

    public SdEngineInstall Install { get; }
    public string Title => Install.DisplayName;
    public string Path => Install.Directory;
    public string Kind => L.Get(Install.IsExternal ? "Engine_External" : "Engine_Downloaded");

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _health;
    [ObservableProperty] private bool _healthOk = true;
}

/// <summary>Строка «параметр пресета — значение» (что программа передаст, если поле оставить «авто»).</summary>
public sealed record PresetFact(string Title, string Value);

/// <summary>
/// Вкладка «Изображения» раздела «Движок»: сборки stable-diffusion.cpp, параметры запуска sd-server с пресетами
/// семейств моделей, вывод сервера и публичный OpenAI-совместимый API изображений.
/// </summary>
public sealed partial class ImageEngineViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private readonly ImageSettings _i;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _logTimer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLines = new();
    private HardwareInfo? _hw;
    private CancellationTokenSource? _installCts;
    private bool _loading;

    public ImageEngineViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        _i = services.Settings.Current.Images;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            await UpdatePreviewAsync();
        };
        _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _logTimer.Tick += (_, _) => DrainLog();
        _logTimer.Start();
        _s.ImageServer.OutputLine += (_, line) => _pendingLines.Enqueue(line);
        _s.ImageServer.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(OnServerChanged));
        _s.ImageSetupChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshInstalled();
            RefreshModel();
        }));
        _s.ImageModels.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshModel));
        _s.ImageApiChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseApi));

        LoadLaunch();
        _s.EnsureImageApiKey();
        _apiKey = Secrets.UnprotectOrEmpty(_i.ApiKeyProtected);
        _exposeApi = _i.ExposeApi;
        _port = (_i.Port > 0 ? _i.Port : ImageSettings.DefaultPort).ToString(CultureInfo.InvariantCulture);
        ListenOptions = BuildListenOptions(_i.ListenAddress);
        _listen = ListenOptions.FirstOrDefault(o => o.Address == _i.ListenAddress) ?? ListenOptions[0];
        foreach (var l in _s.ImageServer.Tail.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)) ServerLines.Add(l);
        RefreshInstalled();
        RefreshModel();
        _constructed = true;
    }

    private readonly bool _constructed;

    public MainViewModel Main => _main;

    public void OnShown()
    {
        _ = LoadHardwareAsync();
        if (Releases.Count == 0 && !IsLoadingReleases) _ = RefreshReleasesAsync();
        RefreshModel();
        SchedulePreview();
    }

    // =============== Сборки ===============

    public ObservableCollection<SdEngineItem> Installed { get; } = new();
    public ObservableCollection<EngineRelease> Releases { get; } = new();
    public ObservableCollection<VariantItem> Variants { get; } = new();

    public bool HasInstalled => Installed.Count > 0;

    [ObservableProperty] private string? _releasesError;
    [ObservableProperty] private string? _installStatus;
    [ObservableProperty] private bool _showAllVariants;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshReleasesCommand))]
    private bool _isLoadingReleases;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(InstallRecommendedCommand))]
    private bool _isInstalling;

    [ObservableProperty] private EngineRelease? _selectedRelease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private VariantItem? _selectedVariant;

    /// <summary>Рекомендованный вариант последнего релиза — для кнопки «Установить» одним нажатием.</summary>
    public VariantItem? Recommended => Variants.FirstOrDefault(v => v.Recommended);

    public string? RecommendedText => Recommended is { } r && SelectedRelease is { } rel ? L.F("ImgEngine_RecommendedText", rel.Tag, r.Title) : null;

    private async Task LoadHardwareAsync()
    {
        _hw ??= await _s.Engine.GetHardwareAsync();
        OnPropertyChanged(nameof(VramBudgetText));
        OnPropertyChanged(nameof(ShowNvidiaHint));
        RebuildVariants();
        _ = CheckHealthAsync();
    }

    private void RefreshInstalled()
    {
        var active = _s.ImageEngines.Active;
        Installed.Clear();
        foreach (var e in _s.ImageEngines.List()) Installed.Add(new SdEngineItem(e, active?.Id == e.Id));
        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(ShowNvidiaHint));
        _ = CheckHealthAsync();
        SchedulePreview();
    }

    private async Task CheckHealthAsync()
    {
        foreach (var item in Installed.ToList())
        {
            var r = await SdEngineInstaller.SmokeTestAsync(item.Install.ServerExe, TimeSpan.FromSeconds(15));
            item.HealthOk = r.Ok;
            item.Health = r.Ok ? L.Get("ImgEngine_HealthOk") : L.Get("Enum_EngineHealth_" + r.Health) + (r.ExitCode is { } c ? $" (0x{c:X8})" : string.Empty);
        }
    }

    partial void OnSelectedReleaseChanged(EngineRelease? value) => RebuildVariants();
    partial void OnShowAllVariantsChanged(bool value) => RebuildVariants();

    private void RebuildVariants()
    {
        Variants.Clear();
        if (SelectedRelease == null || _hw == null)
        {
            OnPropertyChanged(nameof(Recommended));
            OnPropertyChanged(nameof(RecommendedText));
            return;
        }
        var all = SdAssetMatcher.WindowsVariants(SelectedRelease);
        var recommended = SdAssetMatcher.Recommend(all, _hw, OsInfo.IsWin7);
        foreach (var v in all)
        {
            var ok = SdAssetMatcher.IsApplicable(v, _hw, OsInfo.IsWin7);
            if (!ok && !ShowAllVariants) continue;
            Variants.Add(new VariantItem(v, ok, ReferenceEquals(v, recommended)));
        }
        SelectedVariant = Variants.FirstOrDefault(v => v.Recommended) ?? Variants.FirstOrDefault();
        OnPropertyChanged(nameof(Recommended));
        OnPropertyChanged(nameof(RecommendedText));
        InstallRecommendedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefreshReleases() => !IsLoadingReleases;

    [RelayCommand(CanExecute = nameof(CanRefreshReleases))]
    private async Task RefreshReleasesAsync()
    {
        IsLoadingReleases = true;
        ReleasesError = null;
        try
        {
            var list = await _s.Releases.ListAsync(15, CancellationToken.None, SdAssetMatcher.Repo, onlyLlamaTags: false);
            Releases.Clear();
            foreach (var r in list.Where(r => SdAssetMatcher.WindowsVariants(r).Count > 0)) Releases.Add(r);
            SelectedRelease = Releases.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or Newtonsoft.Json.JsonException or Core.Net.OfflineModeException)
        {
            ReleasesError = L.F("Engine_ReleasesError", ex.GetBaseException().Message);
            Log.Warn("ImageEngine", "Cannot load releases", ex);
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    private bool CanInstall() => SelectedVariant != null && !IsInstalling;
    private bool CanInstallRecommended() => Recommended != null && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => SelectedRelease != null && SelectedVariant != null ? InstallVariantAsync(SelectedRelease, SelectedVariant.Variant) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanInstallRecommended))]
    private Task InstallRecommendedAsync() => SelectedRelease != null && Recommended != null ? InstallVariantAsync(SelectedRelease, Recommended.Variant) : Task.CompletedTask;

    private async Task InstallVariantAsync(EngineRelease release, EngineVariant variant)
    {
        IsInstalling = true;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<InstallStage>(st => InstallStatus = L.Get("Enum_InstallStage_" + st));
            var (install, smoke) = await _s.ImageInstaller.InstallAsync(release, variant, progress, _installCts.Token);
            if (_s.ImageEngines.Active == null || smoke.Ok) _s.ImageEngines.SetActive(install);
            _s.NotifyImageSetupChanged();
            InstallStatus = smoke.Ok
                ? L.F("Engine_Installed", install.DisplayName)
                : L.F("Engine_InstalledButBroken", L.Get("Enum_EngineHealth_" + smoke.Health));
        }
        catch (OperationCanceledException)
        {
            InstallStatus = L.Get("Enum_DownloadState_Canceled");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            InstallStatus = L.F("Engine_InstallFailed", ex.Message);
            Log.Error("ImageEngine", "Install failed", ex);
        }
        finally
        {
            IsInstalling = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    [RelayCommand]
    private void AddExternal()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("ImgEngine_PickServerExe"),
            Filter = "sd-server.exe|sd-server.exe|*.exe|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        var e = _s.ImageEngines.AddExternal(dlg.FileName);
        if (e == null)
        {
            MessageBox.Show(L.Get("ImgEngine_NotAnEngine"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.ImageEngines.SetActive(e);
        _s.NotifyImageSetupChanged();
    }

    [RelayCommand]
    private void SetActive(SdEngineItem item)
    {
        _s.ImageEngines.SetActive(item.Install);
        _s.NotifyImageSetupChanged();
    }

    [RelayCommand]
    private async Task RemoveAsync(SdEngineItem item)
    {
        var key = item.Install.IsExternal ? "Engine_ConfirmUnlink" : "Engine_ConfirmDelete";
        if (MessageBox.Show(L.F(key, item.Title, item.Path), "Offline Chatter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            if (_s.ImageServer.Engine?.Id == item.Install.Id &&
                !await Task.Run(() => _s.StopImageServerAsync(CancellationToken.None)))
            {
                ApiMessage = L.F("Img_ServerFailed", _s.ImageServerError ?? "?");
                return;
            }
            _s.ImageEngines.Remove(item.Install);
            _s.NotifyImageSetupChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenFolder(SdEngineItem item) => SettingsViewModel.OpenFolder(item.Install.Directory);

    // =============== Сервер ===============

    public ObservableCollection<string> ServerLines { get; } = new();

    public string ServerStateText
    {
        get
        {
            var srv = _s.ImageServer;
            var text = L.Get("Enum_ServerState_" + srv.State);
            if (srv.State == ServerState.Ready && srv.LoadTime is { } t)
                text += " · " + L.F("ImgEngine_LoadedIn", t.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture));
            return text;
        }
    }

    public ServerState State => _s.ImageServer.State;
    public string? ServerModel => _s.ImageServer.Model?.DisplayName;
    public string? ServerError => _s.ImageServer.State == ServerState.Failed ? _s.ImageServer.LastError : null;
    public string? ServerCommandLine => _s.ImageServer.CommandLine;
    public bool IsServerRunning => _s.ImageServer.IsRunning;

    private ServerState _lastState;

    private void OnServerChanged()
    {
        var state = _s.ImageServer.State;
        if (state == ServerState.Starting && _lastState != ServerState.Starting)
        {
            while (_pendingLines.TryDequeue(out _)) { }
            ServerLines.Clear();
        }
        _lastState = state;
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ServerModel));
        OnPropertyChanged(nameof(ServerError));
        OnPropertyChanged(nameof(ServerCommandLine));
        OnPropertyChanged(nameof(IsServerRunning));
        StopServerCommand.NotifyCanExecuteChanged();
        StartServerCommand.NotifyCanExecuteChanged();
        RaiseApi();
    }

    private void DrainLog()
    {
        var added = 0;
        while (added < 500 && _pendingLines.TryDequeue(out var line))
        {
            ServerLines.Add(line);
            added++;
        }
        while (ServerLines.Count > 1000) ServerLines.RemoveAt(0);
    }

    [RelayCommand]
    private void CopyLog()
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, ServerLines)); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private bool CanStop() => _s.ImageServer.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopServerAsync() => Task.Run(() => _s.StopImageServerAsync(CancellationToken.None));

    private bool CanStart() => _s.ImageServer.State is not (ServerState.Starting or ServerState.Loading) && Model != null && _s.ImageEngines.Active != null;

    /// <summary>Запустить (или перезапустить с новыми параметрами) генератор с моделью по умолчанию.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServerAsync()
    {
        if (Model is not { } model) return;
        ApiMessage = null;
        if (!await Task.Run(() => _s.EnsureImageServerAsync(model, CancellationToken.None, restart: true)))
            ApiMessage = L.F("Img_ServerFailed", _s.ImageServerError ?? "?");
    }

    // =============== Модель и пресет ===============

    /// <summary>Модель, для которой показываются пресет и превью (модель по умолчанию раздела «Изображения»).</summary>
    public ImageModel? Model { get; private set; }

    public string ModelTitle => Model?.DisplayName ?? L.Get("Img_NoModel");

    public IReadOnlyList<ImageFamily> Families { get; } = ImagePresets.All.Select(p => p.Family).ToList();

    /// <summary>Семейство модели: от него зависят нужные файлы и значения «авто».</summary>
    public ImageFamily? Family
    {
        get => Model?.Preset.Family;
        set
        {
            if (Model == null || value == null || Model.Preset.Family == value) return;
            Model.Family = value.Value.ToString();
            _s.ImageModels.AddOrUpdate(Model);
            RaisePreset();
        }
    }

    public IReadOnlyList<PresetFact> PresetFacts
    {
        get
        {
            if (Model == null) return Array.Empty<PresetFact>();
            var p = Model.Preset;
            string B(bool b) => L.Get(b ? "Common_Yes" : "Common_No");
            var files = string.Join(", ", p.Required.Select(c => L.Get("Enum_ImageComponent_" + c)));
            if (p.RequiredAny is { Count: > 0 } any)
                files += ", " + string.Join(" " + L.Get("Common_Or") + " ", any.Select(c => L.Get("Enum_ImageComponent_" + c)));
            var list = new List<PresetFact>
            {
                new(L.Get("ImgEngine_Fact_Files"), files),
                new(L.Get("Img_Size"), $"{p.Width} × {p.Height}"),
                new(L.Get("Img_Steps"), p.Steps.ToString(CultureInfo.InvariantCulture)),
                new("CFG", p.Cfg.ToString("0.##", CultureInfo.InvariantCulture)),
                new(L.Get("Img_Sampler"), p.Sampler + (p.Scheduler != null ? " · " + p.Scheduler : string.Empty)),
                new(L.Get("ImgEngine_OffloadToCpu"), B(p.OffloadToCpu)),
                new(L.Get("ImgEngine_ClipOnCpu"), B(p.ClipOnCpu)),
                new(L.Get("ImgEngine_VaeTiling"), B(p.VaeTiling)),
                new(L.Get("ImgEngine_Fact_Edit"), B(p.SupportsRefImages)),
            };
            if (p.FlowShift is { } fs) list.Add(new PresetFact("Flow shift", fs.ToString("0.##", CultureInfo.InvariantCulture)));
            if (p.SigmasText is { } sig) list.Add(new PresetFact(L.Get("Img_Sigmas"), sig));
            if (p.SizeMultiple > 8) list.Add(new PresetFact(L.Get("ImgEngine_Fact_Multiple"), p.SizeMultiple.ToString(CultureInfo.InvariantCulture)));
            if (p.ConditioningCache is { } cc) list.Add(new PresetFact(L.Get("ImgEngine_CondCache"), cc.ToString(CultureInfo.InvariantCulture)));
            if (p.SupportsAlpha) list.Add(new PresetFact(L.Get("ImgEngine_Fact_Alpha"), L.Get("Common_Yes")));
            if (p.ModelArgs != null) list.Add(new PresetFact("--model-args", p.ModelArgs));
            return list;
        }
    }

    public string? FamilyHint => Model is { } m ? L.Get("ImgFamily_Hint_" + m.Preset.Family) : null;

    private void RefreshModel()
    {
        Model = _s.ActiveImageModel;
        RaisePreset();
        StartServerCommand.NotifyCanExecuteChanged();
    }

    private void RaisePreset()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ModelTitle));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(PresetFacts));
        OnPropertyChanged(nameof(FamilyHint));
        SchedulePreview();
    }

    // =============== Параметры запуска ===============

    public IReadOnlyList<string> FlashAttentionOptions { get; } = new[] { "auto", "on", "off" };
    public IReadOnlyList<string> WeightTypes { get; } = new[] { string.Empty, "f32", "f16", "bf16", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0", "q4_K", "q3_K", "q2_K" };
    public IReadOnlyList<string> RngOptions { get; } = new[] { string.Empty, "cuda", "cpu" };

    [ObservableProperty] private string _threads = string.Empty;
    [ObservableProperty] private bool? _offloadToCpu;
    [ObservableProperty] private bool? _clipOnCpu;
    [ObservableProperty] private bool? _vaeOnCpu;
    [ObservableProperty] private bool? _vaeTiling;
    [ObservableProperty] private string _flashAttention = "auto";
    [ObservableProperty] private string _weightType = string.Empty;
    [ObservableProperty] private string _rng = string.Empty;
    [ObservableProperty] private string _loraDir = string.Empty;
    [ObservableProperty] private string _modelArgs = string.Empty;
    [ObservableProperty] private bool _serverDefaults = true;
    [ObservableProperty] private string _extraArgs = string.Empty;
    [ObservableProperty] private bool _shareVram = true;
    [ObservableProperty] private string? _preview;
    [ObservableProperty] private string? _skippedText;
    /// <summary>--max-vram: «6.8», «cuda0=6.8» или пусто.</summary>
    [ObservableProperty] private string _maxVram = string.Empty;
    /// <summary>--conditioning-cache-size; пусто — значение пресета.</summary>
    [ObservableProperty] private string _conditioningCacheSize = string.Empty;
    [ObservableProperty] private bool _taePreviewOnly = true;
    [ObservableProperty] private bool? _mmap;
    [ObservableProperty] private string _paramsBackend = string.Empty;
    /// <summary>Плитки VAE по размеру в каждом запросе (настройка генерации, перезапуск не нужен).</summary>
    [ObservableProperty] private bool _autoVaeTiling = true;
    [ObservableProperty] private string _vaeTileSize = "512";

    private static readonly HashSet<string> LaunchFields = new(StringComparer.Ordinal)
    {
        nameof(Threads), nameof(OffloadToCpu), nameof(ClipOnCpu), nameof(VaeOnCpu), nameof(VaeTiling), nameof(FlashAttention),
        nameof(WeightType), nameof(Rng), nameof(LoraDir), nameof(ModelArgs), nameof(ServerDefaults), nameof(ExtraArgs), nameof(ShareVram),
        nameof(MaxVram), nameof(ConditioningCacheSize), nameof(TaePreviewOnly), nameof(Mmap), nameof(AutoVaeTiling), nameof(VaeTileSize), nameof(ParamsBackend),
    };

    /// <summary>Поля, которые меняют запросы, а не запуск сервера — для них подсказка о перезапуске не нужна.</summary>
    private static readonly HashSet<string> RequestFields = new(StringComparer.Ordinal) { nameof(AutoVaeTiling), nameof(VaeTileSize), nameof(ShareVram) };

    // ---------- Профили памяти ----------

    public IReadOnlyList<ImageMemoryProfile> Profiles { get; } = Enum.GetValues(typeof(ImageMemoryProfile)).Cast<ImageMemoryProfile>().ToList();

    /// <summary>«Видеокарта 8 ГБ → бюджет 6.8 ГБ» — что подставят профили.</summary>
    public string? VramBudgetText => _hw?.BestGpu is { DedicatedMemory: > 0 } g
        ? L.F("ImgEngine_VramBudget", Units.FormatBytes(g.DedicatedMemory), ImageMemoryProfiles.VramBudgetGb(g.DedicatedMemory).ToString("0.#", CultureInfo.CurrentCulture))
        : null;

    /// <summary>Сборка CUDA на NVIDIA — показать подсказку про системную память драйвера.</summary>
    public bool ShowNvidiaHint => _s.ImageEngines.Active?.Manifest.Backend == EngineBackend.Cuda || _hw?.BestGpu?.Vendor == GpuVendor.Nvidia;

    /// <summary>Заполнить параметры памяти готовым набором: остальное (потоки, LoRA, свои аргументы) не меняется.</summary>
    [RelayCommand]
    private void ApplyProfile(ImageMemoryProfile profile)
    {
        ImageMemoryProfiles.Apply(_i.Launch, profile, _hw?.BestGpu?.DedicatedMemory ?? 0);
        _i.AutoVaeTiling = true;
        LoadLaunch();
        _s.Settings.Save();
        SchedulePreview();
        ApiMessage = L.F("ImgEngine_ProfileApplied", L.Get("Enum_ImageMemoryProfile_" + profile)) +
                     (_s.ImageServer.State == ServerState.Ready ? " " + L.Get("ImgEngine_RestartHint") : string.Empty);
    }

    private void LoadLaunch()
    {
        _loading = true;
        var l = _i.Launch;
        Threads = l.Threads?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        OffloadToCpu = l.OffloadToCpu;
        ClipOnCpu = l.ClipOnCpu;
        VaeOnCpu = l.VaeOnCpu;
        VaeTiling = l.VaeTiling;
        FlashAttention = string.IsNullOrEmpty(l.FlashAttention) ? "auto" : l.FlashAttention;
        WeightType = l.WeightType ?? string.Empty;
        Rng = l.Rng ?? string.Empty;
        LoraDir = l.LoraDir ?? string.Empty;
        ModelArgs = l.ModelArgs ?? string.Empty;
        ServerDefaults = l.ServerDefaults;
        ExtraArgs = l.ExtraArgs ?? string.Empty;
        ShareVram = _i.ShareVram;
        MaxVram = l.MaxVram ?? string.Empty;
        ConditioningCacheSize = l.ConditioningCacheSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TaePreviewOnly = l.TaePreviewOnly;
        Mmap = l.Mmap;
        ParamsBackend = l.ParamsBackend ?? string.Empty;
        AutoVaeTiling = _i.AutoVaeTiling;
        VaeTileSize = _i.VaeTileSize.ToString(CultureInfo.InvariantCulture);
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName == null || !LaunchFields.Contains(e.PropertyName)) return;
        var l = _i.Launch;
        l.Threads = int.TryParse(Threads.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : null;
        l.OffloadToCpu = OffloadToCpu;
        l.ClipOnCpu = ClipOnCpu;
        l.VaeOnCpu = VaeOnCpu;
        l.VaeTiling = VaeTiling;
        l.FlashAttention = FlashAttention;
        l.WeightType = string.IsNullOrWhiteSpace(WeightType) ? null : WeightType;
        l.Rng = string.IsNullOrWhiteSpace(Rng) ? null : Rng;
        l.LoraDir = string.IsNullOrWhiteSpace(LoraDir) ? null : LoraDir.Trim();
        l.ModelArgs = string.IsNullOrWhiteSpace(ModelArgs) ? null : ModelArgs.Trim();
        l.ServerDefaults = ServerDefaults;
        l.ExtraArgs = string.IsNullOrWhiteSpace(ExtraArgs) ? null : ExtraArgs.Trim();
        _i.ShareVram = ShareVram;
        l.MaxVram = string.IsNullOrWhiteSpace(MaxVram) ? null : MaxVram.Trim();
        l.ConditioningCacheSize = int.TryParse(ConditioningCacheSize.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cc) && cc >= 0 ? cc : null;
        l.TaePreviewOnly = TaePreviewOnly;
        l.Mmap = Mmap;
        l.ParamsBackend = string.IsNullOrWhiteSpace(ParamsBackend) ? null : ParamsBackend.Trim();
        _i.AutoVaeTiling = AutoVaeTiling;
        _i.VaeTileSize = int.TryParse(VaeTileSize.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) && ts >= 64 ? ts : 512;
        _s.Settings.Save();
        SchedulePreview();
        if (e.PropertyName == nameof(LoraDir)) _s.NotifyImageSetupChanged();
        if (_s.ImageServer.State == ServerState.Ready && !RequestFields.Contains(e.PropertyName)) ApiMessage = L.Get("ImgEngine_RestartHint");
    }

    /// <summary>Все параметры запуска — снова «авто» (значения пресета).</summary>
    [RelayCommand]
    private void ResetLaunch()
    {
        _i.Launch = new ImageLaunchSettings();
        LoadLaunch();
        _s.Settings.Save();
        SchedulePreview();
    }

    [RelayCommand]
    private void PickLoraDir()
    {
        var dir = Dialogs.PickFolder(L.Get("ImgEngine_LoraDir"), string.IsNullOrWhiteSpace(LoraDir) ? null : LoraDir);
        if (dir != null) LoraDir = dir;
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        var (cmd, skipped) = await _s.PreviewImageLaunchAsync(Model);
        Preview = cmd ?? L.Get(_s.ImageEngines.Active == null ? "Img_NoEngine" : "Img_NoModel");
        SkippedText = skipped.Count > 0 ? L.F("Profiles_Skipped", string.Join(", ", skipped)) : null;
    }

    [RelayCommand]
    private void CopyPreview()
    {
        if (Preview == null || !Preview.StartsWith("sd-server", StringComparison.Ordinal)) return;
        try { Clipboard.SetText(Preview); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    // =============== Публичный API ===============

    [ObservableProperty] private bool _exposeApi;
    [ObservableProperty] private string _port;
    [ObservableProperty] private ListenOption _listen;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private bool _isKeyVisible;
    [ObservableProperty] private string? _apiMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyApiCommand))]
    private bool _isApiBusy;

    public IReadOnlyList<ListenOption> ListenOptions { get; }

    private static IReadOnlyList<ListenOption> BuildListenOptions(string current)
    {
        var list = new List<ListenOption>
        {
            new(EngineSettings.Loopback, L.Get("Api_ListenLoopback")),
            new(EngineSettings.AnyAddress, L.Get("Api_ListenAll")),
        };
        foreach (var a in Core.Net.NetworkRoute.ListAdapters().Where(a => !string.IsNullOrEmpty(a.IPv4)))
            list.Add(new ListenOption(a.IPv4!, L.F("Api_ListenAdapter", a.IPv4, a.Name)));
        if (!list.Any(o => o.Address == current)) list.Add(new ListenOption(current, L.F("Api_ListenAdapter", current, L.Get("Common_Unknown"))));
        return list;
    }

    private int PortValue => int.TryParse(Port, out var p) && p is > 0 and < 65536 ? p : ImageSettings.DefaultPort;

    public bool IsLanExposed => Listen.Address != EngineSettings.Loopback;
    public string DisplayKey => IsKeyVisible || ApiKey.Length == 0 ? ApiKey : new string('•', Math.Min(24, ApiKey.Length));
    public string LocalUrl => $"http://127.0.0.1:{PortValue}/v1";
    public string? LanUrl => Listen.Address switch
    {
        EngineSettings.Loopback => null,
        EngineSettings.AnyAddress => LanAddress() is { } ip ? $"http://{ip}:{PortValue}/v1" : null,
        var ip => $"http://{ip}:{PortValue}/v1",
    };

    public string ApiStatus
    {
        get
        {
            if (!ExposeApi) return L.Get("ImgApi_Off");
            if (_s.ImageProxyError is { } err) return L.F("ImgApi_ProxyError", PortValue, err);
            return _s.IsImageApiListening ? L.F("ImgApi_Listening", LanUrl ?? LocalUrl) : L.Get("ImgApi_WaitingServer");
        }
    }

    public string Example =>
        $"curl {LocalUrl}/images/generations -H \"Authorization: Bearer <key>\" -H \"Content-Type: application/json\" " +
        "-d \"{\\\"prompt\\\":\\\"a lighthouse at dusk\\\",\\\"size\\\":\\\"1024x1024\\\"}\"";

    public string EditExample =>
        $"curl {LocalUrl}/images/edits -H \"Authorization: Bearer <key>\" -F \"prompt=make it winter\" -F \"image[]=@photo.png\"";

    private void RaiseApi()
    {
        OnPropertyChanged(nameof(ApiStatus));
        OnPropertyChanged(nameof(DisplayKey));
        OnPropertyChanged(nameof(LocalUrl));
        OnPropertyChanged(nameof(LanUrl));
        OnPropertyChanged(nameof(Example));
        OnPropertyChanged(nameof(EditExample));
        OnPropertyChanged(nameof(IsLanExposed));
    }

    partial void OnExposeApiChanged(bool value)
    {
        if (value && ApiKey.Length == 0) ApiKey = EngineProcess.NewKey();
        CommitApi();
        ApiMessage = L.Get(value ? "ImgApi_ApplyHint" : "Api_DisabledHint");
    }

    partial void OnPortChanged(string value) => CommitApi();
    partial void OnListenChanged(ListenOption value) => CommitApi();

    partial void OnApiKeyChanged(string value)
    {
        if (ExposeApi && string.IsNullOrWhiteSpace(value))
        {
            ApiKey = EngineProcess.NewKey();
            return;
        }
        CommitApi();
    }

    partial void OnIsKeyVisibleChanged(bool value) => OnPropertyChanged(nameof(DisplayKey));

    private void CommitApi()
    {
        if (!_constructed) return;
        _i.ExposeApi = ExposeApi;
        _i.Port = PortValue;
        _i.ListenAddress = Listen.Address;
        _i.ApiKeyProtected = ApiKey.Length == 0 ? string.Empty : Secrets.Protect(ApiKey.Trim());
        _s.Settings.Save();
        // Прокси переподнимается сразу: генератор перезапускать не нужно
        _s.UpdateImageProxy();
        RaiseApi();
    }

    [RelayCommand]
    private void RegenerateKey()
    {
        ApiKey = EngineProcess.NewKey();
        IsKeyVisible = true;
    }

    [RelayCommand]
    private void CopyKey() => Copy(ApiKey);

    [RelayCommand]
    private void CopyUrl() => Copy(LanUrl ?? LocalUrl);

    [RelayCommand]
    private void CopyExample() => Copy(Example.Replace("<key>", ApiKey));

    [RelayCommand]
    private void CopyEditExample() => Copy(EditExample.Replace("<key>", ApiKey));

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ApiMessage = L.Get("Chat_Copied");
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private bool CanApplyApi() => !IsApiBusy;

    /// <summary>Запустить генератор с моделью по умолчанию — API начнёт отвечать.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyApi))]
    private async Task ApplyApiAsync()
    {
        IsApiBusy = true;
        ApiMessage = L.F("Img_LoadingModel", Model?.DisplayName ?? "?");
        try
        {
            if (ExposeApi && PortBusyByOther(PortValue))
            {
                ApiMessage = L.F("Api_PortBusy", PortValue);
                return;
            }
            var error = await Task.Run(() => _s.StartImageApiAsync(CancellationToken.None));
            ApiMessage = error ?? (ExposeApi ? L.F("Api_Running", LanUrl ?? LocalUrl) : L.Get("Api_RunningPrivate"));
        }
        finally
        {
            IsApiBusy = false;
            RaiseApi();
        }
    }

    private bool PortBusyByOther(int port)
    {
        if (_s.IsImageApiListening && _i.Port == port) return false;
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port); }
        catch (NetworkInformationException) { return false; }
    }

    private static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Count > 0)
                .SelectMany(p => p.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
