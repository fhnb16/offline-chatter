using System;

namespace OfflineChatter.Core.Diagnostics;

/// <summary>
/// Глобальный фасад журнала приложения. До вызова <see cref="Initialize"/> записи уходят в Debug-вывод.
/// Текст сообщений чата сюда не писать, если не включён LoggingSettings.LogMessageContent.
/// </summary>
public static class Log
{
    private static JsonlLogger? _logger;

    public static JsonlLogger? Logger => _logger;

    public static void Initialize(JsonlLogger logger) => _logger = logger;

    public static void Shutdown()
    {
        var l = _logger;
        _logger = null;
        l?.Dispose();
    }

    public static void Write(LogLevel level, string source, string message, Exception? ex = null, object? data = null)
    {
        var l = _logger;
        if (l != null)
        {
            l.Write(level, source, message, ex, data);
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[{level}] {source}: {message}{(ex != null ? Environment.NewLine + ex : string.Empty)}");
    }

    public static void Trace(string source, string message, object? data = null) => Write(LogLevel.Trace, source, message, null, data);
    public static void Debug(string source, string message, object? data = null) => Write(LogLevel.Debug, source, message, null, data);
    public static void Info(string source, string message, object? data = null) => Write(LogLevel.Info, source, message, null, data);
    public static void Warn(string source, string message, Exception? ex = null, object? data = null) => Write(LogLevel.Warn, source, message, ex, data);
    public static void Error(string source, string message, Exception? ex = null, object? data = null) => Write(LogLevel.Error, source, message, ex, data);
    public static void Fatal(string source, string message, Exception? ex = null, object? data = null) => Write(LogLevel.Fatal, source, message, ex, data);
}
