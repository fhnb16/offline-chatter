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
            var margins = MARGINS.All(-1);
            if (UsesAccentPolicy(theme))
            {
                // Win10, SetWindowCompositionAttribute: расширенную на всё окно рамку DWM рисует сплошным цветом рамки
                // поверх эффекта («чёрное пятно» в тёмной теме). Рамка — только под системные кнопки заголовка;
                // остальная часть окна прозрачна для эффекта и так (1 px нужен, чтобы DWM учитывал альфа-канал окна).
                var caption = extendedChrome && source.RootVisual is System.Windows.Window w ? ShellChrome.TitleBarHeightPixels(w) : 1;
                margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = Math.Max(1, caption), cyBottomHeight = 0 };
            }
            DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            LastMargins = margins.cyTopHeight == -1 ? "all" : "top " + margins.cyTopHeight;

            switch (theme.Backdrop)
            {
                case BackdropKind.AeroGlass:
                    // Достаточно расширенной рамки: стекло Win7 рисует сам DWM.
                    break;

                case BackdropKind.Blur:
                    SetAccent(hwnd, AccentState.EnableBlurBehind, 0, flags: AccentBorderFlags);
                    break;

                case BackdropKind.Acrylic when OsInfo.SupportsSystemBackdrop:
                    SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_TRANSIENTWINDOW);
                    break;

                case BackdropKind.Acrylic:
                    SetAccent(hwnd, AccentState.EnableAcrylicBlurBehind, AcrylicTint(theme.IsDark), flags: AccentBorderFlags);
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

    /// <summary>Подложка рисуется через SetWindowCompositionAttribute (размытие и Acrylic на Win10).</summary>
    public static bool UsesAccentPolicy(ResolvedTheme theme) =>
        OsInfo.SupportsWin10Blur && (theme.Backdrop == BackdropKind.Blur || theme.Backdrop == BackdropKind.Acrylic && !OsInfo.SupportsSystemBackdrop);

    /// <summary>Последнее расширение рамки DWM (для «Сведений о подложке»).</summary>
    public static string? LastMargins { get; private set; }

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
                SetAccent(hwnd, AccentState.EnableBlurBehind, 0, flags: AccentBorderFlags);
            else
                SetAccent(hwnd, AccentState.EnableAcrylicBlurBehind, AcrylicTint(theme.IsDark), flags: AccentBorderFlags);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static void Reset(IntPtr hwnd)
    {
        // SetWindowCompositionAttribute — только на Win10: на Win11 даже «выключение» через него мешает системной подложке
        if (OsInfo.SupportsWin10Blur) SetAccent(hwnd, AccentState.Disabled, 0, 0);
        if (OsInfo.SupportsSystemBackdrop) SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, DwmApi.DWMSBT_NONE);
        if (OsInfo.SupportsLegacyMica) SetAttr(hwnd, DwmApi.DWMWA_MICA_EFFECT, 0);
    }

    /// <summary>
    /// Сверка фактических атрибутов DWM с темой: их может переписать система или сторонняя утилита
    /// (Mica For Everyone ставит всем окнам свой заголовок и подложку при показе, смене шаблона, событиях окна).
    /// Возвращает true, если пришлось применить заново.
    /// </summary>
    public static bool Verify(IntPtr hwnd, ResolvedTheme theme, bool extendedChrome)
    {
        if (hwnd == IntPtr.Zero || !OsInfo.SupportsImmersiveDarkMode) return false;
        try
        {
            var darkAttr = OsInfo.Build >= 18985 ? DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE : DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
            var ok = DwmApi.DwmGetWindowAttributeInt(hwnd, darkAttr, out var dark, sizeof(int)) != 0 || (dark != 0) == theme.IsDark;
            if (OsInfo.SupportsSystemBackdrop && DwmApi.DwmGetWindowAttributeInt(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, out var type, sizeof(int)) == 0)
                ok &= type == ExpectedSystemBackdrop(theme);
            if (ok) return false;
            Log.Info("Theme", "DWM attributes were overridden by the system, re-applying", new { dark, theme.IsDark, theme.Backdrop });
            Apply(hwnd, theme, extendedChrome, force: true);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Быстрое исправление без сброса подложки (без мигания): вернуть тёмный режим и тип подложки, если их переписали
    /// извне (утилиты вроде Mica For Everyone делают это сразу после запроса заголовка окна — WM_GETTEXT).
    /// </summary>
    public static void QuickFix(IntPtr hwnd, ResolvedTheme theme)
    {
        if (!OsInfo.SupportsImmersiveDarkMode) return;
        try
        {
            var darkAttr = OsInfo.Build >= 18985 ? DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE : DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
            if (DwmApi.DwmGetWindowAttributeInt(hwnd, darkAttr, out var dark, sizeof(int)) == 0 && (dark != 0) != theme.IsDark)
                SetAttr(hwnd, darkAttr, theme.IsDark ? 1 : 0);
            if (OsInfo.SupportsSystemBackdrop && DwmApi.DwmGetWindowAttributeInt(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, out var type, sizeof(int)) == 0)
            {
                var expected = ExpectedSystemBackdrop(theme);
                if (type != expected) SetAttr(hwnd, DwmApi.DWMWA_SYSTEMBACKDROP_TYPE, expected);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Какое значение DWMWA_SYSTEMBACKDROP_TYPE должно стоять для темы (Win11 22H2+).</summary>
    private static int ExpectedSystemBackdrop(ResolvedTheme theme) => theme.Backdrop switch
    {
        BackdropKind.Mica => DwmApi.DWMSBT_MAINWINDOW,
        BackdropKind.MicaAlt => DwmApi.DWMSBT_TABBEDWINDOW,
        BackdropKind.Acrylic => DwmApi.DWMSBT_TRANSIENTWINDOW,
        _ => DwmApi.DWMSBT_NONE,
    };

    /// <summary>
    /// Сообщить Windows тему приложения (светлая или тёмная), чтобы система не перекрашивала окна по теме ОС.
    /// Win10 1903+; на старых системах ничего не делает.
    /// </summary>
    public static void SetAppMode(bool dark)
    {
        if (!OsInfo.IsWin10OrNewer || OsInfo.Build < 18362) return;
        try
        {
            UxThemeDark.SetPreferredAppMode(dark ? UxThemeDark.AppModeForceDark : UxThemeDark.AppModeForceLight);
            UxThemeDark.FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Debug("Theme", "SetPreferredAppMode is not available: " + ex.Message);
        }
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
            var ok = User32.SetWindowCompositionAttribute(hwnd, ref data);
            LastAccent = $"{state} flags=0x{flags:X} tint=0x{tint:X8} -> {(ok != 0 ? "ok" : "error " + Marshal.GetLastWin32Error())}";
            if (state != AccentState.Disabled) Log.Info("Theme", "SetWindowCompositionAttribute", new { result = LastAccent });
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Последний вызов SetWindowCompositionAttribute и его результат (для «Сведений о подложке»).</summary>
    public static string? LastAccent { get; private set; }

    /// <summary>
    /// Флаги как у рабочих WPF-приложений на Win10 (EarTrumpet): 0x20|0x40|0x80|0x100 — рамка по всем краям.
    /// С флагом 2 на части сборок акрил рисовался без размытия.
    /// </summary>
    private const int AccentBorderFlags = 0x1E0;
}
