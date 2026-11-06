using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;
using OfflineChatter.ViewModels;

namespace OfflineChatter;

public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsStore Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Paths = AppPaths.Detect(AppDomain.CurrentDomain.BaseDirectory);
            Paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // TODO(l10n)
            MessageBox.Show("Не удалось создать папку данных:\n" + ex.Message, "Offline Chatter",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Settings = new SettingsStore(Paths.SettingsFile);
        Settings.Load();

        var logCfg = Settings.Current.Logging;
        Enum.TryParse<LogLevel>(logCfg.MinLevel, true, out var minLevel);
        Log.Initialize(new JsonlLogger(Paths.Logs, "app", logCfg.MaxFileSizeMb * 1024L * 1024L, logCfg.MaxFiles, minLevel));
        CrashHandler.Install(Paths.Logs);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // На Win7 TLS 1.2 по умолчанию выключен для .NET — включаем явно (уровень 1 сетевого стека).
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

        Log.Info("App", "Starting", new
        {
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            os = OsInfo.DisplayName,
            clr = Environment.Version.ToString(),
            process = Environment.Is64BitProcess ? "x64" : "x86",
            portable = Paths.IsPortable,
            root = Paths.Root,
        });

        ThemeManager.Initialize(Settings.Current.Appearance);

        var window = new MainWindow(new MainViewModel(Settings, Paths));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings?.Save();
        Log.Info("App", "Exit", new { code = e.ApplicationExitCode });
        Log.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashHandler.Report("Dispatcher", e.Exception, isTerminating: false);
        // TODO(l10n)
        MessageBox.Show("Произошла непредвиденная ошибка. Подробности записаны в журнал:\n" + Paths.Logs + "\n\n" + e.Exception.Message,
            "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
