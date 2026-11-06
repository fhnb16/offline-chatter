using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed partial class EngineItemViewModel : ObservableObject
{
    public EngineItemViewModel(EngineInstall install, bool isActive)
    {
        Install = install;
        _isActive = isActive;
    }

    public EngineInstall Install { get; }
    public string Title => Install.DisplayName;
    public string Path => Install.Directory;
    public string Kind => L.Get(Install.IsExternal ? "Engine_External" : "Engine_Downloaded");
    public bool CanRemove => true;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _health;
    [ObservableProperty] private bool _healthOk = true;
}

public sealed partial class VariantItem : ObservableObject
{
    public VariantItem(EngineVariant v, bool applicable, bool recommended)
    {
        Variant = v;
        Applicable = applicable;
        Recommended = recommended;
    }

    public EngineVariant Variant { get; }
    public bool Applicable { get; }
    public bool Recommended { get; }

    public string Title
    {
        get
        {
            var name = L.Get("Enum_EngineBackend_" + Variant.Backend) + (Variant.BackendVersion != null ? " " + Variant.BackendVersion : string.Empty) +
                       " · " + Variant.Arch.ToString().ToLowerInvariant();
            return name + " · " + Units.FormatBytes(Variant.TotalSize) + (Recommended ? " · " + L.Get("Engine_Recommended") : string.Empty);
        }
    }
}

