using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;
using OfflineChatter.Shell;
using OfflineChatter.Theming;
using OfflineChatter.ViewModels;
using OfflineChatter.Views;

namespace OfflineChatter;

public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsStore Settings { get; private set; } = null!;
    public static AppServices Services { get; private set; } = null!;

    /// <summary>Фактически зарегистрированная горячая клавиша быстрого вопроса (или пусто).</summary>
    public static string QuickAskHotkeyText { get; private set; } = string.Empty;

    private static MainViewModel? _mainVm;
    private SingleInstance? _single;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private QuickAskWindow? _quick;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Повышенный процесс обновления (Program Files): подменить exe и выйти, без окон и без единственного экземпляра
        if (e.Args.Length > 0 && e.Args[0] == ElevatedUpdate.Switch)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ElevatedUpdate.Run(e.Args));
            return;
        }

        // Один экземпляр на папку: повторный запуск передаёт аргументы работающему окну
        _single = SingleInstance.TryStart(e.Args);
        if (_single == null)
        {
            Shutdown(0);
            return;
        }

        try
        {
            Paths = AppPaths.Detect(AppDomain.CurrentDomain.BaseDirectory);
            Paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Cannot create data folder / Не удалось создать папку данных:\n" + ex.Message, "Offline Chatter",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Settings = new SettingsStore(Paths.SettingsFile);
        Settings.Load();
        L.Initialize(Settings.Current.Language);
        // Форматирование дат и чисел в привязках WPF по языку интерфейса (по умолчанию WPF берёт en-US)
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag)));
        Core.Text.Units.ByteSuffixes = L.Get("Units_Bytes").Split('|');

        var logCfg = Settings.Current.Logging;
        Enum.TryParse<LogLevel>(logCfg.MinLevel, true, out var minLevel);
        Log.Initialize(new JsonlLogger(Paths.Logs, "app", logCfg.MaxFileSizeMb * 1024L * 1024L, logCfg.MaxFiles, minLevel));
        if (Settings.Current.Version < AppSettings.CurrentVersion)
        {
            // Версия 2: на Windows 7 — свой канал сборок llama.cpp (выбор пользователя из версии 2 и новее не трогаем)
            if (Settings.Current.Version < 2 && OsInfo.IsWin7 && Settings.Current.Engine.BuildSource == EngineSource.Official)
                Settings.Current.Engine.BuildSource = EngineSource.AppWin7;
            Settings.Save();
        }
        CrashHandler.Install(Paths.Logs);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Уровень 1 сетевого стека: на Win7/8 явно включаем TLS 1.2, на Win10+ оставляем системные версии (TLS 1.3).
        HttpService.ConfigureGlobalTls(OsInfo.IsWin10OrNewer);

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
        Secrets.LoadMode(Settings.Current.Security);
        if (Secrets.NeedsUnlock)
        {
            // До главного окна: ShutdownMode не должен закрыть приложение после этого диалога
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new UnlockWindow().ShowDialog();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        Services = new AppServices(Paths, Settings);
        Rendering.ImageAttachments.Settings = Settings.Current.Attachments;
        UpdateChecker.CleanupOld(ExePath);
        if (!Settings.Current.Network.OfflineMode) Services.ConnectMcpServers();
        _ = Services.CheckUpdatesInBackgroundAsync();
        _ = Services.CheckEngineUpdateInBackgroundAsync();
        if (Settings.Current.Engine.ExposeApi)
        {
            // Публичный API: сервер поднимается вместе с программой, чтобы другие приложения могли к нему обращаться
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var error = await Services.StartPublicApiAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                if (error != null) Log.Warn("App", "Public API not started: " + error);
            });
        }
        _mainVm = new MainViewModel(Services);

        // Первый запуск: мастер (если ещё нет ни движка, ни моделей)
        if (!Settings.Current.Window.FirstRunDone && Services.Engine.Registry.Active == null && Services.Models.Models.Count == 0)
            _mainVm.Navigate(PageKind.Welcome);

        var window = new MainWindow(_mainVm);
        MainWindow = window;
        window.Closing += OnMainClosing;
        window.SourceInitialized += (_, _) =>
        {
            if (Settings.Current.Window.QuickAskHotkey && HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is { } src)
            {
                // Ctrl+Alt+Space часто занята (PowerToys и т.п.) — пробуем запасную комбинацию
                foreach (var (mods, text) in new[] { (ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+Space"), (ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+Space") })
                {
                    var hk = new GlobalHotkey(src, mods, Key.Space, ShowQuickAsk);
                    if (hk.IsRegistered)
                    {
                        _hotkey = hk;
                        QuickAskHotkeyText = text;
                        Log.Info("App", "Quick ask hotkey: " + text);
                        break;
                    }
                }
            }
        };
        window.Show();

        _tray = new TrayIcon(ShowMainWindow, ShowQuickAsk, () => { ShowMainWindow(); _mainVm.NewChatCommand.Execute(null); }, ExitApp);
        Services.Downloads.JobChanged += (_, job) =>
        {
            if (job.State is not (Core.Net.DownloadState.Completed or Core.Net.DownloadState.Failed)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainWindow?.IsActive == true) return;
                _tray?.Notify(L.Get(job.State == Core.Net.DownloadState.Completed ? "Notify_DownloadDone" : "Notify_DownloadFailed"), job.DisplayName);
            }));
        };
        _single.ArgumentsReceived += args => Dispatcher.BeginInvoke(new Action(() => HandleArgs(args, fromOtherInstance: true)));

        UpdateJumpList();
        window.ContentRendered += (_, _) =>
        {
            HandleArgs(e.Args, fromOtherInstance: false);
            // Запуск из «Загрузок», с рабочего стола или из временной папки архиватора — предложить перенос
            Dispatcher.BeginInvoke(new Action(() => _ = Relocation.OfferIfNeededAsync(Services)), DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>
    /// Аргументы командной строки (и из Jump List / повторного запуска):
    /// --page Kind, --prompt "текст", --new, --chat id, --quick.
    /// </summary>
    private void HandleArgs(string[] args, bool fromOtherInstance)
    {
        if (_mainVm == null) return;
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Has("--quick"))
        {
            ShowQuickAsk();
            return;
        }
        if (fromOtherInstance) ShowMainWindow();

        if (Value("--page") is { } page && Enum.TryParse<PageKind>(page, true, out var kind)) _mainVm.Navigate(kind);
        // --appearance Skin:Mode:Backdrop — сменить оформление на лету (проверка тем: tools/theme-tour.ps1)
        if (Value("--appearance") is { } look)
        {
            var parts = look.Split(':');
            var a = Settings.Current.Appearance;
            if (parts.Length > 0 && Enum.TryParse<SkinKind>(parts[0], true, out var skin)) a.Skin = skin;
            if (parts.Length > 1 && Enum.TryParse<ThemeMode>(parts[1], true, out var mode)) a.Mode = mode;
            if (parts.Length > 2 && Enum.TryParse<BackdropKind>(parts[2], true, out var backdrop)) a.Backdrop = backdrop;
            Settings.Save();
            ThemeManager.Apply(a);
        }
        if (Value("--section") is { } section && Enum.TryParse<SettingsSection>(section, true, out var sec))
            _mainVm.GetPage<SettingsViewModel>()?.Show(sec);
        if (Value("--chat") is { } chatId && _mainVm.Chat.Chats.FirstOrDefault(c => c.Session.Id == chatId) is { } chat)
        {
            _mainVm.Chat.SelectedChat = chat;
            _mainVm.Navigate(PageKind.Chat);
        }
        if (Has("--new")) _mainVm.NewChatCommand.Execute(null);
        if (Value("--prompt") is { } prompt)
        {
            _mainVm.NewChatCommand.Execute(null);
            _mainVm.Chat.InputText = prompt;
            if (_mainVm.Chat.SendCommand.CanExecute(null)) _mainVm.Chat.SendCommand.Execute(null);
        }
    }

    public static string ExePath => Assembly.GetExecutingAssembly().Location;

    /// <summary>Перезапуск программы (новый процесс дождётся, пока этот освободит мьютекс единственного экземпляра).</summary>
    public static void Restart(string? extraArgs = null)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath, ("--restarted " + extraArgs).Trim()) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("App", "Restart failed", ex);
            return;
        }
        if (Current is App app) app._exiting = true;
        Current.MainWindow?.Close();
        Current.Shutdown();
    }

    public static void ShowMainWindow()
    {
        if (Current.MainWindow is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        // Обход запрета на перехват фокуса: кратко делаем окно поверх всех
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    private void ShowQuickAsk()
    {
        if (_mainVm == null) return;
        if (_quick is { IsVisible: true })
        {
            _quick.Activate();
            return;
        }
        _quick = new QuickAskWindow(Services, _mainVm);
        _quick.Closed += (_, _) => _quick = null;
        _quick.Show();
        _quick.Activate();
    }

    private void OnMainClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !Settings.Current.Window.CloseToTray) return;
        e.Cancel = true;
        MainWindow?.Hide();
        _tray?.Notify("Offline Chatter", L.Get("Notify_InTray"));
    }

    private void ExitApp()
    {
        _exiting = true;
        MainWindow?.Close();
        Shutdown();
    }

    /// <summary>Jump List (Win7+): «Новый чат», «Быстрый вопрос» и последние чаты.</summary>
    public static void UpdateJumpList()
    {
        try
        {
            var exe = Assembly.GetExecutingAssembly().Location;
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            list.JumpItems.Add(new JumpTask { Title = L.Get("Chat_New"), Arguments = "--new", ApplicationPath = exe, IconResourcePath = exe });
            list.JumpItems.Add(new JumpTask { Title = L.Get("Tray_QuickAsk"), Arguments = "--quick", ApplicationPath = exe, IconResourcePath = exe });
            if (_mainVm != null)
            {
                foreach (var c in _mainVm.Chat.Chats.Where(c => c.Session.Messages.Count > 0).OrderByDescending(c => c.Session.UpdatedAt).Take(6))
                {
                    list.JumpItems.Add(new JumpTask
                    {
                        Title = c.Title.Length > 60 ? c.Title.Substring(0, 60) + "…" : c.Title,
                        Arguments = "--chat " + c.Session.Id,
                        ApplicationPath = exe,
                        IconResourcePath = exe,
                        CustomCategory = L.Get("Jump_Recent"),
                    });
                }
            }
            JumpList.SetJumpList(Current, list);
            list.Apply();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Debug("App", "Jump list not updated: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single == null)
        {
            base.OnExit(e);
            return;
        }
        UpdateJumpList();
        _hotkey?.Dispose();
        _tray?.Dispose();
        Settings?.Save();
        Services?.Dispose();
        _single.Dispose();
        Log.Info("App", "Exit", new { code = e.ApplicationExitCode });
        Log.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashHandler.Report("Dispatcher", e.Exception, isTerminating: false);
        MessageBox.Show(L.F("App_UnexpectedError", Paths.Logs, e.Exception.Message),
            "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
