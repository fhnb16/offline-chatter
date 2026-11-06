using System;
using System.Runtime.InteropServices;
using OfflineChatter.Interop;

namespace OfflineChatter.Theming;

public enum WindowsGeneration
{
    Older,
    Win7,
    Win8,
    Win81,
    Win10,
    Win11,
}

/// <summary>Версия Windows и доступные для неё возможности оформления.</summary>
public static class OsInfo
{
    static OsInfo()
    {
        var v = new RTL_OSVERSIONINFOW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>() };
        try
        {
            if (NtDll.RtlGetVersion(ref v) == 0)
            {
                Version = new Version((int)v.dwMajorVersion, (int)v.dwMinorVersion, (int)v.dwBuildNumber);
                ServicePack = v.szCSDVersion ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        Version ??= Environment.OSVersion.Version;
        ServicePack ??= Environment.OSVersion.ServicePack;

        Generation = (Version.Major, Version.Minor) switch
        {
            (6, 1) => WindowsGeneration.Win7,
            (6, 2) => WindowsGeneration.Win8,
            (6, 3) => WindowsGeneration.Win81,
            (10, _) when Version.Build >= 22000 => WindowsGeneration.Win11,
            (10, _) => WindowsGeneration.Win10,
            _ when Version.Major > 10 => WindowsGeneration.Win11,
            _ => WindowsGeneration.Older,
        };
    }

    public static Version Version { get; }
    public static string ServicePack { get; }
    public static WindowsGeneration Generation { get; }
    public static int Build => Version.Build;

    public static bool IsWin7 => Generation == WindowsGeneration.Win7;
    public static bool IsWin8OrNewer => Generation >= WindowsGeneration.Win8;
    public static bool IsWin10OrNewer => Generation >= WindowsGeneration.Win10;
    public static bool IsWin11OrNewer => Generation >= WindowsGeneration.Win11;

    /// <summary>Тёмный заголовок окна через DWM: Win10 1809 (17763)+.</summary>
    public static bool SupportsImmersiveDarkMode => IsWin10OrNewer && Build >= 17763;

    /// <summary>
    /// Acrylic через SetWindowCompositionAttribute: только Win10 1803 (17134)+.
    /// На Win11 SWCA с расширенной рамкой рисует чёрный фон — там используется DWMWA_SYSTEMBACKDROP_TYPE.
    /// </summary>
    public static bool SupportsWin10Acrylic => Generation == WindowsGeneration.Win10 && Build >= 17134;

    /// <summary>Размытие через SetWindowCompositionAttribute: только Win10 (на Win11 даёт чёрный фон).</summary>
    public static bool SupportsWin10Blur => Generation == WindowsGeneration.Win10;

    /// <summary>Mica через недокументированный атрибут 1029: Win11 21H2.</summary>
    public static bool SupportsLegacyMica => IsWin11OrNewer && Build < 22523;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE (Mica, Mica Alt, Acrylic): Win11 22H2 (22523)+.</summary>
    public static bool SupportsSystemBackdrop => IsWin11OrNewer && Build >= 22523;

    /// <summary>Win7 Aero Glass (или Vista). Требует включённой композиции DWM.</summary>
    public static bool SupportsAeroGlass => Generation == WindowsGeneration.Win7;

    public static bool IsCompositionEnabled
    {
        get
        {
            if (IsWin8OrNewer) return true;
            try
            {
                return DwmApi.DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    public static string DisplayName
    {
        get
        {
            var name = Generation switch
            {
                WindowsGeneration.Win7 => "Windows 7",
                WindowsGeneration.Win8 => "Windows 8",
                WindowsGeneration.Win81 => "Windows 8.1",
                WindowsGeneration.Win10 => "Windows 10",
                WindowsGeneration.Win11 => "Windows 11",
                _ => "Windows",
            };
            var sp = string.IsNullOrEmpty(ServicePack) ? string.Empty : " " + ServicePack;
            return $"{name}{sp} (build {Build}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";
        }
    }
}
