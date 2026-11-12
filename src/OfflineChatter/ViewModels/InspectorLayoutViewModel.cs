using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Флажок «показывать блок в правой панели чата».</summary>
public sealed class InspectorToggle : ObservableObject
{
    private readonly InspectorLayoutViewModel _owner;

    public InspectorToggle(InspectorLayoutViewModel owner, InspectorSection section, bool visible)
    {
        _owner = owner;
        Section = section;
        _isVisible = visible;
    }

    public InspectorSection Section { get; }
    public string Title => L.Get("Inspector_Section_" + Section);
    public string Hint => L.Get("Inspector_Section_" + Section + "_Hint");

    private bool _isVisible;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) _owner.OnToggled();
        }
    }

    /// <summary>Выставить значение без сохранения (сброс к набору по умолчанию).</summary>
    internal void SetSilently(bool value) => SetProperty(ref _isVisible, value, nameof(IsVisible));
}

/// <summary>
/// Какие блоки показывает правая панель чата. Один экземпляр на окно: его правят сама панель (кнопка «Настроить»),
/// «Настройки → Чат» и мастер первого запуска — все три места показывают один и тот же список.
/// </summary>
public sealed partial class InspectorLayoutViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private bool _loading;

    public InspectorLayoutViewModel(SettingsStore settings)
    {
        _settings = settings;
        var active = InspectorLayout.Resolve(settings.Current.Window.InspectorSections);
        Items = Enum.GetValues(typeof(InspectorSection)).Cast<InspectorSection>()
            .Select(s => new InspectorToggle(this, s, active.Contains(s))).ToList();
        ChatItems = Items.Where(i => !IsServerSection(i.Section)).ToList();
        ServerItems = Items.Where(i => IsServerSection(i.Section)).ToList();
    }

    public IReadOnlyList<InspectorToggle> Items { get; }

    /// <summary>Блоки беседы: промпт, размышления, параметры, подключение, токены.</summary>
    public IReadOnlyList<InspectorToggle> ChatItems { get; }

    /// <summary>Подробности о llama-server и модели.</summary>
    public IReadOnlyList<InspectorToggle> ServerItems { get; }

    /// <summary>Набор блоков изменился.</summary>
    public event EventHandler? Changed;

    public static bool IsServerSection(InspectorSection s) => s >= InspectorSection.Diagnosis;

    public bool Has(InspectorSection section) => Items.First(i => i.Section == section).IsVisible;

    public bool ShowPrompt => Has(InspectorSection.Prompt);
    public bool ShowReasoning => Has(InspectorSection.Reasoning);
    public bool ShowParameters => Has(InspectorSection.Parameters);

    /// <summary>Виден хотя бы один блок о сервере — тогда нужен их общий заголовок.</summary>
    public bool ShowServerHeader => ServerItems.Any(i => i.IsVisible);

    /// <summary>В панели не осталось ни одного блока — показываем подсказку, как их вернуть.</summary>
    public bool IsEmpty => Items.All(i => !i.IsVisible);

    internal void OnToggled()
    {
        if (_loading) return;
        _settings.Current.Window.InspectorSections = InspectorLayout.Store(Items.Where(i => i.IsVisible).Select(i => i.Section));
        _settings.Save();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ShowPrompt));
        OnPropertyChanged(nameof(ShowReasoning));
        OnPropertyChanged(nameof(ShowParameters));
        OnPropertyChanged(nameof(ShowServerHeader));
        OnPropertyChanged(nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Вернуть набор по умолчанию.</summary>
    [RelayCommand]
    private void Reset()
    {
        _loading = true;
        foreach (var i in Items) i.SetSilently(InspectorLayout.Defaults.Contains(i.Section));
        _loading = false;
        _settings.Current.Window.InspectorSections = null;
        _settings.Save();
        Notify();
    }

    /// <summary>Показать все блоки.</summary>
    [RelayCommand]
    private void ShowAll()
    {
        _loading = true;
        foreach (var i in Items) i.SetSilently(true);
        _loading = false;
        OnToggled();
    }
}
