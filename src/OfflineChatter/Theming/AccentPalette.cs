using System;
using System.Windows.Media;
using Microsoft.Win32;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Interop;

namespace OfflineChatter.Theming;

/// <summary>Акцентный цвет и его оттенки в той же схеме, что и в Windows 10/11 (Light1–3, Dark1–3).</summary>
public sealed class AccentPalette
{
    private AccentPalette(Color accent, Color l1, Color l2, Color l3, Color d1, Color d2, Color d3)
    {
        Accent = accent;
        Light1 = l1; Light2 = l2; Light3 = l3;
        Dark1 = d1; Dark2 = d2; Dark3 = d3;
    }

    public Color Accent { get; }
    public Color Light1 { get; }
    public Color Light2 { get; }
    public Color Light3 { get; }
    public Color Dark1 { get; }
    public Color Dark2 { get; }
    public Color Dark3 { get; }

    public static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    public static AccentPalette FromColor(Color accent)
    {
        accent.A = 255;
        return new AccentPalette(
            accent,
            Mix(accent, Colors.White, 0.25), Mix(accent, Colors.White, 0.50), Mix(accent, Colors.White, 0.70),
            Mix(accent, Colors.Black, 0.20), Mix(accent, Colors.Black, 0.40), Mix(accent, Colors.Black, 0.60));
    }

    public static AccentPalette Resolve(AppearanceSettings settings)
    {
        if (settings.Accent == AccentSource.Custom && ColorHex.TryParse(settings.CustomAccent, out var r, out var g, out var b))
            return FromColor(Color.FromRgb(r, g, b));

        return ReadSystem() ?? FromColor(DefaultAccent);
    }

    /// <summary>Системный акцент: палитра Explorer на Win10+, цвет DWM на Win7/8.</summary>
    public static AccentPalette? ReadSystem()
    {
        try
        {
            if (OsInfo.IsWin10OrNewer && TryReadWin10Palette() is { } p) return p;

            if (DwmApi.DwmGetColorizationColor(out var argb, out _) == 0)
            {
                var c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
                return FromColor(c);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Warn("Theme", "Cannot read system accent color", ex);
        }
        return null;
    }

    private static AccentPalette? TryReadWin10Palette()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
        if (key?.GetValue("AccentPalette") is byte[] { Length: >= 28 } bytes)
        {
            // 8 цветов RGBA: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, (резерв)
            Color At(int i) => Color.FromRgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
            return new AccentPalette(At(3), At(2), At(1), At(0), At(4), At(5), At(6));
        }

        using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (dwm?.GetValue("AccentColor") is int abgr)
        {
            var u = unchecked((uint)abgr);
            return FromColor(Color.FromRgb((byte)u, (byte)(u >> 8), (byte)(u >> 16)));
        }
        return null;
    }

    public static Color Mix(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    /// <summary>Относительная яркость по WCAG.</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>Чёрный или белый — что читается лучше поверх цвета.</summary>
    public static Color ContrastText(Color background) =>
        Luminance(background) > 0.40 ? Color.FromRgb(0x10, 0x10, 0x10) : Colors.White;

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
