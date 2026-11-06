using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Diagnostics;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message, string? Exception, string? Data, string File);

/// <summary>Чтение журналов JSONL для просмотрщика (последние N записей из текущего и ротированных файлов).</summary>
public static class LogReader
{
    public static List<LogEntry> ReadTail(string logsDir, string baseName, int max)
    {
        var files = new List<string>();
        var current = Path.Combine(logsDir, baseName + ".jsonl");
        if (File.Exists(current)) files.Add(current);
        for (var i = 1; i < 50; i++)
        {
            var f = Path.Combine(logsDir, $"{baseName}.{i}.jsonl");
            if (!File.Exists(f)) break;
            files.Add(f);
        }

        var result = new List<LogEntry>();
        foreach (var f in files)
        {
            List<string> lines;
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                lines = new List<string>();
                string? line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
            }
            catch (IOException)
            {
                continue;
            }
            for (var i = lines.Count - 1; i >= 0 && result.Count < max; i--)
                if (Parse(lines[i], Path.GetFileName(f)) is { } e) result.Add(e);
            if (result.Count >= max) break;
        }
        result.Reverse();
        return result;
    }

    public static LogEntry? Parse(string line, string file)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var o = JObject.Load(new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None });
            DateTime.TryParse((string?)o["t"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t);
            Enum.TryParse<LogLevel>((string?)o["lvl"], true, out var lvl);
            return new LogEntry(t.ToLocalTime(), lvl, (string?)o["src"] ?? "", (string?)o["msg"] ?? "", (string?)o["ex"],
                o["data"]?.ToString(Formatting.None), file);
        }
        catch (JsonException)
        {
            return new LogEntry(DateTime.MinValue, LogLevel.Info, "?", line, null, null, file);
        }
    }
}

/// <summary>
/// Архив для отчёта об ошибке: журналы, конфиги (без секретов), сведения о системе.
/// Секреты вырезаются по именам полей (*Protected, *Key*, *Token*, *Password*) и по формату "dpapi:…".
/// </summary>
public static class DiagnosticBundle
{
    private static readonly Regex SecretField = new("(protected|apikey|api_key|token|password|secret)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Create(Storage.AppPaths paths, string systemInfo, string targetZip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetZip)!);
        if (File.Exists(targetZip)) File.Delete(targetZip);
        using var zip = ZipFile.Open(targetZip, ZipArchiveMode.Create);

        AddText(zip, "system.txt", systemInfo);
        foreach (var f in Directory.EnumerateFiles(paths.Logs))
        {
            try
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry("logs/" + Path.GetFileName(f), CompressionLevel.Optimal);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
            catch (IOException) { }
        }

        foreach (var f in Directory.EnumerateFiles(paths.Config, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var rel = f.Substring(paths.Config.Length).TrimStart('\\', '/').Replace('\\', '/');
                AddText(zip, "config/" + rel, Redact(File.ReadAllText(f)));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return targetZip;
    }

    /// <summary>Удаляет значения секретных полей из JSON.</summary>
    public static string Redact(string json)
    {
        JToken token;
        try { token = JToken.Parse(json); }
        catch (JsonException) { return "<unparseable>"; }
        void Walk(JToken t)
        {
            switch (t)
            {
                case JObject o:
                    foreach (var p in o.Properties().ToList())
                    {
                        if (p.Value!.Type == JTokenType.String &&(SecretField.IsMatch(p.Name) || ((string?)p.Value)?.StartsWith("dpapi:", StringComparison.Ordinal) == true))
                            p.Value = string.IsNullOrEmpty((string?)p.Value) ? "" : "<redacted>";
                        else Walk(p.Value!);
                    }
                    break;
                case JArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        Walk(token);
        return token.ToString(Formatting.Indented);
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
