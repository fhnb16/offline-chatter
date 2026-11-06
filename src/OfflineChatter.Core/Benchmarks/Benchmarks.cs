using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Benchmarks;

/// <summary>Снимок системы в отчёте — чтобы результаты разных машин и сборок можно было сравнивать.</summary>
public sealed class SystemSnapshot
{
    public string Os { get; set; } = Environment.OSVersion.ToString();
    public string Cpu { get; set; } = string.Empty;
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public long RamBytes { get; set; }
    public List<string> Gpus { get; set; } = new();
    public string? NvidiaDriver { get; set; }
    public string? Engine { get; set; }
    public string? EngineBackend { get; set; }
    public string? AppVersion { get; set; }

    public static SystemSnapshot From(HardwareInfo hw, EngineInstall? engine, string osName, string appVersion) => new()
    {
        Os = osName,
        Cpu = hw.CpuName,
        PhysicalCores = hw.PhysicalCores,
        LogicalCores = hw.LogicalCores,
        RamBytes = hw.TotalRam,
        Gpus = hw.Gpus.Select(g => $"{g.Name} ({g.DedicatedMemory >> 20} MB, driver {g.DriverVersion})").ToList(),
        NvidiaDriver = hw.NvidiaDriver,
        Engine = engine?.DisplayName,
        EngineBackend = engine?.Manifest.Backend.ToString(),
        AppVersion = appVersion,
    };
}

public sealed class BenchRow
{
    /// <summary>pp512, tg128, chat:prompt-1…</summary>
    public string Test { get; set; } = string.Empty;
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double? Median { get; set; }
    public double? P95 { get; set; }
    public string Unit { get; set; } = "tok/s";
    public List<double> Samples { get; set; } = new();
}

public sealed class BenchReport
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>"llama-bench" или "chat-bench".</summary>
    public string Kind { get; set; } = "llama-bench";
    public string Model { get; set; } = string.Empty;
    public long? ModelSize { get; set; }
    public string? Quant { get; set; }
    public string? Parameters { get; set; }
    public SystemSnapshot System { get; set; } = new();
    public List<BenchRow> Rows { get; set; } = new();
    public string? Notes { get; set; }

    public string Title => $"{CreatedAt:g} · {Kind} · {Path.GetFileNameWithoutExtension(Model)} · {System.EngineBackend}";
}

public static class Stats
{
    public static double Median(IReadOnlyList<double> v) => Percentile(v, 0.5);

    public static double Percentile(IReadOnlyList<double> v, double p)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        var pos = (s.Count - 1) * p;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public static double Mean(IReadOnlyList<double> v) => v.Count == 0 ? 0 : v.Average();

    public static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
    }
}

/// <summary>llama-bench -o json: скорость обработки промпта (pp) и генерации (tg).</summary>
public static class LlamaBench
{
    public static string BuildArgs(string model, int prompt, int gen, int reps, int? threads, int? gpuLayers, bool? flashAttn) =>
        string.Join(" ", new[]
        {
            "-m " + ServerLaunchOptions.Quote(model),
            "-p " + prompt.ToString(CultureInfo.InvariantCulture),
            "-n " + gen.ToString(CultureInfo.InvariantCulture),
            "-r " + reps.ToString(CultureInfo.InvariantCulture),
            threads is { } t ? "-t " + t.ToString(CultureInfo.InvariantCulture) : null,
            gpuLayers is { } g ? "-ngl " + g.ToString(CultureInfo.InvariantCulture) : null,
            flashAttn is { } fa ? "-fa " + (fa ? "1" : "0") : null,
            "-o json",
        }.Where(x => x != null));

