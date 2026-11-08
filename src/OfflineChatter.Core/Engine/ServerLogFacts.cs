using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Engine;

/// <summary>
/// Сведения о запущенном llama-server, собранные из его вывода: сборка, устройства, сколько слоёв на GPU,
/// буферы модели и KV-кэша, параметры контекста, метаданные модели (print_info) и скорость каждого запроса.
/// Вывод есть у любой версии сервера, в отличие от /metrics и /props, поэтому основа — он.
/// Тексты сообщений модели в выводе не появляются.
/// </summary>
public sealed class ServerLogFacts
{
    private const int MaxModelInfoKeys = 200;

    public int? Build { get; private set; }
    public string? Commit { get; private set; }
    public string? Compiler { get; private set; }

    public int? Threads { get; private set; }
    public int? ThreadsBatch { get; private set; }
    public int? ThreadsTotal { get; private set; }
    /// <summary>Наборы инструкций CPU, которые сборка использует (SSE3, AVX2, …).</summary>
    public List<string> CpuFeatures { get; private set; } = new();

    /// <summary>Устройства, на которые сервер выгружает модель: «Vulkan0 (NVIDIA GeForce RTX 3060)».</summary>
    public List<string> Devices { get; private set; } = new();
    /// <summary>Свободная память устройства при загрузке, МиБ (по имени устройства).</summary>
    public Dictionary<string, long> DeviceFreeMiB { get; private set; } = new(StringComparer.Ordinal);

    public int? OffloadedLayers { get; private set; }
    public int? TotalLayers { get; private set; }

