using System;

namespace OfflineChatter.Core.Storage;

/// <summary>Как рисовать глифы.</summary>
public enum TextSharpness
{
    /// <summary>
    /// Win7 и Win8 — всегда по пиксельной сетке (там сглаженные контуры мелкого текста выглядят размытыми при любом
    /// масштабе); Win10/11 — по сетке при 100 %, точные контуры на экранах высокой плотности.
    /// </summary>
    Auto,
    /// <summary>По пиксельной сетке (TextFormattingMode.Display): чётко, как в системных окнах.</summary>
    Sharp,
    /// <summary>Точные контуры шрифта (TextFormattingMode.Ideal): ровнее на HiDPI, мягче на обычных экранах.</summary>
    Smooth,
}

/// <summary>Сглаживание краёв глифов.</summary>
public enum TextSmoothing
{
    /// <summary>Как настроено в Windows.</summary>
    Auto,
    ClearType,
    Grayscale,
}

/// <summary>
/// Шрифты интерфейса, текста сообщений и кода. Пустые значения — шрифт и размер по умолчанию для скина:
/// так обновления программы улучшают значения по умолчанию у всех, кто их не трогал.
/// </summary>
public sealed class FontSettings
{
    public string? UiFamily { get; set; }
    /// <summary>Основной размер текста интерфейса, px (остальные размеры интерфейса сдвигаются на ту же величину).</summary>
    public double? UiSize { get; set; }

    public string? ChatFamily { get; set; }
    public double? ChatSize { get; set; }
    /// <summary>Межстрочный интервал текста сообщений, множитель размера шрифта (пусто — как задумано шрифтом).</summary>
    public double? ChatLineSpacing { get; set; }

    /// <summary>Моноширинный шрифт: код, а также цифры статистики и журналы.</summary>
    public string? CodeFamily { get; set; }
    public double? CodeSize { get; set; }

    public TextSharpness Sharpness { get; set; } = TextSharpness.Auto;
    public TextSmoothing Smoothing { get; set; } = TextSmoothing.Auto;

    public const double MinUi = 10, MaxUi = 20;
    public const double MinChat = 10, MaxChat = 28;
    public const double MinCode = 9, MaxCode = 24;
    public const double MinLineSpacing = 1.0, MaxLineSpacing = 2.0;

    /// <summary>Приводит значения в допустимые пределы после ручной правки файла.</summary>
    public void Normalize()
    {
        UiFamily = Family(UiFamily);
        ChatFamily = Family(ChatFamily);
        CodeFamily = Family(CodeFamily);
        UiSize = Size(UiSize, MinUi, MaxUi);
        ChatSize = Size(ChatSize, MinChat, MaxChat);
        CodeSize = Size(CodeSize, MinCode, MaxCode);
        ChatLineSpacing = ChatLineSpacing is { } ls && !double.IsNaN(ls) ? Math.Round(Math.Max(MinLineSpacing, Math.Min(MaxLineSpacing, ls)), 2) : null;
        if (!Enum.IsDefined(typeof(TextSharpness), Sharpness)) Sharpness = TextSharpness.Auto;
        if (!Enum.IsDefined(typeof(TextSmoothing), Smoothing)) Smoothing = TextSmoothing.Auto;
    }

    private static string? Family(string? name)
    {
        var n = name?.Trim();
        return string.IsNullOrEmpty(n) || n!.Length > 200 ? null : n;
    }

    /// <summary>Размер с шагом 0,5 px в пределах; мусор — значение по умолчанию.</summary>
    private static double? Size(double? value, double min, double max) =>
        value is { } v && !double.IsNaN(v) && !double.IsInfinity(v) ? Math.Round(Math.Max(min, Math.Min(max, v)) * 2) / 2 : null;
}

/// <summary>Все размеры шрифтов программы, px.</summary>
public sealed record TypeScale(
    double Ui, double Caption, double Small, double Subtitle, double Large, double Title, double PageTitle,
    double Chat, double ChatSmall, double Code, double? ChatLineHeight);

public static class Typography
{
    /// <summary>Размеры по умолчанию, которые не зависят от скина.</summary>
    public const double DefaultChat = 14, DefaultCode = 12.5;

    /// <summary>
    /// Шкала размеров. <paramref name="uiBase"/>, <paramref name="title"/>, <paramref name="pageTitle"/> — значения скина
    /// (Fluent 13/20/24, системные скины 12/16/16). Если пользователь задал размер интерфейса, все размеры интерфейса
    /// сдвигаются на одну величину: соотношения между подписями, заголовками и текстом сохраняются.
    /// </summary>
    public static TypeScale Resolve(FontSettings f, double uiBase, double title, double pageTitle)
    {
        var d = (f.UiSize ?? uiBase) - uiBase;
        var chat = f.ChatSize ?? DefaultChat;
        return new TypeScale(
            Ui: uiBase + d,
            Caption: 12 + d,
            Small: 11 + d,
            Subtitle: 14 + d,
            Large: 15 + d,
            Title: title + d,
            PageTitle: pageTitle + d,
            Chat: chat,
            ChatSmall: chat - 1.5,
            Code: f.CodeSize ?? DefaultCode,
            ChatLineHeight: f.ChatLineSpacing is { } ls ? Math.Round(chat * ls, 1) : null);
    }

    /// <summary>Нужно ли рисовать текст по пиксельной сетке.</summary>
    /// <param name="modernOs">Windows 10 и новее.</param>
    /// <param name="dpiScale">Масштаб экрана (1.0 — 100 %).</param>
    public static bool UseDisplayMode(TextSharpness sharpness, bool modernOs, double dpiScale) => sharpness switch
    {
        TextSharpness.Sharp => true,
        TextSharpness.Smooth => false,
        _ => !modernOs || dpiScale <= 1.0,
    };
}
