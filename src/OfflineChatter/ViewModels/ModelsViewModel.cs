using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed partial class LocalModelItem : ObservableObject
{
    public LocalModelItem(LocalModel model, bool active)
    {
        Model = model;
        _isActive = active;
    }

    public LocalModel Model { get; }
    public string Title => Model.DisplayName;
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Model.Parameters is { } p) parts.Add(HfClient.FormatParams(p));
            if (Model.Quant != null) parts.Add(Model.Quant);
            parts.Add(Units.FormatBytes(Model.SizeBytes));
            if (Model.ContextLength is { } c) parts.Add(L.F("Models_Ctx", c / 1024 + "K"));
            if (Model.HfRepo != null) parts.Add(Model.HfRepo);
            return string.Join(" · ", parts);
        }
    }
    public string Path => Model.Path;
    public bool Exists => Model.Exists;
    public bool HasVision => Model.HasVision;

    [ObservableProperty] private bool _isActive;
}

public sealed class QuantItem
{
    public QuantItem(HfQuantGroup group, string estimate, FitVerdict verdict, bool recommended)
    {
        Group = group;
        Estimate = estimate;
        Verdict = verdict;
        Recommended = recommended;
    }

    public HfQuantGroup Group { get; }
    public string Name => Group.Quant?.Name ?? System.IO.Path.GetFileName(Group.Primary.Path);
    public string Size => Units.FormatBytes(Group.Size) + (Group.IsSharded ? " · " + L.F("Models_Shards", Group.Files.Count) : string.Empty);
    public string Tier => Group.Quant != null ? L.Get("Quant_Tier_" + Group.Quant.Tier) : string.Empty;
    public string Estimate { get; }
    public FitVerdict Verdict { get; }
    public bool Recommended { get; }
}

/// <summary>Раздел «Модели»: свои модели (любые пути), поиск на Hugging Face, выбор кванта, загрузка.</summary>
public sealed partial class ModelsViewModel : PageViewModel
{
    private const int EstimateContext = 8192;
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;
    private HardwareInfo? _hw;

