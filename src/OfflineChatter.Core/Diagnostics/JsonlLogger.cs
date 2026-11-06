using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Diagnostics;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

/// <summary>
/// Журнал в формате JSON Lines с ротацией по размеру.
/// Запись идёт в фоновом потоке, вызывающий код не блокируется на диске.
/// Файлы: {name}.jsonl (текущий), {name}.1.jsonl … {name}.{maxFiles-1}.jsonl (старые).
/// </summary>
public sealed class JsonlLogger : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), boundedCapacity: 10_000);
    private readonly Thread _writer;
    private FileStream? _stream;
    private long _dropped;
    private int _disposed;

    public JsonlLogger(string directory, string baseName = "app", long maxBytes = 5 * 1024 * 1024, int maxFiles = 5, LogLevel minLevel = LogLevel.Info)
    {
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));

        _directory = directory;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        MinLevel = minLevel;
        Directory.CreateDirectory(directory);

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "JsonlLogger:" + baseName };
        _writer.Start();
    }

    public LogLevel MinLevel { get; set; }

    public string CurrentFilePath => Path.Combine(_directory, _baseName + ".jsonl");

    public bool IsEnabled(LogLevel level) => level >= MinLevel;

    public void Write(LogLevel level, string source, string message, Exception? exception = null, object? data = null)
    {
        if (!IsEnabled(level) || Volatile.Read(ref _disposed) != 0) return;

        var line = Format(DateTime.UtcNow, level, source, message, exception, data);
        if (!_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped);
    }

    internal static string Format(DateTime utc, LogLevel level, string source, string message, Exception? exception, object? data)
    {
        var sb = new StringBuilder(256);
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
        using (var w = new JsonTextWriter(sw) { Formatting = Formatting.None })
        {
            w.WriteStartObject();
            w.WritePropertyName("t");
            w.WriteValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WritePropertyName("lvl");
            w.WriteValue(level.ToString().ToLowerInvariant());
            w.WritePropertyName("src");
            w.WriteValue(source);
            w.WritePropertyName("msg");
            w.WriteValue(message);
            w.WritePropertyName("tid");
            w.WriteValue(Environment.CurrentManagedThreadId);
            if (exception != null)
            {
                w.WritePropertyName("ex");
                w.WriteValue(exception.ToString());
            }
            if (data != null)
            {
                w.WritePropertyName("data");
                try
                {
                    JToken.FromObject(data).WriteTo(w);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    w.WriteValue("<unserializable: " + ex.Message + ">");
                }
            }
            w.WriteEndObject();
        }
        return sb.ToString();
    }

    private void WriterLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    WriteLine(line);

                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                        WriteLine(Format(DateTime.UtcNow, LogLevel.Warn, "Log", $"Dropped {dropped} log entries (queue overflow)", null, null));

                    if (_queue.Count == 0) _stream?.Flush();
                }
                catch (IOException)
                {
                    // Диск недоступен — теряем запись, но не роняем приложение.
                    CloseStream();
                }
                catch (UnauthorizedAccessException)
                {
                    CloseStream();
                }
            }
        }
        finally
        {
            CloseStream();
        }
    }

    private void WriteLine(string line)
    {
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        var stream = EnsureStream();
        if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes)
        {
            CloseStream();
            Rotate();
            stream = EnsureStream();
        }
        stream.Write(bytes, 0, bytes.Length);
    }

    private FileStream EnsureStream()
    {
        if (_stream != null) return _stream;
        _stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 8192);
        return _stream;
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        _stream = null;
    }

    private string RotatedPath(int index) => Path.Combine(_directory, $"{_baseName}.{index}.jsonl");

    private void Rotate()
    {
        if (_maxFiles == 1)
        {
            File.Delete(CurrentFilePath);
            return;
        }

        var oldest = RotatedPath(_maxFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var i = _maxFiles - 2; i >= 1; i--)
        {
            var src = RotatedPath(i);
            if (File.Exists(src)) File.Move(src, RotatedPath(i + 1));
        }

        if (File.Exists(CurrentFilePath)) File.Move(CurrentFilePath, RotatedPath(1));
    }

    /// <summary>Дописывает очередь на диск и закрывает файл.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
