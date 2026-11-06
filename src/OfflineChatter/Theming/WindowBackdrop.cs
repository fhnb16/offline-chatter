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
    // Последняя применённая подложка окна: сбрасываем эффект только при реальной смене,
    // иначе Win11 на мгновение показывает пустой фон (мигание при каждом изменении темы).
    private static readonly System.Collections.Generic.Dictionary<IntPtr, (BackdropKind backdrop, bool dark)> Applied = new();

    /// <param name="extendedChrome">
    /// Заголовок совмещён с содержимым (ShellChrome): рамку DWM нужно держать расширенной всегда,
    /// иначе пропадут системные кнопки заголовка.
    /// </param>
    /// <param name="force">Пересоздать эффект даже без изменений (первый показ окна: Win11 25H2 выставляет свою тему).</param>
    public static void Apply(IntPtr hwnd, ResolvedTheme theme, bool extendedChrome, bool force = false)
    {
        if (hwnd == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget == null) return;

        try
        {
            SetDarkCaption(hwnd, theme.IsDark);
            // Цвет Mica/Acrylic DWM берёт из тёмного режима окна только при включении эффекта:
            // при смене светлая/тёмная его тоже нужно пересоздать, иначе подложка остаётся прежней.
            var state = (theme.Backdrop, theme.IsDark);
            if (force || !Applied.TryGetValue(hwnd, out var previous) || previous != state) Reset(hwnd);
            Applied[hwnd] = state;

            if (!theme.HasBackdrop)
            {
                source.CompositionTarget.BackgroundColor = theme.SolidWindowColor;
                var m = MARGINS.All(extendedChrome ? -1 : 0);
                DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref m);
                RefreshFrame(hwnd);
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
            RefreshFrame(hwnd);
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

    /// <summary>Окно закрыто — забываем его состояние.</summary>
    public static void Forget(IntPtr hwnd) => Applied.Remove(hwnd);

    /// <summary>
    /// Без пересчёта рамки DWM применяет новый тип подложки и цвет заголовка только при следующей активации окна —
    /// отсюда «не применилось» и «сбросилось» при переключении темы.
    /// </summary>
    private static void RefreshFrame(IntPtr hwnd) =>
        User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            User32.SWP_FRAMECHANGED | User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

    /// <summary>Повторно выставить тёмный заголовок (Win11 25H2 иногда возвращает системный при активации).</summary>
    public static void ReapplyCaption(IntPtr hwnd, ResolvedTheme theme)
    {
        try { SetDarkCaption(hwnd, theme.IsDark); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
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