    public ModelsViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        main.Downloads.Updated += (_, _) => RefreshSelectedDownload();
        _s.Models.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshLocal));
        RefreshLocal();
    }

    public override PageKind Kind => PageKind.Models;

    public DownloadsViewModel Downloads => _main.Downloads;

    // ---------- Загрузка выбранной модели (полоса прямо под кнопкой «Скачать») ----------

    private string? SelectedGroup => SelectedResult == null ? null : "model:" + SelectedResult.Id;

    [ObservableProperty] private DownloadSummary? _selectedDownload;

    public bool IsSelectedDownloading => SelectedDownload is { Active: true };

    partial void OnSelectedDownloadChanged(DownloadSummary? value)
    {
        OnPropertyChanged(nameof(IsSelectedDownloading));
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSelectedDownload() => SelectedDownload = SelectedGroup is { } g ? Downloads.Summarize(g) : null;

    [RelayCommand]
    private void CancelSelectedDownload()
    {
        if (SelectedGroup is { } g) Downloads.CancelGroup(g);
        RefreshSelectedDownload();
    }

    [RelayCommand]
    private void PauseSelectedDownload()
    {
        if (SelectedGroup is { } g) Downloads.PauseOrResumeGroup(g);
        RefreshSelectedDownload();
    }

    public ObservableCollection<LocalModelItem> LocalModels { get; } = new();
    public ObservableCollection<HfModelSummary> Results { get; } = new();
    public ObservableCollection<QuantItem> Quants { get; } = new();

    public IReadOnlyList<HfSort> SortOptions { get; } = Enum.GetValues(typeof(HfSort)).Cast<HfSort>().ToList();

    public string DefaultRepo => HfClient.DefaultModelRepo;
    public bool HasLocalModels => LocalModels.Count > 0;
    public string DownloadFolder => _s.ModelsFolder;
    public bool HasHfToken => !string.IsNullOrEmpty(_s.Settings.Current.Models.HfTokenProtected);

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _visionOnly;
    [ObservableProperty] private bool _fitsOnly = true;
    [ObservableProperty] private HfSort _sort = HfSort.Downloads;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HfModelSummary? _selectedResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private QuantItem? _selectedQuant;

    [ObservableProperty] private bool _isLoadingDetails;
    [ObservableProperty] private string? _detailsError;
    [ObservableProperty] private string? _readme;
    [ObservableProperty] private string? _mmprojInfo;
    [ObservableProperty] private bool _includeMmproj = true;
    [ObservableProperty] private string? _scanStatus;

    private HfRepoFiles? _files;

    public bool HasSelection => SelectedResult != null;
    public bool HasMmproj => _files?.PreferredMmproj != null;

    public override void OnNavigatedTo()
    {
        RefreshLocal();
        if (Results.Count == 0 && !IsSearching) _ = SearchAsync();
    }

    private void RefreshLocal()
    {
        var active = _s.ActiveModel;
        LocalModels.Clear();
        foreach (var m in _s.Models.Models.OrderByDescending(m => m.AddedAt))
            LocalModels.Add(new LocalModelItem(m, active?.Id == m.Id));
        OnPropertyChanged(nameof(HasLocalModels));
        _main.Chat.RefreshModels();
    }

    // ---------------- Свои модели ----------------

    /// <summary>Выбрать GGUF-файл на любом диске — он не копируется, в models.json сохраняется путь.</summary>
    [RelayCommand]
    private async Task AddLocalAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("Models_PickFile"),
            Filter = "GGUF (*.gguf)|*.gguf",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames.Where(f => !QuantCatalog.IsMmproj(f)))
        {
            var model = await Task.Run(() => ModelRegistry.Describe(f, ModelSource.LocalFile));
            model = _s.Models.AddOrUpdate(model);
            if (_s.Settings.Current.Models.ActiveModelId == null) SetActiveModel(model);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        ScanStatus = L.Get("Models_Scanning");
        var roots = LocalScanner.DefaultRoots(_s.ModelsFolder).Concat(_s.Settings.Current.Models.ScanFolders).ToList();
        var found = await Task.Run(() => LocalScanner.Scan(roots));
        var added = 0;
        foreach (var f in found)
        {
            if (_s.Models.FindByPath(f) != null) continue;
            var model = await Task.Run(() => ModelRegistry.Describe(f, ModelSource.Scanned));
            _s.Models.AddOrUpdate(model);
            added++;
        }
        ScanStatus = L.F("Models_ScanDone", found.Count, added);
    }

    [RelayCommand]
    private void AddScanFolder()
    {
        var dir = Dialogs.PickFolder(L.Get("Models_PickScanFolder"));
        if (dir == null) return;
        if (!_s.Settings.Current.Models.ScanFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            _s.Settings.Current.Models.ScanFolders.Add(dir);
            _s.Settings.Save();
        }
        _ = ScanAsync();
    }

    [RelayCommand]
    private void UseModel(LocalModelItem item)
    {
        SetActiveModel(item.Model);
        _main.Navigate(PageKind.Chat);
    }

    private void SetActiveModel(LocalModel m)
    {
        _s.Settings.Current.Models.ActiveModelId = m.Id;
        _s.Settings.Save();
        RefreshLocal();
    }

    [RelayCommand]
    private void RemoveModel(LocalModelItem item)
    {
        var res = MessageBox.Show(L.F("Models_ConfirmRemove", item.Title, item.Path), "Offline Chatter",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (res == MessageBoxResult.Cancel) return;
        // «Да» — удалить файлы, «Нет» — только убрать из списка
        if (_s.Engine.Server.Options?.ModelPath == item.Model.Path) _s.Engine.Server.Stop();
        _s.Models.Remove(item.Model, deleteFiles: res == MessageBoxResult.Yes);
    }

    [RelayCommand]
    private void OpenModelFolder(LocalModelItem item)
    {
        var dir = System.IO.Path.GetDirectoryName(item.Path);
        if (dir != null && Directory.Exists(dir)) SettingsViewModel.OpenFolder(dir);
    }

    [RelayCommand]
    private void ChangeDownloadFolder()
    {
        var dir = Dialogs.PickFolder(L.Get("Models_PickDownloadFolder"), _s.ModelsFolder);
        if (dir == null) return;
        _s.Settings.Current.Models.DownloadFolder = dir;
        _s.Settings.Save();
        OnPropertyChanged(nameof(DownloadFolder));
    }

    public void SetHfToken(string token)
    {
        _s.Settings.Current.Models.HfTokenProtected = Secrets.Protect(token.Trim());
        _s.Settings.Save();
        OnPropertyChanged(nameof(HasHfToken));
    }

    // ---------------- Hugging Face ----------------

    partial void OnVisionOnlyChanged(bool value) => _ = SearchAsync();
    partial void OnFitsOnlyChanged(bool value) => _ = SearchAsync();
    partial void OnSortChanged(HfSort value) => _ = SearchAsync();
    partial void OnSelectedResultChanged(HfModelSummary? value)
    {
        RefreshSelectedDownload();
        _ = LoadDetailsAsync(value);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchError = null;
        try
        {
            _hw ??= await _s.Engine.GetHardwareAsync();
            var list = await _s.Hf.SearchAsync(SearchText, VisionOnly, Sort, 50, cts.Token);
            if (cts.IsCancellationRequested) return;
            var budget = _hw.TotalRam + _hw.TotalVram;
            Results.Clear();
            foreach (var m in list)
            {
                if (FitsOnly && m.TotalFileSize is { } size && size > budget * 0.8) continue;
                // Только чат-модели: TTS, видео, эмбеддинги и пр. в чате бесполезны
                if (m.PipelineTag is not (null or "text-generation" or "image-text-to-text" or "text2text-generation" or "conversational" or "any-to-any"))
                    continue;
                Results.Add(m);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or Newtonsoft.Json.JsonException)
        {
            SearchError = L.F("Models_SearchError", ex.GetBaseException().Message);
        }
        finally
        {
            if (ReferenceEquals(cts, _searchCts)) IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task ShowDefaultAsync()
    {
        try
        {
            var m = await _s.Hf.GetModelAsync(HfClient.DefaultModelRepo, CancellationToken.None);
            if (!Results.Any(r => r.Id == m.Id)) Results.Insert(0, m);
            SelectedResult = Results.First(r => r.Id == m.Id);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or Newtonsoft.Json.JsonException)
        {
            SearchError = L.F("Models_SearchError", ex.GetBaseException().Message);
        }
    }

    private async Task LoadDetailsAsync(HfModelSummary? model)
    {
        _detailsCts?.Cancel();
        Quants.Clear();
        Readme = null;
        MmprojInfo = null;
        DetailsError = null;
        _files = null;
        OnPropertyChanged(nameof(HasMmproj));
        if (model == null) return;

        var cts = _detailsCts = new CancellationTokenSource();
        IsLoadingDetails = true;
        try
        {
            _hw ??= await _s.Engine.GetHardwareAsync();
            var files = await _s.Hf.GetFilesAsync(model.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            _files = files;
            OnPropertyChanged(nameof(HasMmproj));
            if (files.PreferredMmproj is { } mm) MmprojInfo = L.F("Models_MmprojInfo", System.IO.Path.GetFileName(mm.Path), Units.FormatBytes(mm.Size));

            // Метаданные (слои, головы) для точной оценки KV — по первому кванту; у всех квантов они одинаковые
            GgufInfo? info = null;
            if (files.Quants.Count > 0)
            {
                try { info = await _s.Hf.ReadRemoteGgufAsync(model.Id, files.Quants[0].Primary.Path, cts.Token); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidDataException)
                {
                    Log.Warn("Models", "Remote GGUF header unavailable", ex);
                }
            }
            if (cts.IsCancellationRequested) return;
            FillQuants(files, info);

            Readme = StripFrontMatter(await _s.Hf.GetReadmeAsync(model.Id, cts.Token));
        }
        catch (OperationCanceledException) { }
        catch (HttpStatusException ex) when ((int)ex.Status is 401 or 403)
        {
            DetailsError = L.Get("Models_Gated");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or Newtonsoft.Json.JsonException)
        {
            DetailsError = ex.GetBaseException().Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _detailsCts)) IsLoadingDetails = false;
        }
    }

    private void FillQuants(HfRepoFiles files, GgufInfo? info)
    {
        var hw = _hw!;
        var mmSize = files.PreferredMmproj?.Size ?? 0;
        var vram = hw.BestGpu?.DedicatedMemory ?? 0;
        var ram = hw.AvailableRam > 0 ? hw.AvailableRam : hw.TotalRam;

        QuantItem? best = null;
        var items = new List<QuantItem>();
        foreach (var g in files.Quants)
        {
            var ctx = Math.Min(EstimateContext, info?.ContextLength ?? EstimateContext);
            var onGpu = vram > 0 ? MemoryEstimator.Estimate(info, g.Size, ctx, gpuLayers: -1, mmprojBytes: mmSize) : null;
            var cpu = MemoryEstimator.Estimate(info, g.Size, ctx, mmprojBytes: mmSize);
            FitVerdict verdict;
            string text;
            if (onGpu != null && onGpu.Vram <= vram * 0.9)
            {
                verdict = FitVerdict.Fits;
                text = L.F("Models_EstimateGpu", Units.FormatBytes(onGpu.Vram), ctx / 1024 + "K");
            }
            else
            {
                verdict = MemoryEstimator.Verdict(cpu.Total, ram + (long)(vram * 0.9));
                text = L.F("Models_EstimateCpu", Units.FormatBytes(cpu.Total), ctx / 1024 + "K");
            }
            items.Add(new QuantItem(g, text, verdict, false));
        }

        // Рекомендация: лучший по качеству квант, который точно помещается, но не выше Q8 (F16 редко оправдан)
        var candidate = items.Where(i => i.Verdict == FitVerdict.Fits && (i.Group.Quant?.QualityRank ?? 0) <= 24)
                             .OrderByDescending(i => i.Group.Quant?.QualityRank ?? 0).FirstOrDefault()
                        ?? items.FirstOrDefault(i => i.Group.Quant?.IsRecommendedDefault == true);
        foreach (var i in items)
        {
            var item = ReferenceEquals(i, candidate) ? new QuantItem(i.Group, i.Estimate, i.Verdict, true) : i;
            Quants.Add(item);
            if (item.Recommended) best = item;
        }
        SelectedQuant = best ?? Quants.FirstOrDefault();
    }

    private bool CanDownload() => SelectedQuant != null && !IsSelectedDownloading;

    /// <summary>Скачать выбранный квант (все шарды) и mmproj в папку моделей: models/автор/репозиторий/.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedResult == null || SelectedQuant == null) return;
        var repo = SelectedResult.Id;
        var folder = System.IO.Path.Combine(_s.ModelsFolder, repo.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var headers = _s.Hf.AuthHeaders();

        DownloadJob Enqueue(HfFile f) => _s.Downloads.Enqueue(Apply(new DownloadJob(_s.Hf.FileUrl(repo, f.Path),
            System.IO.Path.Combine(folder, System.IO.Path.GetFileName(f.Path)), System.IO.Path.GetFileName(f.Path))
        {
            ExpectedSize = f.Size > 0 ? f.Size : null,
            ExpectedSha256 = f.Sha256,
            Group = "model:" + repo,
        }, headers));

        var jobs = SelectedQuant.Group.Files.Select(Enqueue).ToList();
        RefreshSelectedDownload();
        DownloadJob? mmJob = IncludeMmproj && _files?.PreferredMmproj is { } mm ? Enqueue(mm) : null;
        var primaryPath = jobs[0].TargetPath;

        var results = await Task.WhenAll(jobs.Select(j => j.WhenFinished));
        var mmOk = mmJob == null || await mmJob.WhenFinished;
        if (!results.All(r => r)) return;

        var model = await Task.Run(() => ModelRegistry.Describe(primaryPath, ModelSource.HuggingFace, repo));
        if (mmJob != null && mmOk) model.MmprojPath = mmJob.TargetPath;
        model = _s.Models.AddOrUpdate(model);
        if (_s.Settings.Current.Models.ActiveModelId == null || _s.Models.Find(_s.Settings.Current.Models.ActiveModelId) == null)
            SetActiveModel(model);
    }

    private static DownloadJob Apply(DownloadJob job, IDictionary<string, string> headers)
    {
        foreach (var h in headers) job.Headers[h.Key] = h.Value;
        return job;
    }

    [RelayCommand]
    private void OpenOnHf()
    {
        if (SelectedResult != null) EngineViewModel.OpenUrl(_s.Hf.RepoPageUrl(SelectedResult.Id));
    }

    /// <summary>README на HF начинается с YAML-метаданных (license, tags…) — в карточке они не нужны.</summary>
    internal static string? StripFrontMatter(string? readme)
    {
        if (readme == null || !readme.StartsWith("---", StringComparison.Ordinal)) return readme;
        var end = readme.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return readme;
        var next = readme.IndexOf('\n', end + 4);
        return next < 0 ? string.Empty : readme.Substring(next + 1).TrimStart();
    }
}