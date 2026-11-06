namespace OfflineChatter.Core.Storage;

/// <summary>Корневой объект config/settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>"auto", "ru" или "en".</summary>
    public string Language { get; set; } = "auto";

    public const string DefaultUpdateRepo = "fhnb16/offline-chatter";

    /// <summary>Репозиторий программы на GitHub (owner/repo): обновления и Win7-сборки llama.cpp.</summary>
    public string UpdateRepo { get; set; } = DefaultUpdateRepo;

    /// <summary>Id выбранного по умолчанию пресета системного промпта для новых чатов.</summary>
    public string? DefaultPromptPresetId { get; set; }

    public AttachmentSettings Attachments { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public WindowPlacementSettings Window { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    public NetworkSettings Network { get; set; } = new();

    public Engine.EngineSettings Engine { get; set; } = new();

    public Models.ModelsSettings Models { get; set; } = new();

    public Tools.ToolsSettings Tools { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();
}

public enum ProxyMode
{
    System,
    None,
    Manual,
}

/// <summary>Какой TLS-стек использовать. Auto — эскалация по первой ошибке и запоминание для каждого хоста.</summary>
public enum TlsMode
{
    Auto,
    /// <summary>Уровень 1: системный SChannel.</summary>
    System,
    /// <summary>Уровень 2: SChannel + проверка цепочки по вшитым корневым сертификатам.</summary>
    PinnedRoots,
    /// <summary>Уровень 3: собственный TLS 1.2/1.3 (BouncyCastle).</summary>
    Managed,
}

public sealed class NetworkSettings
{
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>Ручной прокси: http://host:port, socks5://host:port, socks5h://user@host:port.</summary>
    public string ProxyAddress { get; set; } = string.Empty;
    public string ProxyUser { get; set; } = string.Empty;

    /// <summary>Пароль прокси, зашифрованный через <see cref="Secrets"/>.</summary>
    public string ProxyPasswordProtected { get; set; } = string.Empty;

    public TlsMode TlsMode { get; set; } = TlsMode.Auto;

    /// <summary>Офлайн-режим: приложение не выходит в сеть (локальный llama-server работает).</summary>
    public bool OfflineMode { get; set; }

    /// <summary>Id сетевого интерфейса (адаптера) для внешних запросов; пусто — маршрут Windows по умолчанию.</summary>
    public string NetworkInterface { get; set; } = string.Empty;

    /// <summary>Проверять обновления программы при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Hugging Face или зеркало (например, https://hf-mirror.com).</summary>
    public string HfEndpoint { get; set; } = "https://huggingface.co";

    /// <summary>GitHub API или совместимое зеркало.</summary>
    public string GitHubApiEndpoint { get; set; } = "https://api.github.com";

    /// <summary>Префикс-зеркало для скачивания файлов с GitHub (пусто — напрямую).</summary>
    public string GitHubDownloadPrefix { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>Ограничение скорости скачивания, КБ/с (0 — без ограничения).</summary>
    public int SpeedLimitKBps { get; set; }
}

public enum SkinKind
{
    /// <summary>Win7: Aero, Win8/8.1: Aero2, Win10/11: Fluent.</summary>
    Auto,
    Aero,
    Aero2,
    AeroLite,
    Luna,
    Royale,
    Classic,
    Fluent,
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum BackdropKind
{
    /// <summary>Win7: стекло, Win8: нет, Win10: Acrylic, Win11: Mica.</summary>
    Auto,
    None,
    /// <summary>Win7 Aero Glass (DwmExtendFrameIntoClientArea).</summary>
    AeroGlass,
    /// <summary>Win10+ простое размытие (SetWindowCompositionAttribute).</summary>
    Blur,
    /// <summary>Win10: Acrylic через SWCA; Win11 22H2+: DWMSBT_TRANSIENTWINDOW.</summary>
    Acrylic,
    /// <summary>Win11.</summary>
    Mica,
    /// <summary>Win11 22H2+ (DWMSBT_TABBEDWINDOW).</summary>
    MicaAlt,
}

public enum AccentSource
{
    System,
    Custom,
}

public sealed class AppearanceSettings
{
    public SkinKind Skin { get; set; } = SkinKind.Auto;
    public ThemeMode Mode { get; set; } = ThemeMode.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Auto;
    public AccentSource Accent { get; set; } = AccentSource.System;

    /// <summary>#RRGGBB, используется при Accent = Custom.</summary>
    public string CustomAccent { get; set; } = "#0078D4";
}

public sealed class WindowPlacementSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 260;
    public bool InspectorVisible { get; set; } = true;

    /// <summary>Кнопка «закрыть» прячет окно в трей (сервер продолжает работать).</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Глобальная горячая клавиша быстрого вопроса (Ctrl+Alt+Space).</summary>
    public bool QuickAskHotkey { get; set; } = true;

    /// <summary>Мастер первого запуска пройден или пропущен.</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>Разделы, убранные из боковой панели (имена PageKind). Они остаются доступны из настроек.</summary>
    public System.Collections.Generic.List<string> HiddenPages { get; set; } = new() { "Benchmarks", "Logs" };

    /// <summary>Отправка сообщения: Enter (false) или Ctrl+Enter (true).</summary>
    public bool SendWithCtrlEnter { get; set; }
}

/// <summary>Подготовка вложений перед отправкой модели.</summary>
public sealed class AttachmentSettings
{
    /// <summary>Длинная сторона изображения после уменьшения, пикселей (0 — не уменьшать).</summary>
    public int ImageMaxSide { get; set; } = 1536;

    /// <summary>Качество JPEG, 40–100.</summary>
    public int ImageQuality { get; set; } = 85;

    /// <summary>Сохранять PNG для небольших картинок с прозрачностью (скриншоты интерфейса), иначе всегда JPEG.</summary>
    public bool KeepPngWithAlpha { get; set; } = true;
}

public sealed class LoggingSettings
{
    public string MinLevel { get; set; } = "Info";
    public int MaxFileSizeMb { get; set; } = 5;
    public int MaxFiles { get; set; } = 5;

    /// <summary>Писать ли в лог текст сообщений чата. По умолчанию — нет.</summary>
    public bool LogMessageContent { get; set; }
}