    public static List<BenchRow> Parse(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0) throw new InvalidDataException("llama-bench produced no JSON");
        var arr = JArray.Parse(json.Substring(start));
        return arr.Select(r =>
        {
            var p = (int?)r["n_prompt"] ?? 0;
            var g = (int?)r["n_gen"] ?? 0;
            var samples = (r["samples_ts"] as JArray)?.Select(x => (double)x).ToList() ?? new List<double>();
            return new BenchRow
            {
                Test = p > 0 && g == 0 ? "pp" + p : g > 0 && p == 0 ? "tg" + g : $"pp{p}+tg{g}",
                Mean = (double?)r["avg_ts"] ?? 0,
                StdDev = (double?)r["stddev_ts"] ?? 0,
                Samples = samples,
                Median = samples.Count > 0 ? Stats.Median(samples) : null,
            };
        }).ToList();
    }

    public static async Task<List<BenchRow>> RunAsync(string benchExe, string args, Action<string>? onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(benchExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(benchExe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } }))
            await Task.Run(() => p.WaitForExit(), ct).ConfigureAwait(false);
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new IOException($"llama-bench exited with code {p.ExitCode}");
        return Parse(stdout.ToString());
    }
}

/// <summary>
/// Chat-bench: реальные запросы чата (как у пользователя) N раз — медиана и p95 для
/// времени до первого токена и скорости генерации. Работает и для облачных API.
/// </summary>
public static class ChatBench
{
    public static readonly string[] DefaultPrompts =
    {
        "Explain in three sentences what a neural network is.",
        "Write a Python function that checks whether a number is prime.",
        "Translate to French: The weather is nice today and we are going to the park.",
    };

    public static async Task<List<BenchRow>> RunAsync(ChatClient client, string? model, IReadOnlyList<string> prompts, int runs, int maxTokens,
        IProgress<string>? progress, CancellationToken ct)
    {
        var ttft = new List<double>();
        var gen = new List<double>();
        var pp = new List<double>();
        var total = new List<double>();
        for (var r = 0; r < runs; r++)
        {
            for (var i = 0; i < prompts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"{r + 1}/{runs} · prompt {i + 1}/{prompts.Count}");
                var req = new ChatRequest { Model = model, Temperature = 0, MaxTokens = maxTokens, Thinking = false, Seed = 42 };
                req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompts[i] });
                var res = await client.StreamAsync(req, _ => { }, ct).ConfigureAwait(false);
                ttft.Add(res.Stats.TimeToFirstTokenMs);
                total.Add(res.Stats.TotalMs);
                if (res.Stats.TokensPerSecond is { } tps) gen.Add(tps);
                if (res.Stats.PromptTokensPerSecond is { } ptps) pp.Add(ptps);
            }
        }

        BenchRow Row(string name, List<double> v, string unit) => new()
        {
            Test = name, Unit = unit, Samples = v, Mean = Stats.Mean(v), StdDev = Stats.StdDev(v),
            Median = Stats.Median(v), P95 = Stats.Percentile(v, 0.95),
        };

        var rows = new List<BenchRow> { Row("generation", gen, "tok/s") };
        if (pp.Count > 0) rows.Add(Row("prompt", pp, "tok/s"));
        rows.Add(Row("time to first token", ttft, "ms"));
        rows.Add(Row("total", total, "ms"));
        return rows;
    }
}

/// <summary>Отчёты в reports/*.json + экспорт в HTML, Markdown, CSV.</summary>
public sealed class ReportStore
{
    private readonly string _dir;

    public ReportStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public string PathOf(BenchReport r) => Path.Combine(_dir, r.Id + "-" + r.Kind + ".json");

    public void Save(BenchReport r) => JsonFile.WriteAtomic(PathOf(r), r);

    public IReadOnlyList<BenchReport> List()
    {
        var list = new List<BenchReport>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                if (JsonFile.Read<BenchReport>(f) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException)
            {
                Log.Warn("Reports", "Bad report " + f, ex);
            }
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public void Delete(BenchReport r)
    {
        try { File.Delete(PathOf(r)); }
        catch (IOException) { }
    }

    private static string F(double v) => v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);

