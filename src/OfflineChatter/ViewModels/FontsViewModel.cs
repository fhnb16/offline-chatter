using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

/// <summary>Шрифт в списке: null — шрифт по умолчанию.</summary>
public sealed record FontChoice(string? Name, string Title);

/// <summary>Размер или интервал в списке: null — по умолчанию.</summary>
public sealed record NumberChoice(double? Value, string Title);

/// <summary>
/// «Оформление → Шрифты». Как и остальное оформление, меняет ровно одно поле и применяет тему через ThemeManager,
/// а после любого применения перечитывает значения из настроек.
/// </summary>
public sealed partial class FontsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private bool _loading;
    private bool _familiesRequested;

    public FontsViewModel(SettingsStore store)
    {
        _store = store;
        SharpnessOptions = Enum.GetValues(typeof(TextSharpness)).Cast<TextSharpness>().ToList();
        SmoothingOptions = Enum.GetValues(typeof(TextSmoothing)).Cast<TextSmoothing>().ToList();
        UiFamilies.Add(DefaultChoice(ThemeTokens.DefaultUiFamily));
        ChatFamilies.Add(DefaultChoice(L.Get("Fonts_SameAsUi")));
        CodeFamilies.Add(DefaultChoice(ThemeTokens.DefaultMonoFamily));
        Reload();
        ThemeManager.Changed += (_, _) => Reload();
    }

    private static FontChoice DefaultChoice(string family) =>
        new(null, L.F("Fonts_Default", family.Split(',')[0].Trim()));

    public IReadOnlyList<TextSharpness> SharpnessOptions { get; }
    public IReadOnlyList<TextSmoothing> SmoothingOptions { get; }

    public ObservableCollection<FontChoice> UiFamilies { get; } = new();
    public ObservableCollection<FontChoice> ChatFamilies { get; } = new();
    public ObservableCollection<FontChoice> CodeFamilies { get; } = new();

    public IReadOnlyList<NumberChoice> UiSizes => Sizes(UiDefault, 10, 11, 12, 13, 14, 15, 16, 17, 18);
    public IReadOnlyList<NumberChoice> ChatSizes => Sizes(Typography.DefaultChat, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24);
    public IReadOnlyList<NumberChoice> CodeSizes => Sizes(Typography.DefaultCode, 10, 11, 12, 12.5, 13, 14, 15, 16, 18);

    /// <summary>Интервал — множитель размера шрифта; меньше ~1,4 не бывает: у Segoe UI естественная строка уже 1,33.</summary>
    public IReadOnlyList<NumberChoice> LineSpacings { get; } =
        new[] { new NumberChoice(null, L.Get("Fonts_LineDefault")) }
            .Concat(new[] { 1.5, 1.6, 1.8, 2.0 }.Select(v => new NumberChoice(v, Format(v))))
            .ToList();

    /// <summary>Основной размер интерфейса у текущего скина: Fluent — 13 px, системные скины — 12 px (9 pt).</summary>
    private static double UiDefault => ThemeManager.Current is { HighContrast: false, Skin: not SkinKind.Fluent } ? 12 : 13;

    private static IReadOnlyList<NumberChoice> Sizes(double byDefault, params double[] values)
    {
        var list = new List<NumberChoice> { new(null, L.F("Fonts_Default", Format(byDefault))) };
        list.AddRange(values.Select(v => new NumberChoice(v, Format(v))));
        return list;
    }

    private static string Format(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);

    [ObservableProperty] private FontChoice? _uiFamily;
    [ObservableProperty] private NumberChoice? _uiSize;
    [ObservableProperty] private FontChoice? _chatFamily;
    [ObservableProperty] private NumberChoice? _chatSize;
    [ObservableProperty] private NumberChoice? _chatLineSpacing;
    [ObservableProperty] private FontChoice? _codeFamily;
    [ObservableProperty] private NumberChoice? _codeSize;
    [ObservableProperty] private TextSharpness _sharpness;
    [ObservableProperty] private TextSmoothing _smoothing;

    /// <summary>Что значит «Авто» на этой системе.</summary>
    public string SharpnessHint => OsInfo.IsWin10OrNewer ? L.Get("Fonts_SharpnessHintModern") : L.Get("Fonts_SharpnessHintLegacy");

    /// <summary>Список шрифтов системы — при первом открытии раздела, в фоне (на Win7 с сотнями шрифтов это заметно).</summary>
    public void EnsureFamilies()
    {
        if (_familiesRequested) return;
        _familiesRequested = true;
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        _ = Task.Run(() =>
        {
            var all = new List<(string name, string title, bool mono)>();
            foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                var name = family.Source;
                if (string.IsNullOrEmpty(name) || name.StartsWith("#", StringComparison.Ordinal)) continue;
                var title = family.FamilyNames.TryGetValue(language, out var local) ? local : name;
                all.Add((name, title, IsMonospaced(family)));
            }
            return all.GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(x => x.title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            _loading = true;
            try
            {
                foreach (var (name, title, mono) in t.Result)
                {
                    var choice = new FontChoice(name, title);
                    UiFamilies.Add(choice);
                    ChatFamilies.Add(choice);
                    if (mono) CodeFamilies.Add(choice);
                }
            }
            finally
            {
                _loading = false;
            }
            Reload();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Моноширинный: у «i» и «W» одинаковая ширина.</summary>
    private static bool IsMonospaced(FontFamily family)
    {
        try
        {
            var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!face.TryGetGlyphTypeface(out var g)) return false;
            var map = g.CharacterToGlyphMap;
            if (!map.TryGetValue('i', out var i) || !map.TryGetValue('W', out var w)) return false;
            return Math.Abs(g.AdvanceWidths[i] - g.AdvanceWidths[w]) < 0.001;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>Перечитать значения из настроек, не применяя их заново.</summary>
    private void Reload()
    {
        var f = _store.Current.Appearance.Fonts;
        _loading = true;
        try
        {
            UiFamily = Pick(UiFamilies, f.UiFamily);
            ChatFamily = Pick(ChatFamilies, f.ChatFamily);
            CodeFamily = Pick(CodeFamilies, f.CodeFamily);
            OnPropertyChanged(nameof(UiSizes));
            UiSize = UiSizes.FirstOrDefault(c => c.Value == f.UiSize) ?? UiSizes[0];
            ChatSize = ChatSizes.FirstOrDefault(c => c.Value == f.ChatSize) ?? ChatSizes[0];
            CodeSize = CodeSizes.FirstOrDefault(c => c.Value == f.CodeSize) ?? CodeSizes[0];
            ChatLineSpacing = LineSpacings.FirstOrDefault(c => c.Value == f.ChatLineSpacing) ?? LineSpacings[0];
            Sharpness = f.Sharpness;
            Smoothing = f.Smoothing;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Выбранный шрифт; если его нет в списке (ещё не загружен или удалён) — добавляем, чтобы выбор не терялся.</summary>
    private static FontChoice Pick(ObservableCollection<FontChoice> list, string? name)
    {
        if (name == null) return list[0];
        var found = list.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        var added = new FontChoice(name, name);
        list.Insert(1, added);
        return added;
    }

    partial void OnUiFamilyChanged(FontChoice? value) => Commit(f => f.UiFamily = value?.Name);
    partial void OnUiSizeChanged(NumberChoice? value) => Commit(f => f.UiSize = value?.Value);
    partial void OnChatFamilyChanged(FontChoice? value) => Commit(f => f.ChatFamily = value?.Name);
    partial void OnChatSizeChanged(NumberChoice? value) => Commit(f => f.ChatSize = value?.Value);
    partial void OnChatLineSpacingChanged(NumberChoice? value) => Commit(f => f.ChatLineSpacing = value?.Value);
    partial void OnCodeFamilyChanged(FontChoice? value) => Commit(f => f.CodeFamily = value?.Name);
    partial void OnCodeSizeChanged(NumberChoice? value) => Commit(f => f.CodeSize = value?.Value);
    partial void OnSharpnessChanged(TextSharpness value) => Commit(f => f.Sharpness = value);
    partial void OnSmoothingChanged(TextSmoothing value) => Commit(f => f.Smoothing = value);

    [RelayCommand]
    private void Reset()
    {
        var a = _store.Current.Appearance;
        a.Fonts = new FontSettings();
        _store.Save();
        ThemeManager.Apply(a);
    }

    private void Commit(Action<FontSettings> change)
    {
        if (_loading) return;
        var a = _store.Current.Appearance;
        change(a.Fonts);
        a.Fonts.Normalize();
        _store.Save();
        ThemeManager.Apply(a);
    }
}
