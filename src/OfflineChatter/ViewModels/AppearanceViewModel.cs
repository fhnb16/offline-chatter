using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Converters;
using OfflineChatter.Localization;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

/// <summary>
/// Раздел «Оформление». Меняет ровно то поле AppearanceSettings, которое тронул пользователь, и сразу применяет тему.
/// Значения в списках всегда перечитываются из настроек после любого применения темы (палитра команд, системные события,
/// командная строка): раньше страница хранила свою копию и при следующем изменении записывала её целиком,
/// откатывая тему или подложку, выбранную в другом месте.
/// </summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private bool _loading;

    public AppearanceViewModel(SettingsStore store)
    {
        _store = store;
        Reload();
        ThemeManager.Changed += (_, _) =>
        {
            Reload();
            OnPropertyChanged(nameof(ResolvedSummary));
        };
    }

    public IReadOnlyList<SkinKind> SkinOptions { get; } = Enum.GetValues(typeof(SkinKind)).Cast<SkinKind>().ToList();
    public IReadOnlyList<ThemeMode> ModeOptions { get; } = Enum.GetValues(typeof(ThemeMode)).Cast<ThemeMode>().ToList();
    public IReadOnlyList<BackdropKind> BackdropOptions { get; } = ThemeManager.SupportedBackdrops();
    public IReadOnlyList<AccentSource> AccentOptions { get; } = Enum.GetValues(typeof(AccentSource)).Cast<AccentSource>().ToList();

    /// <summary>Палитра быстрых акцентов (цвета Windows).</summary>
    public IReadOnlyList<string> Swatches { get; } = new[]
    {
        "#0078D4", "#0063B1", "#8764B8", "#B146C2", "#E3008C", "#C30052",
        "#E81123", "#CA5010", "#FF8C00", "#498205", "#107C10", "#00B294",
        "#038387", "#2D7D9A", "#486860", "#68768A",
    };

    [ObservableProperty] private SkinKind _skin;
    [ObservableProperty] private ThemeMode _mode;
    [ObservableProperty] private BackdropKind _backdrop;
    [ObservableProperty] private AccentSource _accent;
    [ObservableProperty] private string _customAccent = "#0078D4";

    public bool IsCustomAccent => Accent == AccentSource.Custom;

    /// <summary>Перечитать значения из настроек, не применяя их заново.</summary>
    private void Reload()
    {
        var a = _store.Current.Appearance;
        _loading = true;
        try
        {
            Skin = a.Skin;
            Mode = a.Mode;
            Backdrop = BackdropOptions.Contains(a.Backdrop) ? a.Backdrop : BackdropKind.Auto;
            Accent = a.Accent;
            CustomAccent = a.CustomAccent;
        }
        finally
        {
            _loading = false;
        }
    }

    public string ResolvedSummary
    {
        get
        {
            var t = ThemeManager.Current;
            if (t == null) return string.Empty;
            if (t.HighContrast) return L.Get("Appearance_NowHighContrast");
            var parts = new List<string>
            {
                EnumDisplayConverter.Get(t.Skin),
                L.Get(t.IsDark ? "Appearance_Dark" : "Appearance_Light"),
                EnumDisplayConverter.Get(t.Backdrop),
            };
            var note = Skin != SkinKind.Fluent && Skin != SkinKind.Auto && t.IsDark
                ? " · " + L.Get("Appearance_DarkUsesFluent")
                : string.Empty;
            return L.F("Appearance_Now", string.Join(" · ", parts)) + note;
        }
    }

    [RelayCommand]
    private void PickSwatch(string hex)
    {
        CustomAccent = hex;
        Accent = AccentSource.Custom;
    }

    partial void OnSkinChanged(SkinKind value) => Commit(a => a.Skin = value);
    partial void OnModeChanged(ThemeMode value) => Commit(a => a.Mode = value);
    partial void OnBackdropChanged(BackdropKind value) => Commit(a => a.Backdrop = value);

    partial void OnAccentChanged(AccentSource value)
    {
        OnPropertyChanged(nameof(IsCustomAccent));
        Commit(a => a.Accent = value);
    }

    partial void OnCustomAccentChanged(string value)
    {
        if (ColorHex.TryParse(value, out var r, out var g, out var b)) Commit(a => a.CustomAccent = ColorHex.Format(r, g, b));
    }

    private void Commit(Action<AppearanceSettings> change)
    {
        if (_loading) return;
        var a = _store.Current.Appearance;
        change(a);
        _store.Save();
        ThemeManager.Apply(a);
    }
}
