using System;
using System.Runtime.InteropServices;

namespace OfflineChatter.Interop;

// Весь P/Invoke приложения. У каждой функции указана минимальная версия Windows.

[StructLayout(LayoutKind.Sequential)]
internal struct MARGINS
{
    public int cxLeftWidth;
    public int cxRightWidth;
    public int cyTopHeight;
    public int cyBottomHeight;

    public static MARGINS All(int v) => new() { cxLeftWidth = v, cxRightWidth = v, cyTopHeight = v, cyBottomHeight = v };
}

[StructLayout(LayoutKind.Sequential)]
internal struct RTL_OSVERSIONINFOW
{
    public uint dwOSVersionInfoSize;
    public uint dwMajorVersion;
    public uint dwMinorVersion;
    public uint dwBuildNumber;
    public uint dwPlatformId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szCSDVersion;
}

internal enum AccentState
{
    Disabled = 0,
    EnableGradient = 1,
    EnableTransparentGradient = 2,
    EnableBlurBehind = 3,
    /// <summary>Win10 1803+ (17134).</summary>
    EnableAcrylicBlurBehind = 4,
    EnableHostBackdrop = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    public AccentState AccentState;
    public int AccentFlags;
    /// <summary>Цвет подкраски в формате AABBGGRR.</summary>
    public uint GradientColor;
    public int AnimationId;
}

internal enum WindowCompositionAttribute
{
    WCA_ACCENT_POLICY = 19,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    public WindowCompositionAttribute Attribute;
    public IntPtr Data;
    public int SizeOfData;
}

internal static class DwmApi
{
    /// <summary>Win10 1809–20H1 (до 18985).</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    /// <summary>Win10 20H1+ (18985+).</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    /// <summary>Win11 22000+.</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    /// <summary>Win11 22000+.</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    /// <summary>Win11 22H2 (22523+).</summary>
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    /// <summary>Недокументированный атрибут Mica для Win11 21H2 (22000).</summary>
    public const int DWMWA_MICA_EFFECT = 1029;

    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;      // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4;    // Mica Alt

    /// <summary>Vista+.</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>Vista+. На Win8+ всегда true.</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    /// <summary>Vista+. Цвет 0xAARRGGBB.</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetColorizationColor(out uint colorization, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

    /// <summary>Vista+. Набор атрибутов зависит от версии ОС.</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win7+: прямоугольник системных кнопок заголовка (в координатах окна, физические пиксели).</summary>
    public const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    /// <summary>Vista+. Чтение целочисленного атрибута (тёмный режим, тип подложки) — для сверки с желаемым.</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", PreserveSig = true)]
    public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

internal static class User32
{
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_THEMECHANGED = 0x031A;
    public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    public const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;

    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;
    public const int WM_NCHITTEST = 0x0084;
    public const int HTMAXBUTTON = 9;
    public const int WM_ACTIVATE = 0x0006;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>Win2000+. С SWP_FRAMECHANGED заставляет DWM перерисовать рамку и подложку.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    /// <summary>Недокументированная, Win7+ (ACCENT_POLICY работает с Win10).</summary>
    [DllImport("user32.dll", PreserveSig = true)]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}

internal static class UxTheme
{
    /// <summary>XP+. false — включена классическая тема (визуальные стили отключены).</summary>
    [DllImport("uxtheme.dll", PreserveSig = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsThemeActive();
}

internal static class NtDll
{
    /// <summary>Возвращает настоящую версию ОС независимо от манифеста и режима совместимости.</summary>
    [DllImport("ntdll.dll", PreserveSig = true)]
    public static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW info);
}
