using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Строка «подпись — значение» в сведениях о сервере.</summary>
public sealed partial class InsightRow : ObservableObject
{
    public InsightRow(string label, bool mono)
    {
        Label = label;
        Mono = mono;
    }

    public string Label { get; }
    public bool Mono { get; }
    [ObservableProperty] private string? _value;
    [ObservableProperty] private bool _isWarn;
}

public sealed class InsightGroup
{
    public InsightGroup(string title) => Title = title;
    public string Title { get; }
    public ObservableCollection<InsightRow> Rows { get; } = new();
}

/// <summary>
/// Состояние и подробности llama-server: сборка, устройства, параметры запуска, нагрузка, скорость запросов,
/// метаданные модели. Один экземпляр на окно — его показывают боковая панель, шапка и правая панель чата и раздел «Движок».
/// Данные — из вывода сервера (<see cref="ServerLogFacts"/>), процесса и заголовка GGUF; сетевых запросов нет.
/// </summary>
public sealed partial class ServerInsightViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _timer;
    private readonly ResourceMonitor _monitor = new();
    private string? _signature;
    private string? _ggufPath;
    private GgufInfo? _gguf;
    private HardwareInfo? _hw;

    public ServerInsightViewModel(AppServices services)
    {
        _s = services;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _s.Engine.Server.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(OnServerChanged));
        _s.Engine.EnginesChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(OnServerChanged));
        _ = LoadHardwareAsync();
        OnServerChanged();
    }

    public ObservableCollection<InsightGroup> Groups { get; } = new();

    private EngineProcess Server => _s.Engine.Server;

    public ServerState State => Server.State;
    public string StateText => L.Get("Enum_ServerState_" + State);
    public bool IsRunning => State is ServerState.Starting or ServerState.Loading or ServerState.Ready;

    /// <summary>Сборка, с которой запущен сервер (или активная, если он остановлен).</summary>
    private EngineInstall? Engine => IsRunning ? Server.Engine ?? _s.Engine.Registry.Active : _s.Engine.Registry.Active;

    /// <summary>«b7083 · Vulkan» — коротко, для строк состояния.</summary>
    public string EngineShort
    {
        get
        {
            if (Engine is not { } e) return L.Get("Status_NoEngine");
            var facts = IsRunning ? Server.Facts : null;
            var build = facts?.Build is { } b ? "b" + b : TagOf(e);
            return build + " · " + BackendName(e);
        }
    }

    /// <summary>Модель, загруженная в сервер; если сервер остановлен — выбранная по умолчанию.</summary>
    public string ModelTitle
    {
        get
        {
            var path = IsRunning ? Server.Options?.ModelPath : null;
            if (path != null) return Path.GetFileNameWithoutExtension(path);
            return _s.ActiveModel?.DisplayName ?? L.Get("Model_NotSelected");
        }
    }

    /// <summary>Строка под моделью в боковой панели: «b7083 · Vulkan · Готов».</summary>
    public string StatusLine => EngineShort + " · " + StateText;

    /// <summary>Шапка чата: «Готов · b7083 · Vulkan».</summary>
    public string PillText => StateText + " · " + EngineShort;

    /// <summary>Подсказка: модель, сборка, адрес и главное о запуске.</summary>
    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                L.F("Status_TipModel", ModelTitle) + (IsRunning ? string.Empty : " (" + L.Get("Status_NotLoaded") + ")"),
                L.F("Status_TipEngine", Engine?.DisplayName ?? L.Get("Status_NoEngine")),
                L.F("Status_TipState", StateText),
            };
            if (IsRunning)
            {
                var f = Server.Facts;
                lines.Add(Server.BaseUrl);
                if (OffloadText(f, Server.Options) is { } gpu) lines.Add(L.Get("Ins_GpuLayers") + ": " + gpu);
                if ((f.ContextInt("n_ctx") ?? Server.Options?.ContextSize) is { } ctx) lines.Add(L.Get("Ins_Context") + ": " + N(ctx));
            }
            else if (State == ServerState.Failed && ErrorText is { } err)
            {
                lines.Add(FirstLine(err));
            }
            lines.Add(L.Get("Status_TipOpen"));
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Текст ошибки последнего запуска (для строгого режима GPU — своим текстом).</summary>
    public string? ErrorText => Server.State != ServerState.Failed ? null : _s.ServerErrorText;

    private void OnServerChanged()
    {
        _timer.IsEnabled = IsRunning;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ErrorText));
        RefreshHeadlines();
        Refresh();
    }

    private void RefreshHeadlines()
    {
        OnPropertyChanged(nameof(EngineShort));
        OnPropertyChanged(nameof(ModelTitle));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Выбрана другая модель по умолчанию (вызывают разделы «Модели» и «Чат»).</summary>
    public void OnActiveModelChanged()
    {
        RefreshHeadlines();
        Refresh();
    }

    private async Task LoadHardwareAsync()
    {
        _hw = await _s.Engine.GetHardwareAsync();
        Refresh();
    }

    private void EnsureGguf(string? path)
    {
        if (path == _ggufPath) return;
        _ggufPath = path;
        _gguf = null;
        if (path == null) return;
        _ = Task.Run(() =>
        {
            try { return GgufReader.ReadFile(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        }).ContinueWith(t =>
        {
            if (_ggufPath != path) return;
            _gguf = t.Result;
            Refresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------

    private sealed record Row(string Group, string Label, string? Value, bool Mono = true, bool Warn = false);

    public void Refresh()
    {
        var rows = new List<Row>();
        var srv = Server;
        var running = IsRunning;
        var facts = running ? srv.Facts : new ServerLogFacts();
        var opt = running ? srv.Options : null;
        var engine = Engine;
        var modelPath = opt?.ModelPath ?? _s.ActiveModel?.Path;
        EnsureGguf(modelPath);

        // ----- сервер -----
        const string gS = "Ins_GroupServer";
        rows.Add(new Row(gS, "Ins_State", StateText, false, State == ServerState.Failed));
        if (ErrorText is { } err) rows.Add(new Row(gS, "Ins_Error", err, false, true));
        if (engine != null)
        {
            var build = facts.Build is { } b ? "b" + b + (facts.Commit != null ? " (" + facts.Commit + ")" : string.Empty) : TagOf(engine);
            rows.Add(new Row(gS, "Ins_Build", "llama.cpp " + build));
            rows.Add(new Row(gS, "Ins_Backend", BackendName(engine) + " · " + engine.Manifest.Arch.ToString().ToLowerInvariant(), false));
            rows.Add(new Row(gS, "Ins_Source", engine.IsExternal ? L.Get("Engine_External") + ": " + engine.Directory
                : engine.Manifest.SourceRepo ?? "ggml-org/llama.cpp", false));
        }
        else
        {
            rows.Add(new Row(gS, "Ins_Build", L.Get("Status_NoEngine"), false, true));
        }
        if (running)
        {
            rows.Add(new Row(gS, "Ins_Devices", facts.Devices.Count > 0 ? string.Join(Environment.NewLine, facts.Devices) : "CPU", false));
            rows.Add(new Row(gS, "Ins_Address", srv.BaseUrl + (srv.Pid is { } pid ? " · PID " + pid : string.Empty)));
            if (srv.StartedAt is { } started) rows.Add(new Row(gS, "Ins_Uptime", Units.FormatDuration(DateTime.Now - started)));
            if (srv.LoadTime is { } lt) rows.Add(new Row(gS, "Ins_LoadTime", Units.FormatDuration(lt)));
        }

        if (running)
        {
            // ----- нагрузка -----
            const string gL = "Ins_GroupLoad";
            var r = _monitor.Sample(srv.Pid);
            rows.Add(new Row(gL, "Ins_Cpu", r.CpuPercent is { } cpu ? cpu.ToString("0", CultureInfo.InvariantCulture) + " %" : "—"));
            rows.Add(new Row(gL, "Ins_Ram", r.WorkingSet is { } ws ? Units.FormatBytes(ws) : "—"));
            if (r.VramUsed is { } vu && r.VramTotal is { } vt) rows.Add(new Row(gL, "Ins_Vram", Units.FormatBytes(vu) + " / " + Units.FormatBytes(vt)));
            if (facts.ModelBuffersMiB.Count > 0) rows.Add(new Row(gL, "Ins_Weights", Buffers(facts.ModelBuffersMiB)));
            if (facts.KvTotalMiB is { } kv)
                rows.Add(new Row(gL, "Ins_KvCache", Mib(kv) + (facts.KvTypeK != null ? $" · {facts.KvTypeK}/{facts.KvTypeV}" : string.Empty) +
                                                     (facts.KvBuffersMiB.Count > 0 ? " · " + string.Join(", ", facts.KvBuffersMiB.Keys) : string.Empty)));
            if (facts.ComputeBuffersMiB.Count > 0) rows.Add(new Row(gL, "Ins_Compute", Buffers(facts.ComputeBuffersMiB)));

            // ----- работа модели -----
            const string gA = "Ins_GroupActivity";
            if (facts.Requests == 0)
            {
                rows.Add(new Row(gA, "Ins_Requests", L.Get("Ins_NoRequests"), false));
            }
            else
            {
                rows.Add(new Row(gA, "Ins_Requests", N(facts.Requests) + (facts.LastRequestAt is { } at ? " · " + L.F("Ins_LastAt", at.ToString("HH:mm:ss", CultureInfo.InvariantCulture)) : string.Empty)));
                rows.Add(new Row(gA, "Ins_LastPrompt", Speed(facts.LastPromptTokens, facts.LastPromptTps)));
                rows.Add(new Row(gA, "Ins_LastGen", Speed(facts.LastGeneratedTokens, facts.LastGenerationTps)));
                rows.Add(new Row(gA, "Ins_TotalPrompt", Speed(facts.PromptTokensTotal, facts.AvgPromptTps)));
                rows.Add(new Row(gA, "Ins_TotalGen", Speed(facts.GeneratedTokensTotal, facts.AvgGenerationTps)));
                if (facts.LastDraftAcceptance is { } acc) rows.Add(new Row(gA, "Ins_DraftAccept", (acc * 100).ToString("0", CultureInfo.InvariantCulture) + " %"));
            }
            if (facts.ChatFormat != null) rows.Add(new Row(gA, "Ins_ChatFormat", facts.ChatFormat, false));
        }

        if (opt != null)
        {
            // ----- параметры запуска -----
            const string gP = "Ins_GroupLaunch";
            var ctx = facts.ContextInt("n_ctx") ?? opt.ContextSize;
            var perSlot = facts.ContextInt("n_ctx_per_seq") ?? facts.ContextInt("n_ctx_seq");
            var train = _gguf?.ContextLength ?? ParseInt(facts.Info("n_ctx_train"));
            rows.Add(new Row(gP, "Ins_Context", (ctx is { } c ? N(c) : "—") +
                                                (perSlot is { } ps && ps != ctx ? " · " + L.F("Ins_PerSlot", N(ps)) : string.Empty) +
                                                (train is { } t ? " · " + L.F("Ins_Trained", N(t)) : string.Empty)));
            var offload = OffloadText(facts, opt);
            if (offload != null)
                rows.Add(new Row(gP, "Ins_GpuLayers", offload, true, facts.FullyOffloaded == false));
            var threads = facts.Threads ?? opt.Threads;
            if (threads != null)
                rows.Add(new Row(gP, "Ins_Threads", N(threads.Value) +
                    (facts.ThreadsBatch is { } tb && tb != threads ? " · " + L.F("Ins_BatchThreads", N(tb)) : string.Empty) +
                    ((facts.ThreadsTotal ?? _hw?.LogicalCores) is { } total ? " / " + N(total) : string.Empty)));
            var batch = facts.ContextInt("n_batch") ?? opt.BatchSize;
            var ubatch = facts.ContextInt("n_ubatch") ?? opt.UBatchSize;
            if (batch != null || ubatch != null) rows.Add(new Row(gP, "Ins_Batch", (batch is { } bb ? N(bb) : "—") + " / " + (ubatch is { } ub ? N(ub) : "—")));
            rows.Add(new Row(gP, "Ins_Slots", N(facts.Slots ?? opt.Parallel ?? 1)));
            rows.Add(new Row(gP, "Ins_FlashAttn", facts.Context.TryGetValue("flash_attn", out var fa) ? fa : opt.FlashAttention ?? "auto"));
            rows.Add(new Row(gP, "Ins_Mmap", (opt.NoMmap ? "no-mmap" : "mmap") + (opt.Mlock ? " · mlock" : string.Empty)));
            if (opt.ReasoningFormat != null) rows.Add(new Row(gP, "Ins_Reasoning", opt.ReasoningFormat));
            if (opt.MmprojPath != null) rows.Add(new Row(gP, "Ins_Mmproj", Path.GetFileName(opt.MmprojPath), false));
            if (opt.DraftModelPath != null) rows.Add(new Row(gP, "Ins_DraftModel", Path.GetFileName(opt.DraftModelPath), false));
            if (facts.CpuFeatures.Count > 0) rows.Add(new Row(gP, "Ins_CpuFeatures", string.Join(" ", facts.CpuFeatures)));
            if (srv.SkippedFlags.Count > 0) rows.Add(new Row(gP, "Ins_Skipped", string.Join(" ", srv.SkippedFlags), true, true));
        }

        // ----- модель -----
        if (modelPath != null) AddModelRows(rows, modelPath, facts);

        Apply(rows);
        RefreshHeadlines();
    }

    private void AddModelRows(List<Row> rows, string path, ServerLogFacts facts)
    {
        const string g = "Ins_GroupModel";
        var info = _gguf;
        rows.Add(new Row(g, "Ins_ModelName", info?.Name ?? facts.Info("general.name") ?? Path.GetFileNameWithoutExtension(path), false));
        var arch = info?.Architecture ?? facts.Info("arch");
        var basename = info?.Get<string>("general.basename");
        var finetune = info?.Get<string>("general.finetune");
        if (arch != null)
            rows.Add(new Row(g, "Ins_Family", string.Join(" · ", new[] { arch, basename, finetune }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)), false));
        var parameters = facts.Info("model params") ?? info?.SizeLabel;
        if (parameters != null) rows.Add(new Row(g, "Ins_Params", parameters));
        var quant = GgufFileTypes.Name(info?.FileType) ?? facts.Info("file type");
        var bpw = facts.Info("file size") is { } fs && fs.IndexOf("BPW", StringComparison.Ordinal) > 0 && fs.IndexOf('(') is var i && i > 0
            ? fs.Substring(i).Trim('(', ')', ' ') : null;
        if (quant != null) rows.Add(new Row(g, "Ins_Quant", quant + (bpw != null ? " · " + bpw : string.Empty)));
        try
        {
            if (File.Exists(path)) rows.Add(new Row(g, "Ins_FileSize", Units.FormatBytes(new FileInfo(path).Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if ((info?.BlockCount ?? ParseInt(facts.Info("n_layer"))) is { } layers) rows.Add(new Row(g, "Ins_Layers", N(layers)));
        if ((info?.ContextLength ?? ParseInt(facts.Info("n_ctx_train"))) is { } trainCtx) rows.Add(new Row(g, "Ins_TrainCtx", N(trainCtx)));
        if (info?.EmbeddingLength is { } emb) rows.Add(new Row(g, "Ins_Embedding", N(emb)));
        if (info?.HeadCount is { } heads)
            rows.Add(new Row(g, "Ins_Heads", "Q " + N(heads) + (info.HeadCountKv is { } kv && kv != heads ? " · KV " + N(kv) + " (GQA)" : string.Empty)));
        if (info?.ExpertCount is { } experts and > 0)
        {
            var used = info.GetInt(info.Architecture + ".expert_used_count");
            rows.Add(new Row(g, "Ins_Experts", N(experts) + (used is { } u ? " · " + L.F("Ins_ExpertsActive", N(u)) : string.Empty)));
        }
        if (info?.FullAttentionInterval is { } fai) rows.Add(new Row(g, "Ins_Hybrid", L.F("Ins_HybridValue", fai)));
        if (info?.SlidingWindow is { } sw) rows.Add(new Row(g, "Ins_SlidingWindow", N(sw)));
        var vocabModel = info?.Get<string>("tokenizer.ggml.model") ?? facts.Info("vocab type");
        var vocabSize = info?.Metadata.TryGetValue("tokenizer.ggml.tokens", out var tok) == true && tok is GgufArray arr ? (long?)arr.Count : ParseInt(facts.Info("n_vocab"));
        if (vocabModel != null || vocabSize != null)
            rows.Add(new Row(g, "Ins_Vocab", string.Join(" · ", new[] { vocabModel, vocabSize is { } vs ? N(vs) : null }.Where(x => x != null))));
        if (info != null) rows.Add(new Row(g, "Ins_Template", TemplateText(info.ChatTemplate), false));
        var model = _s.Models.Models.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        rows.Add(new Row(g, "Ins_Vision", model?.HasVision == true ? Path.GetFileName(model.MmprojPath) ?? L.Get("Common_Yes") : L.Get("Common_No"), false));
        if (info?.Get<string>("general.license") is { } lic) rows.Add(new Row(g, "Ins_License", lic, false));
        var author = info?.Get<string>("general.organization") ?? info?.Get<string>("general.author");
        if (author != null) rows.Add(new Row(g, "Ins_Author", author, false));
        if (info?.Get<string>("general.quantized_by") is { } qb) rows.Add(new Row(g, "Ins_QuantizedBy", qb, false));
        rows.Add(new Row(g, "Ins_Path", path, false));
    }

    private static string TemplateText(string? template)
    {
        if (string.IsNullOrEmpty(template)) return L.Get("Ins_TemplateNone");
        var features = new List<string>();
        if (template!.IndexOf("tools", StringComparison.Ordinal) >= 0) features.Add(L.Get("Ins_TemplateTools"));
        if (template.IndexOf("enable_thinking", StringComparison.Ordinal) >= 0 || template.IndexOf("<think>", StringComparison.Ordinal) >= 0
            || template.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0)
            features.Add(L.Get("Ins_TemplateThinking"));
        if (template.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) features.Add(L.Get("Ins_TemplateImages"));
        return L.Get("Ins_TemplateYes") + (features.Count > 0 ? ": " + string.Join(", ", features) : string.Empty);
    }

    /// <summary>«29 / 29 · вся модель на GPU», «CPU» или «авто (--fit)».</summary>
    private string? OffloadText(ServerLogFacts f, ServerLaunchOptions? opt)
    {
        var engine = Engine;
        if (engine?.Manifest.Backend == EngineBackend.Cpu) return L.Get("Ins_CpuOnly");
        var strict = opt?.RequireFullOffload == true ? " · " + L.Get("Ins_Strict") : string.Empty;
        if (f.OffloadedLayers is { } o && f.TotalLayers is { } t)
            return $"{N(o)} / {N(t)} · " + L.Get(o >= t ? "Ins_AllOnGpu" : o == 0 ? "Ins_NoneOnGpu" : "Ins_PartOnGpu") + strict;
        if (opt == null) return null;
        return opt.GpuLayers switch
        {
            null when opt.Fit == true => L.Get("Ins_Fit"),
            null => L.Get("Ins_Default"),
            < 0 => L.Get("Ins_AllOnGpu") + strict,
            0 => L.Get("Ins_NoneOnGpu"),
            { } n => N(n),
        };
    }

    /// <summary>Обновить значения на месте, если набор строк тот же; иначе перестроить группы.</summary>
    private void Apply(List<Row> rows)
    {
        var signature = string.Join("|", rows.Select(r => r.Group + "/" + r.Label));
        if (signature == _signature)
        {
            var k = 0;
            foreach (var g in Groups)
                foreach (var row in g.Rows)
                {
                    row.Value = rows[k].Value;
                    row.IsWarn = rows[k].Warn;
                    k++;
                }
            return;
        }
        _signature = signature;
        Groups.Clear();
        foreach (var grp in rows.GroupBy(r => r.Group))
        {
            var g = new InsightGroup(L.Get(grp.Key));
            foreach (var r in grp) g.Rows.Add(new InsightRow(L.Get(r.Label), r.Mono) { Value = r.Value, IsWarn = r.Warn });
            Groups.Add(g);
        }
    }

    /// <summary>Скопировать все сведения текстом (для отчёта об ошибке или сравнения запусков).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CopyAll()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var g in Groups)
        {
            sb.AppendLine(g.Title);
            foreach (var r in g.Rows) sb.Append("  ").Append(r.Label).Append(": ").AppendLine(r.Value?.Replace(Environment.NewLine, "; "));
            sb.AppendLine();
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private static string TagOf(EngineInstall e) =>
        !string.IsNullOrEmpty(e.Manifest.Tag) ? e.Manifest.Tag : e.Manifest.Build is { } b ? "b" + b : "?";

    private static string BackendName(EngineInstall e) =>
        L.Get("Enum_EngineBackend_" + e.Manifest.Backend) + (e.Manifest.BackendVersion != null ? " " + e.Manifest.BackendVersion : string.Empty);

    private static string Buffers(Dictionary<string, double> mib) =>
        string.Join(" · ", mib.OrderByDescending(x => x.Value).Select(x => x.Key + " " + Mib(x.Value)));

    private static string Mib(double mib) => Units.FormatBytes((long)(mib * 1024 * 1024));

    private static string Speed(long? tokens, double? tps) =>
        (tokens is { } t ? L.F("Ins_Tokens", N(t)) : "—") + (tps is { } s ? " · " + s.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s" : string.Empty);

    private static string N(long v) => v.ToString("N0", CultureInfo.CurrentCulture);

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s.Substring(0, i);
    }
}
