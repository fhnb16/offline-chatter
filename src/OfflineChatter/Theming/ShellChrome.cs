using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using OfflineChatter.Interop;

namespace OfflineChatter.Theming;

/// <summary>
/// Заголовок окна, совмещённый с содержимым.
/// Системные кнопки «свернуть / развернуть / закрыть» рисует сам DWM (UseAeroCaptionButtons):
/// на Win7 это настоящие стеклянные кнопки, на Win11 работают Snap Layouts (DWM отвечает HTMAXBUTTON).
/// Без композиции DWM (Aero Basic, Classic) окно возвращается к стандартной рамке.
/// </summary>
public static class ShellChrome
{
    /// <summary>Окно хочет совмещать заголовок с содержимым (задаётся в XAML окна).</summary>
    public static readonly DependencyProperty ExtendIntoTitleBarProperty = DependencyProperty.RegisterAttached(
        "ExtendIntoTitleBar", typeof(bool), typeof(ShellChrome), new PropertyMetadata(false));

    public static bool GetExtendIntoTitleBar(DependencyObject o) => (bool)o.GetValue(ExtendIntoTitleBarProperty);
    public static void SetExtendIntoTitleBar(DependencyObject o, bool v) => o.SetValue(ExtendIntoTitleBarProperty, v);

    private static readonly DependencyPropertyKey IsExtendedKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsExtended", typeof(bool), typeof(ShellChrome), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Заголовок сейчас действительно совмещён (наследуется вниз по дереву — для привязок в разметке).</summary>
    public static readonly DependencyProperty IsExtendedProperty = IsExtendedKey.DependencyProperty;

    public static bool GetIsExtended(DependencyObject o) => (bool)o.GetValue(IsExtendedProperty);

    private static readonly DependencyPropertyKey CaptionButtonsWidthKey = DependencyProperty.RegisterAttachedReadOnly(
        "CaptionButtonsWidth", typeof(double), typeof(ShellChrome), new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Ширина системных кнопок в DIP: правее этого места в полосе заголовка ничего не размещать.</summary>
    public static readonly DependencyProperty CaptionButtonsWidthProperty = CaptionButtonsWidthKey.DependencyProperty;

    public static double GetCaptionButtonsWidth(DependencyObject o) => (double)o.GetValue(CaptionButtonsWidthProperty);

    private static readonly DependencyPropertyKey TitleBarHeightKey = DependencyProperty.RegisterAttachedReadOnly(
        "TitleBarHeight", typeof(double), typeof(ShellChrome), new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty TitleBarHeightProperty = TitleBarHeightKey.DependencyProperty;

    public static double GetTitleBarHeight(DependencyObject o) => (double)o.GetValue(TitleBarHeightProperty);

    public static bool CanExtend => OsInfo.IsCompositionEnabled && Environment.OSVersion.Version.Major >= 6;

    /// <param name="captionGlassOnly">
    /// Расширять рамку DWM только на полосу заголовка (под системные кнопки), а не на всё окно.
    /// Нужно для размытия и Acrylic через SetWindowCompositionAttribute на Win10: область расширенной рамки
    /// DWM заливает сплошным цветом рамки (в тёмной теме — почти чёрным), и эффект под ней не виден.
    /// </param>
    /// <param name="allowExtend">Скин допускает совмещённый заголовок (Fluent). Системные скины остаются со стандартной рамкой.</param>
    internal static bool Update(Window window, bool captionGlassOnly = false, bool allowExtend = true)
    {
        var extend = allowExtend && GetExtendIntoTitleBar(window) && CanExtend && !SystemParameters.HighContrast;
        var current = GetIsExtended(window);
        SetCaptionGlassOnly(window, captionGlassOnly);

        if (extend && !current)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 32,
                GlassFrameThickness = GlassThickness(window),
                ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
                UseAeroCaptionButtons = true,
                NonClientFrameEdges = NonClientFrameEdges.None,
                CornerRadius = new CornerRadius(0),
            });
            window.SetValue(IsExtendedKey, true);
            window.StateChanged -= OnStateChanged;
            window.StateChanged += OnStateChanged;
            window.SizeChanged -= OnSizeChanged;
            window.SizeChanged += OnSizeChanged;
        }
        else if (!extend && current)
        {
            window.ClearValue(WindowChrome.WindowChromeProperty);
            window.SetValue(IsExtendedKey, false);
            window.StateChanged -= OnStateChanged;
            window.SizeChanged -= OnSizeChanged;
            if (window.Content is FrameworkElement root) root.Margin = new Thickness(0);
        }

        if (extend) Refresh(window);
        return extend;
    }

    private static readonly DependencyProperty CaptionGlassOnlyProperty = DependencyProperty.RegisterAttached(
        "CaptionGlassOnly", typeof(bool), typeof(ShellChrome), new PropertyMetadata(false));

    private static void SetCaptionGlassOnly(Window window, bool value)
    {
        if ((bool)window.GetValue(CaptionGlassOnlyProperty) == value) return;
        window.SetValue(CaptionGlassOnlyProperty, value);
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = GlassThickness(window);
    }

    private static Thickness GlassThickness(Window window) =>
        (bool)window.GetValue(CaptionGlassOnlyProperty) ? new Thickness(0, GetTitleBarHeight(window), 0, 0) : new Thickness(-1);

    /// <summary>Высота полосы заголовка в пикселях устройства — сколько рамки DWM нужно под системные кнопки.</summary>
    internal static int TitleBarHeightPixels(Window window) =>
        (int)Math.Ceiling(GetTitleBarHeight(window) * VisualTreeHelper.GetDpi(window).DpiScaleY);

    private static void OnStateChanged(object? sender, EventArgs e) => Refresh((Window)sender!);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh((Window)sender);

    /// <summary>Отступ при развороте и метрики кнопок заголовка.</summary>
    private static void Refresh(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);

        // Развёрнутое окно с WindowChrome вылезает за экран на толщину рамки — компенсируем.
        if (window.Content is FrameworkElement root)
        {
            if (window.WindowState == WindowState.Maximized)
            {
                var fx = (User32.GetSystemMetrics(User32.SM_CXSIZEFRAME) + User32.GetSystemMetrics(User32.SM_CXPADDEDBORDER)) / dpi.DpiScaleX;
                var fy = (User32.GetSystemMetrics(User32.SM_CYSIZEFRAME) + User32.GetSystemMetrics(User32.SM_CXPADDEDBORDER)) / dpi.DpiScaleY;
                root.Margin = new Thickness(fx, fy, fx, fy);
            }
            else
            {
                root.Margin = new Thickness(0);
            }
        }

        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (DwmApi.DwmGetWindowAttribute(hwnd, DwmApi.DWMWA_CAPTION_BUTTON_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            {
                window.SetValue(CaptionButtonsWidthKey, r.Width / dpi.DpiScaleX + 8);
                var h = Math.Max(32, Math.Ceiling(r.Bottom / dpi.DpiScaleY));
                window.SetValue(TitleBarHeightKey, h);
                if (WindowChrome.GetWindowChrome(window) is { } chrome && Math.Abs(chrome.CaptionHeight - h) > 0.5)
                {
                    chrome.CaptionHeight = h;
                    if ((bool)window.GetValue(CaptionGlassOnlyProperty)) chrome.GlassFrameThickness = GlassThickness(window);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
