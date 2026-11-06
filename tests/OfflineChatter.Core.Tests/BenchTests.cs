using System.IO;
using System.IO.Compression;
using System.Linq;
using OfflineChatter.Core.Benchmarks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class BenchTests
{
    // Реальный вывод llama-bench b12176 (сокращён)
    private const string BenchJson = @"main: some log line
[
  { ""n_prompt"": 64, ""n_gen"": 0, ""avg_ts"": 688.752769, ""stddev_ts"": 10.841676, ""samples_ts"": [ 696.419, 681.087 ], ""backends"": ""CPU"" },
  { ""n_prompt"": 0, ""n_gen"": 16, ""avg_ts"": 61.959582, ""stddev_ts"": 0.546355, ""samples_ts"": [ 61.5733, 62.3459 ] }
]";

    [Fact]
    public void Parses_Llama_Bench_Json()
    {
        var rows = LlamaBench.Parse(BenchJson);
        Assert.Equal(new[] { "pp64", "tg16" }, rows.Select(r => r.Test));
        Assert.Equal(688.752769, rows[0].Mean, 4);
        Assert.Equal(2, rows[1].Samples.Count);
        Assert.Equal((61.5733 + 62.3459) / 2, rows[1].Median!.Value, 4);
    }

    [Fact]
    public void Bench_Args_Quote_Paths()
    {
        var a = LlamaBench.BuildArgs(@"D:\My Models\m.gguf", 512, 128, 3, 8, -1, true);
        Assert.Equal("-m \"D:\\My Models\\m.gguf\" -p 512 -n 128 -r 3 -t 8 -ngl -1 -fa 1 -o json", a);
    }

    [Fact]
    public void Percentiles()
    {
        var v = new[] { 10.0, 20, 30, 40, 50 };
        Assert.Equal(30, Stats.Median(v));
        Assert.Equal(48, Stats.Percentile(v, 0.95), 6);
        Assert.Equal(0, Stats.StdDev(new[] { 5.0 }));
    }

    [Fact]
    public void Report_Roundtrip_And_Exports()
    {
        using var tmp = new TempDir();
        var store = new ReportStore(tmp.Path);
        var r = new BenchReport { Model = @"C:\m\x-Q4_K_M.gguf", Rows = LlamaBench.Parse(BenchJson), System = new SystemSnapshot { Cpu = "CPU <test>", EngineBackend = "Cpu" } };
        store.Save(r);
        var back = Assert.Single(store.List());
        Assert.Equal(2, back.Rows.Count);

        var md = ReportStore.ToMarkdown(new[] { back });
        Assert.Contains("| pp64 | 689 |", md);
        var csv = ReportStore.ToCsv(new[] { back });
        Assert.Equal(3, csv.Trim().Split('\n').Length);
        var html = ReportStore.ToHtml(new[] { back });
        Assert.Contains("CPU &lt;test&gt;", html);
    }

    [Fact]
    public void Redacts_Secrets_In_Configs()
    {
        var json = "{\"Network\":{\"ProxyPasswordProtected\":\"dpapi:AAA\",\"ProxyUser\":\"bob\"},\"Providers\":[{\"ApiKeyProtected\":\"dpapi:BBB\",\"Name\":\"x\"}],\"HfTokenProtected\":\"\",\"other\":\"dpapi:CCC\"}";
        var red = DiagnosticBundle.Redact(json);
        Assert.DoesNotContain("AAA", red);
        Assert.DoesNotContain("BBB", red);
        Assert.DoesNotContain("CCC", red);
        Assert.Contains("bob", red);
    }

    [Fact]
    public void Bundle_Contains_Logs_And_Redacted_Config()
    {
        using var tmp = new TempDir();
        var paths = new AppPaths(tmp.Combine("app"), true);
        paths.EnsureCreated();
        File.WriteAllText(Path.Combine(paths.Logs, "app.jsonl"), "{\"t\":\"2026-01-01T00:00:00.000Z\",\"lvl\":\"info\",\"src\":\"x\",\"msg\":\"hello\"}\n");
        File.WriteAllText(paths.SettingsFile, "{\"Models\":{\"HfTokenProtected\":\"dpapi:SECRET\"}}");
        var zipPath = DiagnosticBundle.Create(paths, "sysinfo", tmp.Combine("diag.zip"));
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("logs/app.jsonl"));
        using var r = new StreamReader(zip.GetEntry("config/settings.json")!.Open());
        Assert.DoesNotContain("SECRET", r.ReadToEnd());

        var entries = LogReader.ReadTail(paths.Logs, "app", 100);
        var e = Assert.Single(entries);
        Assert.Equal("hello", e.Message);
        Assert.Equal(LogLevel.Info, e.Level);
    }
}
