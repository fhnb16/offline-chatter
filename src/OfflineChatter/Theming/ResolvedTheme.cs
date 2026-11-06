using System.Windows.Media;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Theming;

/// <summary>
/// Фактически применённая тема: что получилось из настроек пользователя после учёта
/// возможностей ОС, тёмного режима и High Contrast.
/// </summary>
public sealed record ResolvedTheme(
    SkinKind Skin,
    bool IsDark,
    BackdropKind Backdrop,
    AccentPalette Palette,
    bool HighContrast,
    Color SolidWindowColor)
{
    public bool HasBackdrop => Backdrop != BackdropKind.None;

    /// <summary>Акцент для заливок (кнопки, пузырь пользователя), как в Win11: Dark1 на светлой, Light2 на тёмной теме.</summary>
    public Color AccentFill => IsDark ? Palette.Light2 : Palette.Dark1;

    /// <summary>Акцент для текста и ссылок.</summary>
    public Color AccentText => IsDark ? Palette.Light3 : Palette.Dark2;
}
