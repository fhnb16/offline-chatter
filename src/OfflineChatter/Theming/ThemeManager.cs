using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;
using OfflineChatter.Interop;

namespace OfflineChatter.Theming;

/// <summary>
/// Единая точка управления оформлением: скин (словарь стилей контролов), подложка окон,
/// акцентный цвет, светлая/тёмная тема, High Contrast. Следит за системными изменениями.
/// </summary>
public static class ThemeManager
{
    private const string AssemblySuffix = ";V4.0.0.0;31bf3856ad364e35;component/themes/";
    private static readonly Uri FluentSkinUri = new("pack://application:,,,/OfflineChatter;component/Skins/Fluent.xaml");

    private static readonly List<WeakReference<Window>> AttachedWindows = new();
    private static readonly List<ResourceDictionary> SkinDictionaries = new();
    private static AppearanceSettings _settings = new();
    private static DispatcherTimer? _debounce;

    public static ResolvedTheme? Current { get; private set; }

    /// <summary>Тема применена заново (из-за настроек или системного события).</summary>
    public static event EventHandler? Changed;

    public static void Initialize(AppearanceSettings settings)
    {
        _settings = settings;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Apply();
    }

    public static void Apply(AppearanceSettings settings)
    {
        _settings = settings;
        Apply();
    }