/// <summary>Раздел «Движок»: железо, установленные сборки, загрузка новых, состояние сервера.</summary>
public sealed partial class EngineViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private HardwareInfo? _hw;
    private IReadOnlyList<EngineRelease> _releases = Array.Empty<EngineRelease>();
    private CancellationTokenSource? _installCts;

    public EngineViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        Profiles = new ProfilesViewModel(services);
        _s.Engine.EnginesChanged += (_, _) =>
        {
            RefreshInstalled();
            _ = Profiles.UpdatePreviewAsync();
        };
        _s.Engine.Server.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(OnServerChanged));
        RefreshInstalled();
    }

    public override PageKind Kind => PageKind.Engine;

    public DownloadsViewModel Downloads => _main.Downloads;

    public ProfilesViewModel Profiles { get; }

    public ObservableCollection<EngineItemViewModel> Installed { get; } = new();
    public ObservableCollection<EngineRelease> Releases { get; } = new();
    public ObservableCollection<VariantItem> Variants { get; } = new();

    [ObservableProperty] private string _hardwareSummary = L.Get("Engine_HardwareProbing");
    [ObservableProperty] private string? _runtimeWarning;
    [ObservableProperty] private string? _releasesError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshReleasesCommand))]
    private bool _isLoadingReleases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalling;

    [ObservableProperty] private string? _installStatus;

    [ObservableProperty] private EngineRelease? _selectedRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private VariantItem? _selectedVariant;

    [ObservableProperty] private bool _showAllVariants;

    /// <summary>Канал Win7-compat: сборки llama.cpp из репозитория программы (workflow llama-win7.yml).</summary>
    [ObservableProperty] private bool _useCompatChannel;

    public bool HasCompatChannel => !string.IsNullOrWhiteSpace(_s.Settings.Current.UpdateRepo);

    partial void OnUseCompatChannelChanged(bool value) => _ = RefreshReleasesAsync();

    public bool HasInstalled => Installed.Count > 0;
    public bool CanInstall => SelectedVariant != null && !IsInstalling;

    // ---------- Сервер ----------
    public string ServerState => L.Get("Enum_ServerState_" + _s.Engine.Server.State);
    public bool IsServerRunning => _s.Engine.Server.State is Core.Engine.ServerState.Ready or Core.Engine.ServerState.Loading or Core.Engine.ServerState.Starting;
    public string? ServerDetails
    {
        get
        {
            var srv = _s.Engine.Server;
            if (srv.State == Core.Engine.ServerState.Failed) return srv.LastError;
            if (srv.State == Core.Engine.ServerState.Stopped) return null;
            var model = srv.Options?.ModelPath != null ? System.IO.Path.GetFileName(srv.Options.ModelPath) : "?";
            return L.F("Engine_ServerDetails", srv.BaseUrl, srv.Pid?.ToString() ?? "—", model,
                srv.LoadTime is { } t ? Units.FormatDuration(t) : "—");
        }
    }
    public string? ServerCommandLine => _s.Engine.Server.CommandLine;
    public string ServerLogTail => _s.Engine.Server.Tail;

    public override void OnNavigatedTo()
    {
        _ = LoadHardwareAsync();
        if (Releases.Count == 0 && !IsLoadingReleases) _ = RefreshReleasesAsync();
        OnPropertyChanged(nameof(ServerLogTail));
        _ = Profiles.UpdatePreviewAsync();
    }

    private void OnServerChanged()
    {
        OnPropertyChanged(nameof(ServerState));
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(ServerDetails));
        OnPropertyChanged(nameof(ServerCommandLine));
        OnPropertyChanged(nameof(ServerLogTail));
        StopServerCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadHardwareAsync()
    {
        _hw = await _s.Engine.GetHardwareAsync();
        var gpus = _hw.Gpus.Count == 0
            ? L.Get("Engine_NoGpu")
            : string.Join("; ", _hw.Gpus.Select(g => $"{g.Name} ({Units.FormatBytes(g.DedicatedMemory)})"));
        var simd = _hw.Avx512 ? "AVX-512" : _hw.Avx2 ? "AVX2" : _hw.Avx ? "AVX" : L.Get("Common_Unknown");
        var lines = new List<string>
        {
            $"{_hw.CpuName} · {L.F("Engine_Cores", _hw.PhysicalCores, _hw.LogicalCores)} · {simd}",
            L.F("Engine_Ram", Units.FormatBytes(_hw.TotalRam), Units.FormatBytes(_hw.AvailableRam)),
            gpus,
        };
        if (_hw.NvidiaDriver != null) lines.Add(L.F("Engine_Nvidia", _hw.NvidiaDriver, _hw.CudaVersionText ?? "?"));
        lines.Add(L.Get(_hw.VulkanAvailable ? "Engine_VulkanYes" : "Engine_VulkanNo"));
        HardwareSummary = string.Join(Environment.NewLine, lines);

        var rt = CompatChecker.CheckRuntime(_s.Engine.Registry.Active?.Directory);
        RuntimeWarning = rt.Ok ? null : L.Get(!rt.Ucrt ? "Engine_NoUcrt" : "Engine_NoVcRuntime");
        RebuildVariants();
        _ = CheckHealthAsync();
    }

    private void RefreshInstalled()
    {
        var active = _s.Engine.Registry.Active;
        Installed.Clear();
        foreach (var e in _s.Engine.Registry.List())
            Installed.Add(new EngineItemViewModel(e, active?.Id == e.Id));
        OnPropertyChanged(nameof(HasInstalled));
        _ = CheckHealthAsync();
    }

    /// <summary>Проверка каждой сборки: запускается ли llama-server --version.</summary>
    private async Task CheckHealthAsync()
    {
        foreach (var item in Installed.ToList())
        {
            var r = await CompatChecker.SmokeTestAsync(item.Install.ServerExe, TimeSpan.FromSeconds(15));
            item.HealthOk = r.Ok;
            item.Health = r.Ok ? r.VersionText : L.Get("Enum_EngineHealth_" + r.Health) + (r.ExitCode is { } c ? $" (0x{c:X8})" : string.Empty);
            if (r.Ok && item.Install.IsExternal && item.Install.Manifest.Build == null) item.Install.Manifest.Build = r.Build;
        }
    }

    partial void OnSelectedReleaseChanged(EngineRelease? value) => RebuildVariants();
    partial void OnShowAllVariantsChanged(bool value) => RebuildVariants();

    private void RebuildVariants()
    {
        Variants.Clear();
        if (SelectedRelease == null || _hw == null) return;
        var all = AssetMatcher.WindowsVariants(SelectedRelease);
        var recommended = AssetMatcher.Recommend(all, _hw, OsInfo.IsWin7);
        foreach (var v in all)
        {
            var ok = AssetMatcher.IsApplicable(v, _hw, OsInfo.IsWin7);
            if (!ok && !ShowAllVariants) continue;
            Variants.Add(new VariantItem(v, ok, ReferenceEquals(v, recommended)));
        }
        // По ТЗ по умолчанию — CPU-сборка; рекомендованная лишь подсвечивается
        var arch = _hw.IsArm64 ? CpuArch.Arm64 : CpuArch.X64;
        SelectedVariant = Variants.FirstOrDefault(v => v.Variant.Backend == EngineBackend.Cpu && v.Variant.Arch == arch) ?? Variants.FirstOrDefault();
    }

    private bool CanRefreshReleases() => !IsLoadingReleases;

    [RelayCommand(CanExecute = nameof(CanRefreshReleases))]
    private async Task RefreshReleasesAsync()
    {
        IsLoadingReleases = true;
        ReleasesError = null;
        try
        {
            _releases = await _s.Releases.ListAsync(20, CancellationToken.None, UseCompatChannel ? _s.Settings.Current.UpdateRepo : null);
            Releases.Clear();
            foreach (var r in _releases) Releases.Add(r);
            if (!UseCompatChannel && !_releases.Any(r => r.Tag == GitHubReleases.KnownGoodTag))
            {
                try { Releases.Add(await _s.Releases.GetByTagAsync(GitHubReleases.KnownGoodTag, CancellationToken.None)); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException) { }
            }
            SelectedRelease = Releases.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or IOException or Newtonsoft.Json.JsonException)
        {
            ReleasesError = L.F("Engine_ReleasesError", ex.GetBaseException().Message);
            Log.Warn("Engine", "Cannot load releases", ex);
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    public string ReleaseLabel(EngineRelease r) => r.Tag;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedRelease == null || SelectedVariant == null) return;
        IsInstalling = true;
        _installCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<InstallStage>(s => InstallStatus = L.Get("Enum_InstallStage_" + s));
            var (install, smoke) = await _s.EngineInstaller.InstallAsync(SelectedRelease, SelectedVariant.Variant, progress, _installCts.Token);
            if (_s.Engine.Registry.Active == null || smoke.Ok) _s.Engine.SetActive(install);
            else _s.Engine.NotifyChanged();
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
            Log.Error("Engine", "Install failed", ex);
        }
        finally
        {
            IsInstalling = false;
            _installCts.Dispose();
            _installCts = null;
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    /// <summary>Подключить свою сборку: выбрать llama-server.exe в любой папке.</summary>
    [RelayCommand]
    private void AddExternal()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("Engine_PickServerExe"),
            Filter = "llama-server.exe|llama-server.exe|*.exe|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        var e = _s.Engine.AddExternal(dlg.FileName);
        if (e == null)
        {
            MessageBox.Show(L.Get("Engine_NotAnEngine"), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.Engine.SetActive(e);
    }

    [RelayCommand]
    private void SetActive(EngineItemViewModel item) => _s.Engine.SetActive(item.Install);

    [RelayCommand]
    private void Remove(EngineItemViewModel item)
    {
        var key = item.Install.IsExternal ? "Engine_ConfirmUnlink" : "Engine_ConfirmDelete";
        if (MessageBox.Show(L.F(key, item.Title, item.Path), "Offline Chatter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _s.Engine.Remove(item.Install);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenFolder(EngineItemViewModel item) => SettingsViewModel.OpenFolder(item.Install.Directory);

    [RelayCommand]
    private void InstallVcRuntime() =>
        OpenUrl(_hw?.IsArm64 == true ? CompatChecker.VcRedistUrlArm64 : CompatChecker.VcRedistUrlX64);

    private bool CanStop() => IsServerRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopServer() => _s.Engine.Server.Stop();

    [RelayCommand]
    private void RefreshLog() => OnPropertyChanged(nameof(ServerLogTail));

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("UI", "Cannot open URL " + url, ex);
        }
    }
}
