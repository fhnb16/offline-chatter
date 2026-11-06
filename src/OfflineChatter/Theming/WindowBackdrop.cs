using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Interop;

namespace OfflineChatter.Theming;

/// <summary>
/// Подложка окна через DWM без AllowsTransparency (layered-окна в WPF медленные).
/// Схема: прозрачный фон HwndSource + DwmExtendFrameIntoClientArea(-1) + нужный эффект.
/// </summary>
internal static class WindowBackdrop
{
    public static void Apply(IntPtr hwnd, ResolvedTheme theme)
    {
        if (hwnd == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget == null) return;

        try
        {
            SetDarkCaption(hwnd, theme.IsDark);
            Reset(hwnd);

            if (!theme.HasBackdrop)
            {
                source.CompositionTarget.BackgroundColor = theme.SolidWindowColor;
                var zero = MARGINS.All(0);
                DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref zero);
                return;
            }

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var all = MARGINS.All(-1);
            DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref all);

            switch (theme.Backdrop)
            {
                case BackdropKind.AeroGlass:
                    // Достаточно расширенной рамки: стекло Win7 рисует сам DWM.
                    break;

                case BackdropKind.Blur:
                    SetAccent(hwnd, AccentState.EnableBlurBehind, 0, flags: 0);
                    break;

                case BackdropKind.Acrylic when OsInfo.SupportsSystemBackdrop:
                    SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_TRANSIENTWINDOW);
                    break;

                case BackdropKind.Acrylic:
                    SetAccent(hwnd, AccentState.EnableAcrylicBlurBehind, AcrylicTint(theme.IsDark), flags: 2);
                    break;

                case BackdropKind.Mica when OsInfo.SupportsSystemBackdrop:
                    SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_MAINWINDOW);
                    break;

                case BackdropKind.Mica:
                    SetAttr(hwnd, DwmApi.DWMWA_MICA_EFFECT, 1);
                    break;

                case BackdropKind.MicaAlt:
                    SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_TABBEDWINDOW);
                    break;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            Log.Warn("Theme", "Failed to apply window backdrop " + theme.Backdrop, ex);
            source.CompositionTarget.BackgroundColor = theme.SolidWindowColor;
        }
    }

    /// <summary>
    /// Обход лагов Acrylic на Win10 1903+: на время перетаскивания или ресайза
    /// включаем простое размытие, после — снова Acrylic.
    /// </summary>
    public static void OnSizeMove(IntPtr hwnd, ResolvedTheme theme, bool entering)
    {
        if (theme.Backdrop != BackdropKind.Acrylic || OsInfo.SupportsSystemBackdrop || !OsInfo.SupportsWin10Acrylic) return;
        try
        {
            if (entering)
                SetAccent(hwnd, AccentState.EnableBlurBehind, 0, flags: 0);
            else
                SetAccent(hwnd, AccentState.EnableAcrylicBlurBehind, AcrylicTint(theme.IsDark), flags: 2);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static void Reset(IntPtr hwnd)
    {
        if (OsInfo.IsWin10OrNewer) SetAccent(hwnd, AccentState.Disabled, 0, 0);
        if (OsInfo.SupportsSystemBackdrop) SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_NONE);
        if (OsInfo.SupportsLegacyMica) SetAttr(hwnd, DwmApi.DWMWA_MICA_EFFECT, 0);
    }

    private static void SetDarkCaption(IntPtr hwnd, bool dark)
    {
        if (!OsInfo.SupportsImmersiveDarkMode) return;
        var attr = OsInfo.Build >= 18985 ? DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE : DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
        SetAttr(hwnd, attr, dark ? 1 : 0);
    }

    /// <summary>Подкраска Acrylic в формате AABBGGRR. Серые оттенки симметричны, порядок каналов не важен.</summary>
    private static uint AcrylicTint(bool dark) => dark ? 0xCC202020u : 0xCCF3F3F3u;

    private static void SetAttr(IntPtr hwnd, int attribute, int value)
    {
        var hr = DwmApi.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        Log.Debug("Theme", $"DwmSetWindowAttribute(0x{hwnd.ToInt64():X}, {attribute}={value}) hr=0x{hr:X8}");
    }

    private static void SetAccent(IntPtr hwnd, AccentState state, uint tint, int flags)
    {
        var policy = new AccentPolicy { AccentState = state, AccentFlags = flags, GradientColor = tint };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            User32.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
