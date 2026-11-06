using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class LoggerTests
{
    [Fact]
    public void Format_ProducesSingleLineJson()
    {
        var line = JsonlLogger.Format(new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc), LogLevel.Warn, "Net", "multi\nline",
            new InvalidOperationException("boom"), new { host = "github.com", attempt = 2 });

        Assert.DoesNotContain("\n", line);
        // DateParseHandling.None: проверяем строку как она записана, без превращения в DateTime
        var o = JObject.Load(new Newtonsoft.Json.JsonTextReader(new StringReader(line)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None });
        Assert.Equal("2026-01-02T03:04:05.006Z", (string?)o["t"]);
        Assert.Equal("warn", (string?)o["lvl"]);
        Assert.Equal("Net", (string?)o["src"]);
        Assert.Equal("multi\nline", (string?)o["msg"]);
        Assert.Contains("boom", (string?)o["ex"]);
        Assert.Equal("github.com", (string?)o["data"]!["host"]);
        Assert.Equal(2, (int)o["data"]!["attempt"]!);
    }

    [Fact]
    public void BelowMinLevel_IsNotWritten()
    {
        using var tmp = new TempDir();
        using (var log = new JsonlLogger(tmp.Path, "app", minLevel: LogLevel.Info))
        {
            log.Write(LogLevel.Debug, "T", "hidden");
            log.Write(LogLevel.Info, "T", "shown");
        }

        var lines = File.ReadAllLines(tmp.Combine("app.jsonl"));
        Assert.Single(lines);
        Assert.Contains("shown", lines[0]);
    }

    [Fact]
    public void Rotation_KeepsAtMostMaxFiles()
    {
        using var tmp = new TempDir();
        using (var log = new JsonlLogger(tmp.Path, "app", maxBytes: 2048, maxFiles: 3, minLevel: LogLevel.Trace))
        {
            for (var i = 0; i < 200; i++)
                log.Write(LogLevel.Info, "T", "message number " + i + new string('x', 40));
        }

        var files = Directory.GetFiles(tmp.Path, "app*.jsonl").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "app.1.jsonl", "app.2.jsonl", "app.jsonl" }, files);
        foreach (var f in files)
            Assert.True(new FileInfo(tmp.Combine(f!)).Length <= 2048, f);

        // Самая свежая запись — в текущем файле
        Assert.Contains("message number 199", File.ReadAllText(tmp.Combine("app.jsonl")));
    }
}
