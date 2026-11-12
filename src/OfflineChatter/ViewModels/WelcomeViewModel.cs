using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

/// <summary>Мастер первого запуска: железо → движок → модель по умолчанию → первый вопрос.</summary>
public sealed partial class WelcomeViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private HardwareInfo? _hw;
    private EngineRelease? _release;
    private EngineVariant? _cpu;
    private EngineVariant? _recommended;
    private HfRepoFiles? _files;
    private string? _engineSourceRepo;

    public WelcomeViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
    }

    public override PageKind Kind => PageKind.Welcome;

    public DownloadsViewModel Downloads => _main.Downloads;

    /// <summary>Шаг «Правая панель чата»: что показывать рядом с беседой (потом меняется в самой панели и в настройках).</summary>
    public InspectorLayoutViewModel InspectorLayout => _main.InspectorLayout;

    [ObservableProperty] private string _hardwareText = L.Get("Engine_HardwareProbing");
    [ObservableProperty] private string? _compatWarning;
    [ObservableProperty] private string _engineText = L.Get("Welcome_Loading");
    [ObservableProperty] private string _modelText = L.Get("Welcome_Loading");
    [ObservableProperty] private bool _useRecommendedGpu;
    [ObservableProperty] private bool _hasGpuOption;
    [ObservableProperty] private string? _gpuOptionText;
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private bool _modelReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallAllCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _status;

    public bool IsDone => EngineReady && ModelReady;

    public override void OnNavigatedTo() => _ = PrepareAsync();

    private async Task PrepareAsync()
    {
        EngineReady = _s.Engine.Registry.Active != null;
        ModelReady = _s.ActiveModel is { Exists: true };
        OnPropertyChanged(nameof(IsDone));

        _hw = await _s.Engine.GetHardwareAsync();
        HardwareText = $"{OsInfo.DisplayName}\n{_hw.CpuName} · {L.F("Engine_Cores", _hw.PhysicalCores, _hw.LogicalCores)}\n" +
                       L.F("Engine_Ram", Units.FormatBytes(_hw.TotalRam), Units.FormatBytes(_hw.AvailableRam)) +
                       (_hw.BestGpu is { } g ? $"\n{g.Name} ({Units.FormatBytes(g.DedicatedMemory)})" : string.Empty);
        var rt = CompatChecker.CheckRuntime(null);
        CompatWarning = rt.Ok ? null : L.Get(!rt.Ucrt ? "Engine_NoUcrt" : "Engine_NoVcRuntime");

        if (EngineReady)
        {
            EngineText = L.F("Welcome_EngineReady", _s.Engine.Registry.Active!.DisplayName);
        }
        else
        {
            try
            {
                var source = _s.Settings.Current.Engine.BuildSource ?? EngineSources.Default(OsInfo.IsWin7);
                var (repo, onlyLlamaTags) = EngineSources.Resolve(source, _s.Settings.Current.Engine.CustomBuildRepo);
                if (source == EngineSource.Custom && repo == null)
                    throw new InvalidOperationException(L.Get("Engine_CustomRepoInvalid"));

                if (source == EngineSource.Official)
                {
                    _release = await _s.Releases.GetByTagAsync(GitHubReleases.KnownGoodTag, CancellationToken.None);
                }
                else
                {
                    var releases = await _s.Releases.ListAsync(20, CancellationToken.None, repo, onlyLlamaTags);
                    var arch = _hw.IsArm64 ? CpuArch.Arm64 : CpuArch.X64;
                    _release = releases.FirstOrDefault(r => AssetMatcher.WindowsVariants(r)
                        .Any(v => v.Backend == EngineBackend.Cpu && v.Arch == arch));
                }
                _engineSourceRepo = repo;
                if (_release == null)
                {
                    EngineText = L.Get("Welcome_EngineUnavailable");
                }
                else
                {
                    var variants = AssetMatcher.WindowsVariants(_release);
                    var arch = _hw.IsArm64 ? CpuArch.Arm64 : CpuArch.X64;
                    _cpu = variants.FirstOrDefault(v => v.Backend == EngineBackend.Cpu && v.Arch == arch);
                    _recommended = AssetMatcher.Recommend(variants, _hw, OsInfo.IsWin7);
                    HasGpuOption = _recommended != null && _recommended.Backend != EngineBackend.Cpu;
                    GpuOptionText = HasGpuOption
                        ? L.F("Welcome_GpuOption", L.Get("Enum_EngineBackend_" + _recommended!.Backend) + (_recommended.BackendVersion != null ? " " + _recommended.BackendVersion : string.Empty), Units.FormatBytes(_recommended.TotalSize))
                        : null;
                    EngineText = _cpu != null ? L.F("Welcome_EngineCpu", _release.Tag, Units.FormatBytes(_cpu.TotalSize)) : L.Get("Welcome_EngineUnavailable");
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or Newtonsoft.Json.JsonException)
            {
                EngineText = L.F("Engine_ReleasesError", ex.GetBaseException().Message);
            }
            catch (InvalidOperationException ex)
            {
                EngineText = ex.Message;
            }
        }

        if (ModelReady)
        {
            ModelText = L.F("Welcome_ModelReady", _s.ActiveModel!.DisplayName);
        }
        else
        {
            try
            {
                _files = await _s.Hf.GetFilesAsync(HfClient.DefaultModelRepo, CancellationToken.None);
                var q = _files.Quants.FirstOrDefault(x => x.Quant?.IsRecommendedDefault == true) ?? _files.Quants.FirstOrDefault();
                var size = (q?.Size ?? 0) + (_files.PreferredMmproj?.Size ?? 0);
                ModelText = L.F("Welcome_ModelDefault", HfClient.DefaultModelRepo, q?.Quant?.Name ?? "?", Units.FormatBytes(size));
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or Newtonsoft.Json.JsonException)
            {
                ModelText = L.F("Models_SearchError", ex.GetBaseException().Message);
            }
        }
    }

    private bool CanInstall() => !IsBusy;

    /// <summary>Скачать и установить движок (если нет) и модель по умолчанию (если нет).</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAllAsync()
    {
        IsBusy = true;
        try
        {
            if (!EngineReady && _release != null)
            {
                var variant = UseRecommendedGpu && _recommended != null ? _recommended : _cpu;
                if (variant != null)
                {
                    var progress = new Progress<InstallStage>(st => Status = L.Get("Welcome_Step_Engine") + ": " + L.Get("Enum_InstallStage_" + st));
                    var (install, smoke) = await _s.EngineInstaller.InstallAsync(_release, variant, progress, CancellationToken.None);
                    if (_engineSourceRepo != null)
                    {
                        install.Manifest.SourceRepo = _engineSourceRepo;
                        EngineRegistry.WriteManifest(install.Directory, install.Manifest);
                    }
                    _s.Settings.Current.Engine.BuildSource ??= EngineSources.Default(OsInfo.IsWin7);
                    _s.Settings.Save();
                    _s.Engine.SetActive(install);
                    EngineReady = smoke.Ok;
                    EngineText = smoke.Ok ? L.F("Welcome_EngineReady", install.DisplayName) : L.F("Engine_InstalledButBroken", L.Get("Enum_EngineHealth_" + smoke.Health));
                }
            }

            if (!ModelReady && _files != null)
            {
                Status = L.Get("Welcome_Step_Model");
                var q = _files.Quants.FirstOrDefault(x => x.Quant?.IsRecommendedDefault == true) ?? _files.Quants.First();
                var folder = Path.Combine(_s.ModelsFolder, HfClient.DefaultModelRepo.Replace('/', Path.DirectorySeparatorChar));
                var jobs = q.Files.Concat(_files.PreferredMmproj != null ? new[] { _files.PreferredMmproj } : Array.Empty<HfFile>())
                    .Select(f => _s.Downloads.Enqueue(new DownloadJob(_s.Hf.FileUrl(HfClient.DefaultModelRepo, f.Path), Path.Combine(folder, Path.GetFileName(f.Path)), Path.GetFileName(f.Path))
                    {
                        ExpectedSize = f.Size > 0 ? f.Size : null,
                        ExpectedSha256 = f.Sha256,
                        Group = "model:" + HfClient.DefaultModelRepo,
                    })).ToList();
                var ok = await Task.WhenAll(jobs.Select(j => j.WhenFinished));
                if (ok[0])
                {
                    var model = ModelRegistry.Describe(jobs[0].TargetPath, ModelSource.HuggingFace, HfClient.DefaultModelRepo);
                    if (jobs.Count > 1 && ok[jobs.Count - 1]) model.MmprojPath = jobs[jobs.Count - 1].TargetPath;
                    model = _s.Models.AddOrUpdate(model);
                    _s.Settings.Current.Models.ActiveModelId = model.Id;
                    _s.Settings.Save();
                    ModelReady = true;
                    ModelText = L.F("Welcome_ModelReady", model.DisplayName);
                    _main.Chat.RefreshModels();
                }
            }
            Status = IsDone ? L.Get("Welcome_AllDone") : L.Get("Welcome_Partial");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = L.F("Engine_InstallFailed", ex.Message);
            Log.Error("Welcome", "Setup failed", ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsDone));
        }
    }

    [RelayCommand]
    private void UseOwnEngine()
    {
        _main.Navigate(PageKind.Engine);
        _main.GetPage<EngineViewModel>()?.AddExternalCommand.Execute(null);
    }

    [RelayCommand]
    private void UseOwnModel()
    {
        _main.Navigate(PageKind.Models);
        _main.GetPage<ModelsViewModel>()?.AddLocalCommand.Execute(null);
    }

    [RelayCommand]
    private void UseApi() => _main.Navigate(PageKind.Providers);

    [RelayCommand]
    private void Finish()
    {
        _s.Settings.Current.Window.FirstRunDone = true;
        _s.Settings.Save();
        _main.Chat.RefreshModels();
        _main.Navigate(PageKind.Chat);
        if (IsDone) _main.Chat.InputText = L.Get("Welcome_FirstPrompt");
    }
}
