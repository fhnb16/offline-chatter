using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Theming;

/// <summary>
/// Ключи ресурсов темы. В XAML они используются только через DynamicResource,
/// значения выставляет <see cref="ThemeManager"/>.
/// </summary>
public static class ThemeKeys
{
    public const string WindowBackground = "WindowBackgroundBrush";
    public const string SidebarBackground = "SidebarBackgroundBrush";
    public const string ContentBackground = "ContentBackgroundBrush";
    public const string ContentBorder = "ContentBorderBrush";
    public const string CardBackground = "CardBackgroundBrush";
    public const string CardBorder = "CardBorderBrush";
    public const string PopupBackground = "PopupBackgroundBrush";

    public const string TextPrimary = "TextPrimaryBrush";
    public const string TextSecondary = "TextSecondaryBrush";
    public const string TextTertiary = "TextTertiaryBrush";
    public const string TextOnAccent = "TextOnAccentBrush";

    public const string Accent = "AccentBrush";
    public const string AccentHover = "AccentHoverBrush";
    public const string AccentPressed = "AccentPressedBrush";
    public const string AccentText = "AccentTextBrush";
    public const string AccentColor = "AccentColor";
    public const string UserBubble = "UserBubbleBrush";
    public const string UserBubbleText = "UserBubbleTextBrush";

    public const string ControlFill = "ControlFillBrush";
    public const string ControlHover = "ControlHoverBrush";
    public const string ControlPressed = "ControlPressedBrush";
    public const string ControlDisabled = "ControlDisabledBrush";
    public const string ControlStroke = "ControlStrokeBrush";
    public const string InputFill = "InputFillBrush";
    public const string InputFocusedFill = "InputFocusedFillBrush";
    public const string SubtleHover = "SubtleHoverBrush";
    public const string SubtlePressed = "SubtlePressedBrush";
    public const string SelectedItem = "SelectedItemBrush";
    public const string Divider = "DividerBrush";
    public const string FocusStroke = "FocusStrokeBrush";
    public const string ScrollThumb = "ScrollThumbBrush";
    public const string Selection = "SelectionBrush";

    public const string StatusOk = "StatusOkBrush";
    public const string StatusWarn = "StatusWarnBrush";
    public const string StatusError = "StatusErrorBrush";

    public const string AppFontFamily = "AppFontFamily";
    public const string MonoFontFamily = "MonoFontFamily";
}