    public static string ToMarkdown(IReadOnlyList<BenchReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Offline Chatter — benchmark report").AppendLine();
        foreach (var r in reports)
        {
            sb.AppendLine($"## {r.Title}").AppendLine();
            sb.AppendLine($"- Model: `{Path.GetFileName(r.Model)}` {(r.ModelSize is { } s ? $"({s >> 20} MB)" : string.Empty)}");
            if (r.Parameters != null) sb.AppendLine($"- Parameters: `{r.Parameters}`");
            sb.AppendLine($"- System: {r.System.Os}; {r.System.Cpu} ({r.System.PhysicalCores}C/{r.System.LogicalCores}T); RAM {r.System.RamBytes >> 30} GB");
            if (r.System.Gpus.Count > 0) sb.AppendLine($"- GPU: {string.Join("; ", r.System.Gpus)}");
            sb.AppendLine($"- Engine: {r.System.Engine}").AppendLine();
            sb.AppendLine("| Test | Mean | ± | Median | p95 | Unit |").AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var row in r.Rows)
                sb.AppendLine($"| {row.Test} | {F(row.Mean)} | {F(row.StdDev)} | {(row.Median is { } m ? F(m) : "—")} | {(row.P95 is { } p ? F(p) : "—")} | {row.Unit} |");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToCsv(IReadOnlyList<BenchReport> reports)
    {
        var sb = new StringBuilder("report,kind,model,backend,test,mean,stddev,median,p95,unit\n");
        string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        foreach (var r in reports)
            foreach (var row in r.Rows)
                sb.AppendLine(string.Join(",", Q(r.Id), Q(r.Kind), Q(Path.GetFileName(r.Model)), Q(r.System.EngineBackend ?? ""), Q(row.Test),
                    F(row.Mean), F(row.StdDev), row.Median is { } m ? F(m) : "", row.P95 is { } p ? F(p) : "", Q(row.Unit)));
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<BenchReport> reports)
    {
        string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Offline Chatter — benchmarks</title><style>")
          .Append("body{font-family:'Segoe UI',system-ui,sans-serif;margin:32px;color:#1b1b1b;background:#fafafa}h2{margin-top:32px}")
          .Append("table{border-collapse:collapse;margin:8px 0 16px}th,td{border:1px solid #ddd;padding:6px 10px;text-align:right}th{background:#f0f0f0}td:first-child,th:first-child{text-align:left}")
          .Append(".meta{color:#555;font-size:14px}.bar{display:inline-block;height:10px;background:#0078d4;border-radius:3px;margin-left:8px;vertical-align:middle}")
          .Append("@media (prefers-color-scheme: dark){body{background:#202020;color:#eee}th{background:#2d2d2d}th,td{border-color:#444}.meta{color:#aaa}}")
          .Append("</style></head><body><h1>Offline Chatter — benchmark report</h1>");
        var maxTps = reports.SelectMany(r => r.Rows).Where(r => r.Unit == "tok/s").Select(r => r.Mean).DefaultIfEmpty(1).Max();
        foreach (var r in reports)
        {
            sb.Append("<h2>").Append(E(r.Title)).Append("</h2><div class=\"meta\">")
              .Append("Model: <b>").Append(E(Path.GetFileName(r.Model))).Append("</b><br>")
              .Append("System: ").Append(E($"{r.System.Os}; {r.System.Cpu}; RAM {r.System.RamBytes >> 30} GB")).Append("<br>");
            if (r.System.Gpus.Count > 0) sb.Append("GPU: ").Append(E(string.Join("; ", r.System.Gpus))).Append("<br>");
            sb.Append("Engine: ").Append(E(r.System.Engine)).Append("<br>");
            if (r.Parameters != null) sb.Append("Parameters: <code>").Append(E(r.Parameters)).Append("</code>");
            sb.Append("</div><table><tr><th>Test</th><th>Mean</th><th>±</th><th>Median</th><th>p95</th><th>Unit</th></tr>");
            foreach (var row in r.Rows)
            {
                var bar = row.Unit == "tok/s" && maxTps > 0 ? $"<span class=\"bar\" style=\"width:{(int)(120 * row.Mean / maxTps)}px\"></span>" : string.Empty;
                sb.Append("<tr><td>").Append(E(row.Test)).Append("</td><td>").Append(F(row.Mean)).Append(bar).Append("</td><td>").Append(F(row.StdDev))
                  .Append("</td><td>").Append(row.Median is { } m ? F(m) : "—").Append("</td><td>").Append(row.P95 is { } p ? F(p) : "—")
                  .Append("</td><td>").Append(E(row.Unit)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
