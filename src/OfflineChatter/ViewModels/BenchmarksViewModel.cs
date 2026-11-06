using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Benchmarks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed partial class ReportItem : ObservableObject
{
    public ReportItem(BenchReport report) => Report = report;
    public BenchReport Report { get; }
    public string Title => Report.Title;
    public string Summary => string.Join(" · ", Report.Rows.Select(r => $"{r.Test} {r.Mean.ToString(r.Mean >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture)} {r.Unit}"));
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Строка таблицы сравнения: тест + значения по каждому отмеченному отчёту.</summary>
public sealed record CompareRow(string Test, string Unit, IReadOnlyList<string> Values);

/// <summary>Раздел «Бенчмарки»: llama-bench, chat-bench, отчёты, сравнение и экспорт.</summary>
public sealed partial class BenchmarksViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private readonly ReportStore _store;
    private CancellationTokenSource? _cts;

    public BenchmarksViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        _store = new ReportStore(services.Paths.Reports);
        _chatPrompts = string.Join(Environment.NewLine, ChatBench.DefaultPrompts);
        Reload();
    }

    public override PageKind Kind => PageKind.Benchmarks;

    public ObservableCollection<ReportItem> Reports { get; } = new();
    public ObservableCollection<CompareRow> Comparison { get; } = new();
    public ObservableCollection<string> CompareHeaders { get; } = new();
    public ObservableCollection<ModelOption> Targets => _main.Chat.ModelOptions;

    [ObservableProperty] private int _promptTokens = 512;
    [ObservableProperty] private int _genTokens = 128;
    [ObservableProperty] private int _repetitions = 3;
    [ObservableProperty] private string _threads = string.Empty;
    [ObservableProperty] private string _gpuLayers = string.Empty;
    [ObservableProperty] private bool _flashAttention = true;

    [ObservableProperty] private ModelOption? _chatTarget;
    [ObservableProperty] private int _chatRuns = 2;
    [ObservableProperty] private int _chatMaxTokens = 128;
    [ObservableProperty] private string _chatPrompts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunLlamaBenchCommand), nameof(RunChatBenchCommand))]
    private bool _isRunning;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _output = string.Empty;

    /// <summary>Локальные модели для llama-bench (по умолчанию — активная).</summary>
    public ObservableCollection<Core.Models.LocalModel> BenchModels { get; } = new();

    [ObservableProperty] private Core.Models.LocalModel? _benchModel;

    public override void OnNavigatedTo()
    {
        var keep = BenchModel?.Id ?? _s.ActiveModel?.Id;
        BenchModels.Clear();
        foreach (var m in _s.Models.Models.Where(m => m.Exists)) BenchModels.Add(m);
        BenchModel = BenchModels.FirstOrDefault(m => m.Id == keep) ?? BenchModels.FirstOrDefault();
        ChatTarget ??= _main.Chat.SelectedModel ?? Targets.FirstOrDefault();
        Reload();
    }

    private void Reload()
    {
        Reports.Clear();
        foreach (var r in _store.List())
        {
            var item = new ReportItem(r);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReportItem.IsChecked)) BuildComparison(); };
            Reports.Add(item);
        }
        BuildComparison();
    }

    private async Task<SystemSnapshot> SnapshotAsync()
    {
        var hw = await _s.Engine.GetHardwareAsync();
        return SystemSnapshot.From(hw, _s.Engine.Registry.Active, OsInfo.DisplayName, _s.Version);
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunLlamaBenchAsync()
    {
        var model = BenchModel;
        var engine = _s.Engine.Registry.Active;
        if (model == null || engine == null || !File.Exists(engine.BenchExe))
        {
            Status = L.Get(model == null ? "Profiles_NoModel" : "Bench_NoBenchExe");
            return;
        }
        int? I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        var args = LlamaBench.BuildArgs(model.Path, PromptTokens, GenTokens, Math.Max(1, Repetitions), I(Threads),
            engine.Manifest.Backend == Core.Engine.EngineBackend.Cpu ? null : I(GpuLayers) ?? 99, FlashAttention);

        // Сервер держит модель в памяти — на время замера останавливаем, чтобы цифры были честными
        if (_s.Engine.Server.State != Core.Engine.ServerState.Stopped) _s.Engine.Server.Stop();

        await RunAsync(async ct =>
        {
            Output = "llama-bench " + args + Environment.NewLine;
            var rows = await LlamaBench.RunAsync(engine.BenchExe, args, line =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => AppendOutput(line))), ct);
            return new BenchReport
            {
                Kind = "llama-bench", Model = model.Path, ModelSize = model.SizeBytes, Quant = model.Quant, Parameters = args,
                System = await SnapshotAsync(), Rows = rows,
            };
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunChatBenchAsync()
    {
        var target = ChatTarget;
        if (target == null)
        {
            Status = L.Get("Chat_NoModel");
            return;
        }
        var prompts = ChatPrompts.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (prompts.Count == 0) prompts = ChatBench.DefaultPrompts.ToList();

        await RunAsync(async ct =>
        {
            var (client, model, error) = await _s.ResolveChatClientAsync(target.Ref, s => Status = s, ct);
            if (client == null) throw new InvalidOperationException(error);
            Output = $"chat-bench · {target.Title} · {ChatRuns}× {prompts.Count}" + Environment.NewLine;
            var progress = new Progress<string>(p => { Status = p; AppendOutput(p); });
            var rows = await ChatBench.RunAsync(client, model, prompts, Math.Max(1, ChatRuns), Math.Max(16, ChatMaxTokens), progress, ct);
            var local = target.Ref.StartsWith("local:", StringComparison.Ordinal);
            return new BenchReport
            {
                Kind = "chat-bench", Model = local ? _s.Engine.Server.Options?.ModelPath ?? target.Title : target.Title,
                ModelSize = local ? _s.ActiveModel?.SizeBytes : null, Quant = local ? _s.ActiveModel?.Quant : null,
                Parameters = local ? _s.Engine.Server.CommandLine : target.Group,
                System = await SnapshotAsync(), Rows = rows,
                Notes = $"runs={ChatRuns}, max_tokens={ChatMaxTokens}, prompts={prompts.Count}",
            };
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task<BenchReport>> work)
    {
        IsRunning = true;
        Status = L.Get("Bench_Running");
        _cts = new CancellationTokenSource();
        try
        {
            var report = await work(_cts.Token);
            _store.Save(report);
            Log.Info("Bench", "Report saved", new { report.Kind, report.Id, rows = report.Rows.Select(r => new { r.Test, r.Mean }) });
            Status = L.Get("Bench_Done");
            Reload();
            if (Reports.FirstOrDefault() is { } first) first.IsChecked = true;
        }
        catch (OperationCanceledException)
        {
            Status = L.Get("Enum_DownloadState_Canceled");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or System.Net.Http.HttpRequestException or Newtonsoft.Json.JsonException)
        {
            Status = L.F("Bench_Failed", ex.Message);
            Log.Error("Bench", "Benchmark failed", ex);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void AppendOutput(string line)
    {
        Output += line + Environment.NewLine;
        if (Output.Length > 20000) Output = Output.Substring(Output.Length - 15000);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void BuildComparison()
    {
        Comparison.Clear();
        CompareHeaders.Clear();
        var selected = Reports.Where(r => r.IsChecked).Select(r => r.Report).ToList();
        if (selected.Count == 0) return;
        foreach (var r in selected) CompareHeaders.Add(r.Title);
        var tests = selected.SelectMany(r => r.Rows.Select(x => (x.Test, x.Unit))).Distinct().ToList();
        foreach (var (test, unit) in tests)
        {
            var values = selected.Select(r =>
            {
                var row = r.Rows.FirstOrDefault(x => x.Test == test);
                return row == null ? "—" : row.Mean.ToString(row.Mean >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + (row.StdDev > 0 ? " ± " + row.StdDev.ToString("0.0", CultureInfo.CurrentCulture) : string.Empty);
            }).ToList();
            Comparison.Add(new CompareRow(test, unit, values));
        }
    }

    private IReadOnlyList<BenchReport> ExportSet()
    {
        var checkedOnes = Reports.Where(r => r.IsChecked).Select(r => r.Report).ToList();
        return checkedOnes.Count > 0 ? checkedOnes : Reports.Select(r => r.Report).ToList();
    }

    [RelayCommand]
    private void Export(string format)
    {
        var set = ExportSet();
        if (set.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "." + format,
            Filter = format.ToUpperInvariant() + "|*." + format,
        };
        if (dlg.ShowDialog() != true) return;
        var text = format switch
        {
            "html" => ReportStore.ToHtml(set),
            "md" => ReportStore.ToMarkdown(set),
            "csv" => ReportStore.ToCsv(set),
            _ => Newtonsoft.Json.JsonConvert.SerializeObject(set, JsonFile.Settings),
        };
        File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(format == "csv"));
        Status = L.F("Bench_Exported", dlg.FileName);
    }

    [RelayCommand]
    private void DeleteReport(ReportItem item)
    {
        _store.Delete(item.Report);
        Reload();
    }

    [RelayCommand]
    private void OpenFolder() => SettingsViewModel.OpenFolder(_s.Paths.Reports);
}
