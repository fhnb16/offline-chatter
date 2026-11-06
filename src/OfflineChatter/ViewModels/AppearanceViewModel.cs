using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Converters;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

/// <summary>Раздел «Оформление»: правит AppearanceSettings и сразу применяет тему.</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private bool _loading;

    public AppearanceViewModel(SettingsStore store)
    {
        _store = store;
        var a = store.Current.Appearance;
        _loading = true;
        Skin = a.Skin;
        Mode = a.Mode;
        Backdrop = BackdropOptions.Contains(a.Backdrop) ? a.Backdrop : BackdropKind.Auto;
        Accent = a.Accent;
        CustomAccent = a.CustomAccent;
        _loading = false;

        ThemeManager.Changed += (_, _) => OnPropertyChanged(nameof(ResolvedSummary));
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

    public string ResolvedSummary
    {
        get
        {
            var t = ThemeManager.Current;
            if (t == null) return string.Empty;
            if (t.HighContrast) return "Сейчас: режим высокой контрастности"; // TODO(l10n)
            var parts = new List<string>
            {
                EnumDisplayConverter.Get(t.Skin),
                t.IsDark ? "тёмная" : "светлая",
                EnumDisplayConverter.Get(t.Backdrop),
            };
            var note = Skin != SkinKind.Fluent && Skin != SkinKind.Auto && t.IsDark
                ? " · в тёмной теме контролы рисуются Fluent-стилем"
                : string.Empty;
            return "Сейчас: " + string.Join(" · ", parts) + note; // TODO(l10n)
        }
    }

    [RelayCommand]
    private void PickSwatch(string hex)
    {
        CustomAccent = hex;
        Accent = AccentSource.Custom;
    }

    partial void OnSkinChanged(SkinKind value) => Commit();
    partial void OnModeChanged(ThemeMode value) => Commit();
    partial void OnBackdropChanged(BackdropKind value) => Commit();

    partial void OnAccentChanged(AccentSource value)
    {
        OnPropertyChanged(nameof(IsCustomAccent));
        Commit();
    }

    partial void OnCustomAccentChanged(string value)
    {
        if (ColorHex.TryParse(value, out _, out _, out _)) Commit();
    }

    private void Commit()
    {
        if (_loading) return;
        var a = _store.Current.Appearance;
        a.Skin = Skin;
        a.Mode = Mode;
        a.Backdrop = Backdrop;
        a.Accent = Accent;
        if (ColorHex.TryParse(CustomAccent, out var r, out var g, out var b))
            a.CustomAccent = ColorHex.Format(r, g, b);

        ThemeManager.Apply(a);
        _store.Save();
    }
}
