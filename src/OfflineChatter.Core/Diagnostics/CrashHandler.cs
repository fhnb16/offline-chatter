using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OfflineChatter.Core.Diagnostics;

/// <summary>
/// Перехват необработанных исключений вне UI-потока. Обработчик Dispatcher подключается в WPF-приложении
/// и тоже вызывает <see cref="Report"/>.
/// </summary>
public static class CrashHandler
{
    private static string? _logsDirectory;

    public static void Install(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("AppDomain", e.ExceptionObject as Exception, isTerminating: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("TaskScheduler", e.Exception, isTerminating: false);
            e.SetObserved();
        };
    }

    /// <summary>Пишет исключение в журнал и, если процесс завершается, в отдельный crash-*.log.</summary>
    public static void Report(string origin, Exception? ex, bool isTerminating)
    {
        Log.Write(isTerminating ? LogLevel.Fatal : LogLevel.Error, "Crash", "Unhandled exception from " + origin, ex);

        if (!isTerminating || _logsDirectory == null) return;

        // Фоновый логгер может не успеть дописать очередь — дублируем синхронно в отдельный файл.
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Offline Chatter crash report");
            sb.AppendLine("Time (UTC): " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            sb.AppendLine("Origin: " + origin);
            sb.AppendLine("OS: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
            sb.AppendLine("Process: " + (Environment.Is64BitProcess ? "x64" : "x86") + ", CLR " + Environment.Version);
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "<no exception object>");
            var path = Path.Combine(_logsDirectory, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Последний рубеж: исключение из обработчика падения не должно ничего скрыть.
        }

        Log.Shutdown();
    }
}