internal static class ThemeTokens
{
    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static SolidColorBrush B(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static SolidColorBrush B(uint argb) => B(C(argb));

    /// <summary>Базовый сплошной цвет окна, когда подложки нет.</summary>
    public static Color SolidWindowColor(bool dark, SkinKind skin) =>
        dark ? C(0xFF202020)
             : skin == SkinKind.Fluent ? C(0xFFF3F3F3) : C(0xFFF0F0F0);

    public static Dictionary<string, object> Build(ResolvedTheme t)
    {
        if (t.HighContrast) return BuildHighContrast();

        var d = t.IsDark;
        var bd = t.Backdrop;
        var native = t.Skin != SkinKind.Fluent;
        var r = new Dictionary<string, object>();

        // --- Поверхности ---
        r[ThemeKeys.WindowBackground] = t.HasBackdrop ? Brushes.Transparent : B(t.SolidWindowColor);

        r[ThemeKeys.SidebarBackground] = bd switch
        {
            // Стекло Win7: «изморозь», чтобы тёмный текст читался на любой раскраске DWM
            BackdropKind.AeroGlass => B(d ? 0x8C1E1E1Eu : 0x8CFFFFFFu),
            BackdropKind.None => B(d ? 0xFF1C1C1Cu : native ? 0xFFF5F7FAu : 0xFFEEEEEEu),
            _ => Brushes.Transparent,
        };

        r[ThemeKeys.ContentBackground] = bd switch
        {
            BackdropKind.None => B(d ? 0xFF272727u : native ? 0xFFFFFFFFu : 0xFFFAFAFAu),
            BackdropKind.AeroGlass => B(d ? 0xF2202020u : 0xF7FFFFFFu),
            BackdropKind.Acrylic or BackdropKind.Blur => B(d ? 0x99202020u : 0xA6FFFFFFu),
            _ => B(d ? 0x4C3A3A3Au : 0x80FFFFFFu), // Mica / Mica Alt: «слой» как в Win11
        };
        r[ThemeKeys.ContentBorder] = B(d ? 0x19000000u : 0x0F000000u);

        r[ThemeKeys.CardBackground] = t.HasBackdrop && bd != BackdropKind.AeroGlass
            ? B(d ? 0x0DFFFFFFu : 0xB3FFFFFFu)
            : B(d ? 0xFF2D2D2Du : 0xFFFFFFFFu);
        r[ThemeKeys.CardBorder] = B(d ? 0x19FFFFFFu : native ? 0xFFDADFE6u : 0x0F000000u);
        r[ThemeKeys.PopupBackground] = B(d ? 0xFF2C2C2Cu : 0xFFF9F9F9u);

        // --- Текст ---
        r[ThemeKeys.TextPrimary] = B(d ? 0xFFFFFFFFu : 0xE4000000u);
        r[ThemeKeys.TextSecondary] = B(d ? 0xC5FFFFFFu : 0x9E000000u);
        r[ThemeKeys.TextTertiary] = B(d ? 0x87FFFFFFu : 0x72000000u);

        // --- Акцент ---
        var fill = t.AccentFill;
        r[ThemeKeys.AccentColor] = fill;
        r[ThemeKeys.Accent] = B(fill);
        r[ThemeKeys.AccentHover] = B(AccentPalette.WithAlpha(fill, 0xE6));
        r[ThemeKeys.AccentPressed] = B(AccentPalette.WithAlpha(fill, 0xCC));
        r[ThemeKeys.AccentText] = B(t.AccentText);
        r[ThemeKeys.TextOnAccent] = B(AccentPalette.ContrastText(fill));
        r[ThemeKeys.UserBubble] = B(fill);
        r[ThemeKeys.UserBubbleText] = B(AccentPalette.ContrastText(fill));
        r[ThemeKeys.Selection] = B(AccentPalette.WithAlpha(t.Palette.Accent, 0x66));

        // --- Контролы (Fluent-скин и наши собственные элементы) ---
        r[ThemeKeys.ControlFill] = B(d ? 0x0FFFFFFFu : 0xB3FFFFFFu);
        r[ThemeKeys.ControlHover] = B(d ? 0x15FFFFFFu : 0x80F9F9F9u);
        r[ThemeKeys.ControlPressed] = B(d ? 0x08FFFFFFu : 0x4DF9F9F9u);
        r[ThemeKeys.ControlDisabled] = B(d ? 0x0BFFFFFFu : 0x4DF9F9F9u);
        r[ThemeKeys.ControlStroke] = B(d ? 0x18FFFFFFu : 0x29000000u);
        r[ThemeKeys.InputFill] = B(d ? 0x0BFFFFFFu : 0xB3FFFFFFu);
        r[ThemeKeys.InputFocusedFill] = B(d ? 0xFF1F1F1Fu : 0xFFFFFFFFu);
        r[ThemeKeys.SubtleHover] = B(d ? 0x0FFFFFFFu : 0x0A000000u);
        r[ThemeKeys.SubtlePressed] = B(d ? 0x0AFFFFFFu : 0x06000000u);
        r[ThemeKeys.SelectedItem] = B(d ? 0x14FFFFFFu : 0x0F000000u);
        r[ThemeKeys.Divider] = B(d ? 0x15FFFFFFu : 0x14000000u);
        r[ThemeKeys.FocusStroke] = B(d ? 0xFFFFFFFFu : 0xE4000000u);
        r[ThemeKeys.ScrollThumb] = B(d ? 0x8BFFFFFFu : 0x72000000u);

        // --- Статусы ---
        r[ThemeKeys.StatusOk] = B(d ? 0xFF6CCB5Fu : 0xFF0F7B0Fu);
        r[ThemeKeys.StatusWarn] = B(d ? 0xFFFCE100u : 0xFF9D5D00u);
        r[ThemeKeys.StatusError] = B(d ? 0xFFFF99A4u : 0xFFC42B1Cu);

        AddFonts(r);
        return r;
    }

    private static Dictionary<string, object> BuildHighContrast()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var highlight = SystemColors.HighlightBrush;
        var highlightText = SystemColors.HighlightTextBrush;
        var gray = SystemColors.GrayTextBrush;
        var control = SystemColors.ControlBrush;

        var r = new Dictionary<string, object>
        {
            [ThemeKeys.WindowBackground] = window,
            [ThemeKeys.SidebarBackground] = window,
            [ThemeKeys.ContentBackground] = window,
            [ThemeKeys.ContentBorder] = text,
            [ThemeKeys.CardBackground] = window,
            [ThemeKeys.CardBorder] = text,
            [ThemeKeys.PopupBackground] = window,
            [ThemeKeys.TextPrimary] = text,
            [ThemeKeys.TextSecondary] = text,
            [ThemeKeys.TextTertiary] = gray,
            [ThemeKeys.TextOnAccent] = highlightText,
            [ThemeKeys.AccentColor] = SystemColors.HighlightColor,
            [ThemeKeys.Accent] = highlight,
            [ThemeKeys.AccentHover] = highlight,
            [ThemeKeys.AccentPressed] = highlight,
            [ThemeKeys.AccentText] = SystemColors.HotTrackBrush,
            [ThemeKeys.UserBubble] = highlight,
            [ThemeKeys.UserBubbleText] = highlightText,
            [ThemeKeys.Selection] = highlight,
            [ThemeKeys.ControlFill] = control,
            [ThemeKeys.ControlHover] = highlight,
            [ThemeKeys.ControlPressed] = highlight,
            [ThemeKeys.ControlDisabled] = control,
            [ThemeKeys.ControlStroke] = text,
            [ThemeKeys.InputFill] = window,
            [ThemeKeys.InputFocusedFill] = window,
            [ThemeKeys.SubtleHover] = highlight,
            [ThemeKeys.SubtlePressed] = highlight,
            [ThemeKeys.SelectedItem] = highlight,
            [ThemeKeys.Divider] = text,
            [ThemeKeys.FocusStroke] = text,
            [ThemeKeys.ScrollThumb] = text,
            [ThemeKeys.StatusOk] = text,
            [ThemeKeys.StatusWarn] = text,
            [ThemeKeys.StatusError] = text,
        };
        AddFonts(r);
        return r;
    }

    private static void AddFonts(Dictionary<string, object> r)
    {
        r[ThemeKeys.AppFontFamily] = OsInfo.IsWin11OrNewer
            ? new FontFamily("Segoe UI Variable Text, Segoe UI")
            : new FontFamily("Segoe UI");
        r[ThemeKeys.MonoFontFamily] = new FontFamily("Cascadia Mono, Consolas, Courier New");
    }
}
