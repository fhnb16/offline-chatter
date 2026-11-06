using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Converters;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}

/// <summary>Пустая строка → Visible (для подсказок-заглушек в полях ввода).</summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Человекочитаемые названия значений перечислений.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    // TODO(l10n): перенести в Strings.resx / Strings.ru.resx
    private static readonly Dictionary<object, string> Names = new()
    {
        [SkinKind.Auto] = "Авто (по версии Windows)",
        [SkinKind.Aero] = "Aero (Windows 7)",
        [SkinKind.Aero2] = "Aero2 (Windows 8/10)",
        [SkinKind.AeroLite] = "Aero Lite",
        [SkinKind.Luna] = "Luna (Windows XP)",
        [SkinKind.Royale] = "Royale (Media Center)",
        [SkinKind.Classic] = "Классическая",
        [SkinKind.Fluent] = "Fluent (Windows 11)",

        [ThemeMode.System] = "Как в системе",
        [ThemeMode.Light] = "Светлая",
        [ThemeMode.Dark] = "Тёмная",

        [BackdropKind.Auto] = "Авто",
        [BackdropKind.None] = "Нет (сплошной фон)",
        [BackdropKind.AeroGlass] = "Aero Glass",
        [BackdropKind.Blur] = "Размытие",
        [BackdropKind.Acrylic] = "Acrylic",
        [BackdropKind.Mica] = "Mica",
        [BackdropKind.MicaAlt] = "Mica Alt",

        [AccentSource.System] = "Системный",
        [AccentSource.Custom] = "Свой цвет",
    };

    public static string Get(object value) => Names.TryGetValue(value, out var s) ? s : value.ToString();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value == null ? string.Empty : Get(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