    /// <summary>Буферы весов по устройствам, МиБ (CPU_Mapped, Vulkan0, CUDA0, …).</summary>
    public Dictionary<string, double> ModelBuffersMiB { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> KvBuffersMiB { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> ComputeBuffersMiB { get; private set; } = new(StringComparer.Ordinal);
    public double? KvTotalMiB { get; private set; }
    public int? KvCells { get; private set; }
    public string? KvTypeK { get; private set; }
    public string? KvTypeV { get; private set; }

    /// <summary>Параметры контекста из llama_context: n_ctx, n_ctx_per_seq, n_batch, n_ubatch, flash_attn, n_seq_max…</summary>
    public Dictionary<string, string> Context { get; private set; } = new(StringComparer.Ordinal);
    public int? Slots { get; private set; }

    /// <summary>Метаданные модели из print_info (arch, model params, file type, n_layer…).</summary>
    public Dictionary<string, string> ModelInfo { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Формат чата последнего запроса (определяет разбор вызовов инструментов и рассуждений).</summary>
    public string? ChatFormat { get; private set; }

    // ----- работа модели -----
    public int Requests { get; private set; }
    public long PromptTokensTotal { get; private set; }
    public double PromptMsTotal { get; private set; }
    public long GeneratedTokensTotal { get; private set; }
    public double GenerationMsTotal { get; private set; }
    public int? LastPromptTokens { get; private set; }
    public double? LastPromptTps { get; private set; }
    public int? LastGeneratedTokens { get; private set; }
    public double? LastGenerationTps { get; private set; }
    public double? LastDraftAcceptance { get; private set; }
    public DateTime? LastRequestAt { get; private set; }

    public double? AvgPromptTps => PromptMsTotal > 0 ? PromptTokensTotal / (PromptMsTotal / 1000) : null;
    public double? AvgGenerationTps => GenerationMsTotal > 0 ? GeneratedTokensTotal / (GenerationMsTotal / 1000) : null;

    /// <summary>Все слои модели в видеопамяти (по строке «offloaded N/M layers to GPU»).</summary>
    public bool? FullyOffloaded => OffloadedLayers is { } o && TotalLayers is { } t ? o >= t : null;

    private static readonly Regex BuildRx = new(@"^build:\s*(\d+)\s*\(([0-9a-fA-F]+)\)(?:\s*with\s+(.+?)(?:\s+for\s+\S+)?)?\s*$", RegexOptions.Compiled);
    private static readonly Regex SystemInfoRx = new(@"system_info:\s*n_threads\s*=\s*(\d+)\s*\(n_threads_batch\s*=\s*(\d+)\)\s*/\s*(\d+)\s*\|(.*)$", RegexOptions.Compiled);
    private static readonly Regex FeatureRx = new(@"([A-Za-z0-9_]+)\s*=\s*1\b", RegexOptions.Compiled);
    private static readonly Regex UsingDeviceRx = new(@"using device\s+(\S+)\s+\((.*?)\)(?:\s*(?:\(.*?\))?\s*-\s*(\d+)\s*MiB free)?", RegexOptions.Compiled);
    private static readonly Regex PrintInfoRx = new(@"^(?:print_info|llm_load_print_meta):\s*([^=]+?)\s*=\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex OffloadedRx = new(@"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers to GPU", RegexOptions.Compiled);
    private static readonly Regex ModelBufferRx = new(@"(?:load_tensors|llm_load_tensors):\s*(\S+)\s+model buffer size\s*=\s*([\d.]+)\s*MiB", RegexOptions.Compiled);
    private static readonly Regex KvBufferRx = new(@":\s*(\S+)\s+KV buffer size\s*=\s*([\d.]+)\s*MiB", RegexOptions.Compiled);
    private static readonly Regex ComputeBufferRx = new(@":\s*(\S+)\s+compute buffer size\s*=\s*([\d.]+)\s*MiB", RegexOptions.Compiled);
    private static readonly Regex KvSizeRx = new(@"llama_kv_cache\w*:\s*size\s*=\s*([\d.]+)\s*MiB\s*\(\s*(\d+)\s*cells.*?K\s*\((\w+)\).*?V\s*\((\w+)\)", RegexOptions.Compiled);
    private static readonly Regex ContextRx = new(@"^(?:llama_context|llama_new_context_with_model):\s*(n_ctx|n_ctx_per_seq|n_ctx_seq|n_batch|n_ubatch|flash_attn|n_seq_max|causal_attn|kv_unified|freq_base)\s*=\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex SlotsRx = new(@"n_slots\s*=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex ChatFormatRx = new(@"Chat format:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex PromptEvalRx = new(@"prompt eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens.*?([\d.]+)\s*tokens per second", RegexOptions.Compiled);
    private static readonly Regex EvalRx = new(@"(?<!prompt )\beval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*(?:tokens|runs).*?([\d.]+)\s*tokens per second", RegexOptions.Compiled);
    private static readonly Regex DraftRx = new(@"draft acceptance rate\s*=\s*([\d.]+)", RegexOptions.Compiled);

    /// <summary>Разобрать одну строку вывода сервера.</summary>
    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var s = line.Trim();
        Match m;

        if (Build == null && (m = BuildRx.Match(s)).Success)
        {
            Build = Int(m.Groups[1].Value);
            Commit = m.Groups[2].Value;
            if (m.Groups[3].Success) Compiler = m.Groups[3].Value.Trim();
            return;
        }
        if ((m = SystemInfoRx.Match(s)).Success)
        {
            Threads = Int(m.Groups[1].Value);
            ThreadsBatch = Int(m.Groups[2].Value);
            ThreadsTotal = Int(m.Groups[3].Value);
            CpuFeatures = FeatureRx.Matches(m.Groups[4].Value).Cast<Match>().Select(x => x.Groups[1].Value).Distinct().ToList();
            return;
        }
        if ((m = UsingDeviceRx.Match(s)).Success)
        {
            var name = m.Groups[1].Value;
            var text = name + " (" + m.Groups[2].Value + ")";
            if (!Devices.Contains(text)) Devices.Add(text);
            if (m.Groups[3].Success && long.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free))
                DeviceFreeMiB[name] = free;
            return;
        }
        if ((m = PrintInfoRx.Match(s)).Success)
        {
            if (ModelInfo.Count < MaxModelInfoKeys || ModelInfo.ContainsKey(m.Groups[1].Value))
                ModelInfo[m.Groups[1].Value] = m.Groups[2].Value;
            return;
        }
        if ((m = OffloadedRx.Match(s)).Success)
        {
            OffloadedLayers = Int(m.Groups[1].Value);
            TotalLayers = Int(m.Groups[2].Value);
            return;
        }
        if ((m = ModelBufferRx.Match(s)).Success)
        {
            ModelBuffersMiB[m.Groups[1].Value] = Dbl(m.Groups[2].Value);
            return;
        }
        if ((m = KvSizeRx.Match(s)).Success)
        {
            KvTotalMiB = Dbl(m.Groups[1].Value);
            KvCells = Int(m.Groups[2].Value);
            KvTypeK = m.Groups[3].Value;
            KvTypeV = m.Groups[4].Value;
            return;
        }
        if ((m = KvBufferRx.Match(s)).Success)
        {
            KvBuffersMiB[m.Groups[1].Value] = Dbl(m.Groups[2].Value);
            return;
        }
        if ((m = ComputeBufferRx.Match(s)).Success)
        {
            ComputeBuffersMiB[m.Groups[1].Value] = Dbl(m.Groups[2].Value);
            return;
        }
        if ((m = ContextRx.Match(s)).Success)
        {
            Context[m.Groups[1].Value] = m.Groups[2].Value;
            return;
        }
        if (Slots == null && s.IndexOf("initializing slots", StringComparison.Ordinal) >= 0 && (m = SlotsRx.Match(s)).Success)
        {
            Slots = Int(m.Groups[1].Value);
            return;
        }
        if ((m = ChatFormatRx.Match(s)).Success)
        {
            ChatFormat = m.Groups[1].Value;
            return;
        }
        if ((m = PromptEvalRx.Match(s)).Success)
        {
            var ms = Dbl(m.Groups[1].Value);
            var n = Int(m.Groups[2].Value) ?? 0;
            PromptMsTotal += ms;
            PromptTokensTotal += n;
            LastPromptTokens = n;
            LastPromptTps = Dbl(m.Groups[3].Value);
            return;
        }
        if ((m = EvalRx.Match(s)).Success)
        {
            var ms = Dbl(m.Groups[1].Value);
            var n = Int(m.Groups[2].Value) ?? 0;
            GenerationMsTotal += ms;
            GeneratedTokensTotal += n;
            LastGeneratedTokens = n;
            LastGenerationTps = Dbl(m.Groups[3].Value);
            Requests++;
            LastRequestAt = DateTime.Now;
            return;
        }
        if ((m = DraftRx.Match(s)).Success)
            LastDraftAcceptance = Dbl(m.Groups[1].Value);
    }

    public static ServerLogFacts Parse(IEnumerable<string> lines)
    {
        var f = new ServerLogFacts();
        foreach (var l in lines) f.Feed(l);
        return f;
    }

    /// <summary>Копия для чтения из другого потока.</summary>
    public ServerLogFacts Clone()
    {
        var c = (ServerLogFacts)MemberwiseClone();
        c.CpuFeatures = new List<string>(CpuFeatures);
        c.Devices = new List<string>(Devices);
        c.DeviceFreeMiB = new Dictionary<string, long>(DeviceFreeMiB, StringComparer.Ordinal);
        c.ModelBuffersMiB = new Dictionary<string, double>(ModelBuffersMiB, StringComparer.Ordinal);
        c.KvBuffersMiB = new Dictionary<string, double>(KvBuffersMiB, StringComparer.Ordinal);
        c.ComputeBuffersMiB = new Dictionary<string, double>(ComputeBuffersMiB, StringComparer.Ordinal);
        c.Context = new Dictionary<string, string>(Context, StringComparer.Ordinal);
        c.ModelInfo = new Dictionary<string, string>(ModelInfo, StringComparer.Ordinal);
        return c;
    }

    public string? Info(string key) => ModelInfo.TryGetValue(key, out var v) ? v : null;
    public int? ContextInt(string key) => Context.TryGetValue(key, out var v) ? Int(v) : null;

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>Названия типов квантования из general.file_type (enum llama_ftype).</summary>
public static class GgufFileTypes
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1",
        [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M", [13] = "Q3_K_L", [14] = "Q4_K_S", [15] = "Q4_K_M",
        [16] = "Q5_K_S", [17] = "Q5_K_M", [18] = "Q6_K", [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S",
        [22] = "IQ3_XS", [23] = "IQ3_XXS", [24] = "IQ1_S", [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M",
        [28] = "IQ2_S", [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M", [32] = "BF16", [36] = "TQ1_0",
        [37] = "TQ2_0", [38] = "MXFP4_MOE",
    };

    public static string? Name(int? fileType) => fileType is { } t && Names.TryGetValue(t, out var n) ? n : null;
}
