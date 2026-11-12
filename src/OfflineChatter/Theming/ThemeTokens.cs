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

    public const string GlassTextEffect = "GlassTextEffect";

    public const string CodeBackground = "CodeBackgroundBrush";
    public const string CodeKeyword = "CodeKeywordBrush";
    public const string CodeString = "CodeStringBrush";
    public const string CodeComment = "CodeCommentBrush";
    public const string CodeNumber = "CodeNumberBrush";
    public const string QuoteBar = "QuoteBarBrush";
    public const string AppFontFamily = "AppFontFamily";
    public const string MonoFontFamily = "MonoFontFamily";
    public const string CardCornerRadius = "CardCornerRadius";

    // --- Форма и строение оболочки: у каждого скина свои (Fluent — скруглённый «слой», Aero — панели Проводника) ---
    /// <summary>Скругление мелких элементов: кнопки-иконки, плашки вложений, карточки вызовов инструментов.</summary>
    public const string ControlCornerRadius = "ControlCornerRadius";
    public const string PillCornerRadius = "PillCornerRadius";
    public const string ContentCornerRadius = "ContentCornerRadius";
    public const string ContentBorderThickness = "ContentBorderThickness";
    public const string SidebarBorder = "SidebarBorderBrush";
    public const string SidebarBorderThickness = "SidebarBorderThickness";

    /// <summary>Полоса команд над лентой чата (в Aero — как панель команд Проводника; во Fluent её нет).</summary>
    public const string ToolbarBackground = "ToolbarBackgroundBrush";
    public const string ToolbarBorder = "ToolbarBorderBrush";
    public const string ToolbarBorderThickness = "ToolbarBorderThickness";

    // --- Строка списка в системных скинах (боковая панель, списки): наведение и выбор как в Проводнике ---
    public const string ItemHover = "ItemHoverBrush";
    public const string ItemHoverBorder = "ItemHoverBorderBrush";
    public const string ItemSelected = "ItemSelectedBrush";
    public const string ItemSelectedBorder = "ItemSelectedBorderBrush";
    public const string ItemSelectedText = "ItemSelectedTextBrush";
    public const string ItemCornerRadius = "ItemCornerRadius";
    public const string SubtleHoverBorder = "SubtleHoverBorderBrush";

    // --- Типографика ---
    /// <summary>Цвет заголовков: во Fluent — основной текст, в Aero — синий «main instruction».</summary>
    public const string Heading = "HeadingBrush";
    public const string SectionHeader = "SectionHeaderBrush";
    public const string HeadingFontWeight = "HeadingFontWeight";
    public const string PageTitleFontSize = "PageTitleFontSize";
    public const string TitleFontSize = "TitleFontSize";
    public const string AppFontSize = "AppFontSize";

    /// <summary>Курсор над кнопками-иконками: во Fluent — «рука», в системных скинах — обычная стрелка, как у кнопок Windows.</summary>
    public const string ActionCursor = "ActionCursor";
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

    public static Dictionary<string, object?> Build(ResolvedTheme t)
    {
        if (t.HighContrast) return BuildHighContrast();

        var d = t.IsDark;
        var bd = t.Backdrop;
        var native = t.Skin != SkinKind.Fluent;
        var r = new Dictionary<string, object?>();

        // --- Поверхности ---
        r[ThemeKeys.WindowBackground] = t.HasBackdrop ? Brushes.Transparent : B(t.SolidWindowColor);

        r[ThemeKeys.SidebarBackground] = bd switch
        {
            // Стекло Win7: «изморозь», чтобы тёмный текст читался на любой раскраске DWM
            BackdropKind.AeroGlass => B(d ? 0x8C1E1E1Eu : 0x8CFFFFFFu),
            BackdropKind.None => B(d ? 0xFF1C1C1Cu : native ? 0xFFF5F7FAu : 0xFFEEEEEEu),
            // Светлая Mica/Acrylic на тёмных обоях бывает серой — лёгкая светлая вуаль сохраняет контраст текста
            _ => d ? Brushes.Transparent : B(0x59F7F7F7u),
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
        // Пузырь пользователя — нейтральная поверхность с лёгким оттенком акцента: акцент остаётся
        // для действий и выделения, а лента не превращается в цветные плашки.
        var bubble = AccentPalette.Mix(d ? C(0xFF2E2E2E) : C(0xFFF4F5F7), t.Palette.Accent, d ? 0.16 : 0.10);
        r[ThemeKeys.UserBubble] = B(bubble);
        r[ThemeKeys.UserBubbleText] = B(d ? 0xFFFFFFFFu : 0xE4000000u);
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

        // --- Код и цитаты ---
        r[ThemeKeys.CodeBackground] = B(d ? 0x33000000u : 0x0A000000u);
        r[ThemeKeys.CodeKeyword] = B(d ? 0xFF569CD6u : 0xFF0000FFu);
        r[ThemeKeys.CodeString] = B(d ? 0xFFCE9178u : 0xFFA31515u);
        r[ThemeKeys.CodeComment] = B(d ? 0xFF6A9955u : 0xFF008000u);
        r[ThemeKeys.CodeNumber] = B(d ? 0xFFB5CEA8u : 0xFF098658u);
        r[ThemeKeys.QuoteBar] = B(AccentPalette.WithAlpha(fill, 0x99));

        // --- Статусы ---
        r[ThemeKeys.StatusOk] = B(d ? 0xFF6CCB5Fu : 0xFF0F7B0Fu);
        r[ThemeKeys.StatusWarn] = B(d ? 0xFFFCE100u : 0xFF9D5D00u);
        r[ThemeKeys.StatusError] = B(d ? 0xFFFF99A4u : 0xFFC42B1Cu);

        // Свечение под текстом на стекле Win7 (как у DrawThemeTextEx с DTT_GLOWSIZE)
        if (bd == BackdropKind.AeroGlass)
        {
            var glow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = d ? Colors.Black : Colors.White,
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.95,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
            };
            glow.Freeze();
            r[ThemeKeys.GlassTextEffect] = glow;
        }
        else
        {
            r[ThemeKeys.GlassTextEffect] = null;
        }

        AddShell(r, t);
        AddFonts(r);
        return r;
    }

    private static LinearGradientBrush V(uint top, uint bottom)
    {
        var b = new LinearGradientBrush(C(top), C(bottom), 90);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Форма и строение оболочки по скину. Fluent — прежний вид: скруглённый «слой» содержимого, пилюли, крупные заголовки.
    /// Системные скины повторяют родные окна своей Windows: Aero (Win7) — панель навигации и панель команд Проводника,
    /// выбор строки с голубым градиентом, синие заголовки 12 pt; Aero2 (Win8) — то же плоско и без скруглений;
    /// AeroLite, Luna, Royale, Classic — системные цвета.
    /// </summary>
    private static void AddShell(Dictionary<string, object?> r, ResolvedTheme t)
    {
        var skin = t.Skin;
        var fluent = skin == SkinKind.Fluent;
        var d = t.IsDark;

        // Скругления как у родных элементов скина: Fluent — 8, Aero — 3, XP — 3, Win8 и Classic — прямые углы
        double card = skin switch { SkinKind.Fluent => 8, SkinKind.Aero or SkinKind.Luna or SkinKind.Royale => 3, _ => 0 };
        double control = skin switch { SkinKind.Fluent => 6, SkinKind.Aero or SkinKind.Luna or SkinKind.Royale => 3, _ => 0 };
        r[ThemeKeys.CardCornerRadius] = new CornerRadius(card);
        r[ThemeKeys.ControlCornerRadius] = new CornerRadius(control);
        r[ThemeKeys.PillCornerRadius] = new CornerRadius(fluent ? 11 : control);
        r[ThemeKeys.ItemCornerRadius] = new CornerRadius(skin == SkinKind.Aero ? 3 : fluent ? 4 : 0);

        // Содержимое: во Fluent — «слой» со скруглённым углом и тонкой рамкой; в системных скинах — обычная клиентская
        // область, от панели навигации её отделяет линия
        r[ThemeKeys.ContentCornerRadius] = fluent ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
        r[ThemeKeys.ContentBorderThickness] = fluent ? new Thickness(1, 1, 0, 0) : new Thickness(0);
        r[ThemeKeys.SidebarBorderThickness] = fluent ? new Thickness(0) : new Thickness(0, 0, 1, 0);
        r[ThemeKeys.ToolbarBorderThickness] = fluent ? new Thickness(0) : new Thickness(0, 0, 0, 1);

        r[ThemeKeys.SubtleHoverBorder] = Brushes.Transparent;
        r[ThemeKeys.ItemSelectedText] = r[ThemeKeys.TextPrimary];
        r[ThemeKeys.Heading] = r[ThemeKeys.TextPrimary];
        r[ThemeKeys.SectionHeader] = r[ThemeKeys.TextSecondary];
        r[ThemeKeys.HeadingFontWeight] = FontWeights.SemiBold;
        r[ThemeKeys.PageTitleFontSize] = 24.0;
        r[ThemeKeys.TitleFontSize] = 20.0;
        r[ThemeKeys.AppFontSize] = 13.0;
        r[ThemeKeys.ActionCursor] = fluent ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

        if (fluent)
        {
            r[ThemeKeys.SidebarBorder] = Brushes.Transparent;
            r[ThemeKeys.ToolbarBackground] = Brushes.Transparent;
            r[ThemeKeys.ToolbarBorder] = Brushes.Transparent;
            r[ThemeKeys.ItemHover] = r[ThemeKeys.SubtleHover];
            r[ThemeKeys.ItemHoverBorder] = Brushes.Transparent;
            r[ThemeKeys.ItemSelected] = r[ThemeKeys.SelectedItem];
            r[ThemeKeys.ItemSelectedBorder] = Brushes.Transparent;
            return;
        }

        // Системные скины: текст 9 pt, заголовок страницы — 12 pt обычным начертанием (Segoe UI в Win7 / 8)
        r[ThemeKeys.HeadingFontWeight] = FontWeights.Normal;
        r[ThemeKeys.PageTitleFontSize] = 16.0;
        r[ThemeKeys.TitleFontSize] = 16.0;
        r[ThemeKeys.AppFontSize] = 12.0;

        // С подложкой (её выбрали явно) панели остаются полупрозрачными — меняются только строки и заголовки
        var opaque = !t.HasBackdrop;

        switch (skin)
        {
            case SkinKind.Aero:
                // Windows 7: бледно-голубые панели Проводника, строки с градиентом и рамкой
                r[ThemeKeys.ItemHover] = V(0xFFFAFBFD, 0xFFEBF3FD);
                r[ThemeKeys.ItemHoverBorder] = B(0xFFB8D6FBu);
                r[ThemeKeys.ItemSelected] = V(0xFFDCEBFC, 0xFFC1DBFC);
                r[ThemeKeys.ItemSelectedBorder] = B(0xFF7DA2CEu);
                r[ThemeKeys.SidebarBorder] = B(0xFFD5DFEAu);
                r[ThemeKeys.ToolbarBackground] = opaque ? V(0xFFFAFCFE, 0xFFE3ECF7) : Brushes.Transparent;
                r[ThemeKeys.ToolbarBorder] = B(opaque ? 0xFFB6C5D8u : 0x33000000u);
                r[ThemeKeys.Heading] = B(0xFF003399u);
                r[ThemeKeys.SectionHeader] = B(0xFF1E3287u);
                if (opaque) r[ThemeKeys.SidebarBackground] = B(0xFFF1F5FBu);
                break;

            case SkinKind.Aero2:
                // Windows 8: те же роли, но плоско
                r[ThemeKeys.ItemHover] = B(0xFFE5F3FBu);
                r[ThemeKeys.ItemHoverBorder] = B(0xFF70C0E7u);
                r[ThemeKeys.ItemSelected] = B(0xFFCBE8F6u);
                r[ThemeKeys.ItemSelectedBorder] = B(0xFF26A0DAu);
                r[ThemeKeys.SidebarBorder] = B(0xFFDADBDCu);
                r[ThemeKeys.ToolbarBackground] = opaque ? B(0xFFF5F6F7u) : Brushes.Transparent;
                r[ThemeKeys.ToolbarBorder] = B(opaque ? 0xFFDADBDCu : 0x33000000u);
                r[ThemeKeys.Heading] = B(0xFF003399u);
                r[ThemeKeys.SectionHeader] = B(0xFF1E3287u);
                if (opaque) r[ThemeKeys.SidebarBackground] = B(0xFFF5F6F7u);
                break;

            default:
                // AeroLite, Luna, Royale, Classic: выбор строки и панели — системными цветами
                r[ThemeKeys.ItemHover] = Brushes.Transparent;
                r[ThemeKeys.ItemHoverBorder] = SystemColors.ControlDarkBrush;
                r[ThemeKeys.ItemSelected] = SystemColors.HighlightBrush;
                r[ThemeKeys.ItemSelectedBorder] = SystemColors.HighlightBrush;
                r[ThemeKeys.ItemSelectedText] = SystemColors.HighlightTextBrush;
                r[ThemeKeys.SidebarBorder] = SystemColors.ControlDarkBrush;
                r[ThemeKeys.ToolbarBackground] = opaque ? SystemColors.ControlBrush : Brushes.Transparent;
                r[ThemeKeys.ToolbarBorder] = SystemColors.ControlDarkBrush;
                if (opaque) r[ThemeKeys.SidebarBackground] = SystemColors.ControlBrush;
                break;
        }

        // Кнопки-иконки ведут себя как кнопки панели инструментов скина: при наведении — та же подсветка, что у строк
        r[ThemeKeys.SubtleHover] = r[ThemeKeys.ItemHover];
        r[ThemeKeys.SubtleHoverBorder] = r[ThemeKeys.ItemHoverBorder];
        r[ThemeKeys.SubtlePressed] = skin is SkinKind.Aero or SkinKind.Aero2 ? r[ThemeKeys.ItemSelected] : SystemColors.ControlLightBrush;
        if (opaque)
        {
            // Рамка карточек и полей — как у поля ввода в теме Windows
            r[ThemeKeys.CardBorder] = B(skin is SkinKind.Aero or SkinKind.Aero2 ? 0xFFD3DAE3u : 0xFFA0A0A0u);
            r[ThemeKeys.Divider] = r[ThemeKeys.SidebarBorder];
        }
    }

    private static Dictionary<string, object?> BuildHighContrast()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var highlight = SystemColors.HighlightBrush;
        var highlightText = SystemColors.HighlightTextBrush;
        var gray = SystemColors.GrayTextBrush;
        var control = SystemColors.ControlBrush;

        var r = new Dictionary<string, object?>
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
            [ThemeKeys.GlassTextEffect] = null,
            [ThemeKeys.CodeBackground] = window,
            [ThemeKeys.CodeKeyword] = text,
            [ThemeKeys.CodeString] = text,
            [ThemeKeys.CodeComment] = gray,
            [ThemeKeys.CodeNumber] = text,
            [ThemeKeys.QuoteBar] = text,
            [ThemeKeys.CardCornerRadius] = new CornerRadius(0),
            [ThemeKeys.ControlCornerRadius] = new CornerRadius(0),
            [ThemeKeys.PillCornerRadius] = new CornerRadius(0),
            [ThemeKeys.ItemCornerRadius] = new CornerRadius(0),
            [ThemeKeys.ContentCornerRadius] = new CornerRadius(0),
            [ThemeKeys.ContentBorderThickness] = new Thickness(1, 1, 0, 0),
            [ThemeKeys.SidebarBorder] = text,
            [ThemeKeys.SidebarBorderThickness] = new Thickness(0),
            [ThemeKeys.ToolbarBackground] = window,
            [ThemeKeys.ToolbarBorder] = text,
            [ThemeKeys.ToolbarBorderThickness] = new Thickness(0),
            [ThemeKeys.ItemHover] = highlight,
            [ThemeKeys.ItemHoverBorder] = highlight,
            [ThemeKeys.ItemSelected] = highlight,
            [ThemeKeys.ItemSelectedBorder] = highlight,
            [ThemeKeys.ItemSelectedText] = highlightText,
            [ThemeKeys.SubtleHoverBorder] = text,
            [ThemeKeys.Heading] = text,
            [ThemeKeys.SectionHeader] = text,
            [ThemeKeys.HeadingFontWeight] = FontWeights.SemiBold,
            [ThemeKeys.PageTitleFontSize] = 24.0,
            [ThemeKeys.TitleFontSize] = 20.0,
            [ThemeKeys.AppFontSize] = 13.0,
            [ThemeKeys.ActionCursor] = System.Windows.Input.Cursors.Hand,
        };
        AddFonts(r);
        return r;
    }

    private static void AddFonts(Dictionary<string, object?> r)
    {
        r[ThemeKeys.AppFontFamily] = OsInfo.IsWin11OrNewer
            ? new FontFamily("Segoe UI Variable Text, Segoe UI")
            : new FontFamily("Segoe UI");
        r[ThemeKeys.MonoFontFamily] = new FontFamily("Cascadia Mono, Consolas, Courier New");
    }
}