    /// <summary>Подключить окно: подложка, тёмный заголовок, реакция на системные сообщения.</summary>
    public static void Attach(Window window)
    {
        AttachedWindows.Add(new WeakReference<Window>(window));
        window.Closed += (_, _) =>
        {
            AttachedWindows.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, window));
            WindowBackdrop.Forget(new WindowInteropHelper(window).Handle);
        };

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            OnSourceInitialized(window);
        else
            window.SourceInitialized += (_, _) => OnSourceInitialized(window);

        // Win11 25H2+ при первом показе окна сам выставляет тёмный заголовок по системной теме
        // и затирает значение, заданное в SourceInitialized. Поэтому после первого рендера применяем ещё раз.
        void OnRendered(object? s, EventArgs e)
        {
            window.ContentRendered -= OnRendered;
            ApplyToWindow(window, force: true);
            ScheduleVerify();
        }
        window.ContentRendered += OnRendered;
    }

    private static void OnSourceInitialized(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        ApplyToWindow(window);
    }

    private static DispatcherTimer? _verifyTimer;
    private static int _verifyRounds;

    /// <summary>
    /// Через 150, 500 и 1200 мс после применения темы сверяем атрибуты DWM у всех окон: система переписывает их
    /// асинхронно (после смены шаблона окна, при первом показе). Сверка дешёвая и ничего не мигает, если всё совпало.
    /// </summary>
    private static void ScheduleVerify()
    {
        _verifyRounds = 0;
        if (_verifyTimer == null)
        {
            _verifyTimer = new DispatcherTimer(DispatcherPriority.Background);
            _verifyTimer.Tick += (_, _) =>
            {
                VerifyAll();
                _verifyRounds++;
                if (_verifyRounds >= 3) _verifyTimer!.Stop();
                else _verifyTimer!.Interval = TimeSpan.FromMilliseconds(_verifyRounds == 1 ? 350 : 700);
            };
        }
        _verifyTimer.Stop();
        _verifyTimer.Interval = TimeSpan.FromMilliseconds(150);
        _verifyTimer.Start();
    }

    private static void VerifyAll()
    {
        if (Current == null) return;
        foreach (var w in AttachedWindows.ToList())
        {
            if (!w.TryGetTarget(out var window)) continue;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) WindowBackdrop.Verify(hwnd, Current, ShellChrome.GetIsExtended(window));
        }
    }

    private static void ApplyToWindow(Window window, bool force = false)
    {
        if (Current == null) return;
        var extended = ShellChrome.Update(window);
        WindowBackdrop.Apply(new WindowInteropHelper(window).Handle, Current, extended, force);
    }

    private static void Apply()
    {
        var resolved = Resolve(_settings);
        var app = Application.Current;
        if (app == null) return;

        var skinChanged = Current == null || Current.Skin != resolved.Skin || Current.HighContrast != resolved.HighContrast;
        Current = resolved;

        if (skinChanged) ApplySkin(app, resolved);

        foreach (var kv in ThemeTokens.Build(resolved))
            app.Resources[kv.Key] = kv.Value;

        foreach (var w in AttachedWindows.ToList())
        {
            if (w.TryGetTarget(out var window) && new WindowInteropHelper(window).Handle != IntPtr.Zero)
                ApplyToWindow(window);
        }
        ScheduleVerify();

        Log.Info("Theme", "Theme applied", new
        {
            os = OsInfo.DisplayName,
            skin = resolved.Skin.ToString(),
            dark = resolved.IsDark,
            backdrop = resolved.Backdrop.ToString(),
            highContrast = resolved.HighContrast,
            requested = new { _settings.Skin, _settings.Mode, _settings.Backdrop, _settings.Accent },
        });
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ---------------- Разрешение настроек ----------------

    public static ResolvedTheme Resolve(AppearanceSettings s)
    {
        var highContrast = SystemParameters.HighContrast;
        var dark = !highContrast && s.Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark(),
        };

        var skin = ResolveSkin(s.Skin);
        // Системные скины не поддерживают тёмную тему — контролы рисует Fluent-скин.
        if (dark && skin != SkinKind.Fluent) skin = SkinKind.Fluent;

        var backdrop = highContrast ? BackdropKind.None : ResolveBackdrop(s.Backdrop);
        var palette = AccentPalette.Resolve(s);
        return new ResolvedTheme(skin, dark, backdrop, palette, highContrast, ThemeTokens.SolidWindowColor(dark, skin));
    }

    public static SkinKind ResolveSkin(SkinKind requested)
    {
        if (requested != SkinKind.Auto) return requested;
        if (!IsVisualStylesActive()) return SkinKind.Classic;
        return OsInfo.Generation switch
        {
            WindowsGeneration.Win7 => SkinKind.Aero,
            WindowsGeneration.Win8 or WindowsGeneration.Win81 => SkinKind.Aero2,
            WindowsGeneration.Win10 or WindowsGeneration.Win11 => SkinKind.Fluent,
            _ => SkinKind.Aero,
        };
    }

    public static BackdropKind ResolveBackdrop(BackdropKind requested)
    {
        if (requested == BackdropKind.Auto)
        {
            requested = OsInfo.Generation switch
            {
                WindowsGeneration.Win7 => BackdropKind.AeroGlass,
                WindowsGeneration.Win10 => BackdropKind.Acrylic,
                WindowsGeneration.Win11 => BackdropKind.Mica,
                _ => BackdropKind.None,
            };
        }

        return requested switch
        {
            BackdropKind.AeroGlass when OsInfo.SupportsAeroGlass && OsInfo.IsCompositionEnabled => BackdropKind.AeroGlass,
            BackdropKind.Blur when OsInfo.SupportsWin10Blur => BackdropKind.Blur,
            BackdropKind.Acrylic when OsInfo.SupportsSystemBackdrop || OsInfo.SupportsWin10Acrylic => BackdropKind.Acrylic,
            BackdropKind.Mica when OsInfo.SupportsSystemBackdrop || OsInfo.SupportsLegacyMica => BackdropKind.Mica,
            BackdropKind.MicaAlt when OsInfo.SupportsSystemBackdrop => BackdropKind.MicaAlt,
            BackdropKind.MicaAlt when OsInfo.SupportsLegacyMica => BackdropKind.Mica,
            _ => BackdropKind.None,
        };
    }

    /// <summary>Какие подложки вообще имеют смысл на этой ОС (для списка в настройках).</summary>
    public static IReadOnlyList<BackdropKind> SupportedBackdrops()
    {
        var list = new List<BackdropKind> { BackdropKind.Auto, BackdropKind.None };
        if (OsInfo.SupportsAeroGlass) list.Add(BackdropKind.AeroGlass);
        if (OsInfo.SupportsWin10Blur) list.Add(BackdropKind.Blur);
        if (OsInfo.SupportsWin10Acrylic || OsInfo.SupportsSystemBackdrop) list.Add(BackdropKind.Acrylic);
        if (OsInfo.SupportsLegacyMica || OsInfo.SupportsSystemBackdrop) list.Add(BackdropKind.Mica);
        if (OsInfo.SupportsSystemBackdrop) list.Add(BackdropKind.MicaAlt);
        return list;
    }

    public static bool IsSystemDark()
    {
        if (!OsInfo.IsWin10OrNewer) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsVisualStylesActive()
    {
        try { return UxTheme.IsThemeActive(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // ---------------- Скины ----------------

    private static void ApplySkin(Application app, ResolvedTheme theme)
    {
        var merged = app.Resources.MergedDictionaries;
        foreach (var d in SkinDictionaries) merged.Remove(d);
        SkinDictionaries.Clear();

        // В High Contrast оставляем системную тему WPF по умолчанию — она сама берёт системные цвета.
        if (theme.HighContrast)
        {
            var hc = new ResourceDictionary();
            AddNativeKeyedStyles(hc, null);
            SkinDictionaries.Add(hc);
        }
        else if (theme.Skin == SkinKind.Fluent)
        {
            SkinDictionaries.Add(new ResourceDictionary { Source = FluentSkinUri });
        }
        else
        {
            var native = LoadNativeDictionary(theme.Skin);
            if (native != null) SkinDictionaries.Add(native);
            var overlay = new ResourceDictionary();
            AddNativeKeyedStyles(overlay, native);
            SkinDictionaries.Add(overlay);
        }

        // Скины вставляются первыми: Shared.xaml и прочие словари приложения идут после и имеют приоритет.
        for (var i = 0; i < SkinDictionaries.Count; i++)
            merged.Insert(i, SkinDictionaries[i]);
    }

    private static ResourceDictionary? LoadNativeDictionary(SkinKind skin)
    {
        var (assembly, file) = skin switch
        {
            SkinKind.Aero => ("PresentationFramework.Aero", "Aero.NormalColor.xaml"),
            SkinKind.Aero2 => ("PresentationFramework.Aero2", "Aero2.NormalColor.xaml"),
            SkinKind.AeroLite => ("PresentationFramework.AeroLite", "AeroLite.NormalColor.xaml"),
            SkinKind.Luna => ("PresentationFramework.Luna", "Luna.NormalColor.xaml"),
            SkinKind.Royale => ("PresentationFramework.Royale", "Royale.NormalColor.xaml"),
            SkinKind.Classic => ("PresentationFramework.Classic", "Classic.xaml"),
            _ => (null, null),
        };
        if (assembly == null) return null;

        try
        {
            return new ResourceDictionary { Source = new Uri($"pack://application:,,,/{assembly}{AssemblySuffix}{file}", UriKind.Absolute) };
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Windows.Markup.XamlParseException or InvalidOperationException)
        {
            Log.Error("Theme", "Cannot load native skin " + skin, ex);
            return null;
        }
    }

    /// <summary>
    /// Именованные стили, которые используют окна (AccentButtonStyle, SubtleButtonStyle),
    /// в нативных скинах наследуют системный вид. Fluent.xaml определяет их сам.
    /// </summary>
    private static void AddNativeKeyedStyles(ResourceDictionary target, ResourceDictionary? native)
    {
        // Системные скины рисуют контролы ниже (23 px против 32 px во Fluent): выравниваем минимальную высоту,
        // чтобы при смене скина вёрстка не прыгала и поля ввода не обрезались.
        foreach (var type in new[] { typeof(Button), typeof(System.Windows.Controls.Primitives.ToggleButton), typeof(ComboBox), typeof(TextBox), typeof(PasswordBox) })
        {
            var style = new Style(type, native?[type] as Style);
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            if (type == typeof(Button) || type == typeof(System.Windows.Controls.Primitives.ToggleButton))
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 2, 12, 2)));
            else if (type != typeof(ComboBox))
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            target[type] = style;
        }

        // Строки списков (боковая панель, результаты поиска) — той же высоты, что во Fluent
        var item = new Style(typeof(ListBoxItem), native?[typeof(ListBoxItem)] as Style);
        item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        item.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        item.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        target[typeof(ListBoxItem)] = item;

        var buttonBase = target[typeof(Button)] as Style;
        target["AccentButtonStyle"] = new Style(typeof(Button), buttonBase);

        var toolbarButton = native != null && native.Contains(ToolBar.ButtonStyleKey) ? native[ToolBar.ButtonStyleKey] as Style : null;
        target["SubtleButtonStyle"] = toolbarButton != null
            ? new Style(typeof(Button), toolbarButton)
            : new Style(typeof(Button), buttonBase);
    }

    // ---------------- Системные события ----------------

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case User32.WM_DWMCOMPOSITIONCHANGED:
            case User32.WM_DWMCOLORIZATIONCOLORCHANGED:
            case User32.WM_THEMECHANGED:
                ScheduleReapply();
                break;

            case User32.WM_SETTINGCHANGE:
                var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                if (area is null or "ImmersiveColorSet" or "WindowsThemeElement")
                    ScheduleReapply();
                break;

            case User32.WM_ACTIVATE:
                if (Current != null && wParam != IntPtr.Zero)
                {
                    var win = HwndSource.FromHwnd(hwnd)?.RootVisual as Window;
                    WindowBackdrop.Verify(hwnd, Current, win != null && ShellChrome.GetIsExtended(win));
                }
                break;

            case User32.WM_ENTERSIZEMOVE:
                if (Current != null) WindowBackdrop.OnSizeMove(hwnd, Current, entering: true);
                break;

            case User32.WM_EXITSIZEMOVE:
                if (Current != null) WindowBackdrop.OnSizeMove(hwnd, Current, entering: false);
                break;
        }
        return IntPtr.Zero;
    }

    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) ScheduleReapply();
    }

    /// <summary>Системные сообщения приходят пачками — применяем тему один раз после паузы.</summary>
    private static void ScheduleReapply()
    {
        if (_debounce == null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _debounce.Tick += (_, _) =>
            {
                _debounce!.Stop();
                Apply();
            };
        }
        _debounce.Stop();
        _debounce.Start();
    }
}
