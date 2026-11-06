namespace OfflineChatter.Core.Storage;

/// <summary>Корневой объект config/settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>"auto", "ru" или "en".</summary>
    public string Language { get; set; } = "auto";

    public AppearanceSettings Appearance { get; set; } = new();

    public WindowPlacementSettings Window { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();
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
}

public sealed class LoggingSettings
{
    public string MinLevel { get; set; } = "Info";
    public int MaxFileSizeMb { get; set; } = 5;
    public int MaxFiles { get; set; } = 5;

    /// <summary>Писать ли в лог текст сообщений чата. По умолчанию — нет.</summary>
    public bool LogMessageContent { get; set; }
}
