using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Библиотека системных промптов (настройки → «Чат»). Выбор пресета в чате — в правой панели.</summary>
public sealed partial class PromptPresetsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private bool _loading;

    public PromptPresetsViewModel(AppServices services)
    {
        _s = services;
        Reload();
        Selected = Items.FirstOrDefault();
    }

    public ObservableCollection<PromptPreset> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private PromptPreset? _selected;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isDefault;

    public bool HasSelection => Selected != null;

    private void Reload()
    {
        Items.Clear();
        foreach (var p in _s.Prompts.Items) Items.Add(p);
    }

    partial void OnSelectedChanged(PromptPreset? value)
    {
        _loading = true;
        Name = value?.Name ?? string.Empty;
        Text = value?.Text ?? string.Empty;
        IsDefault = value != null && _s.Settings.Current.DefaultPromptPresetId == value.Id;
        _loading = false;
    }

    partial void OnNameChanged(string value)
    {
        if (_loading || Selected == null) return;
        Selected.Name = value.Trim();
        Save();
        // Обновить подпись в списке: PromptPreset не уведомляет об изменениях
        var keep = Selected;
        var i = Items.IndexOf(keep);
        if (i >= 0)
        {
            _loading = true;
            Items[i] = keep;
            Selected = keep;
            _loading = false;
        }
    }

    partial void OnTextChanged(string value)
    {
        if (_loading || Selected == null) return;
        Selected.Text = value;
        Save();
    }

    partial void OnIsDefaultChanged(bool value)
    {
        if (_loading || Selected == null) return;
        _s.Settings.Current.DefaultPromptPresetId = value ? Selected.Id : null;
        _s.Settings.Save();
    }

    private void Save() => _s.Prompts.Save();

    [RelayCommand]
    private void Add()
    {
        var p = new PromptPreset { Name = L.Get("Prompts_NewName"), Text = string.Empty };
        _s.Prompts.Items.Add(p);
        Save();
        Items.Add(p);
        Selected = p;
    }

    private bool CanDelete() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected == null) return;
        var p = Selected;
        _s.Prompts.Items.Remove(p);
        if (_s.Settings.Current.DefaultPromptPresetId == p.Id)
        {
            _s.Settings.Current.DefaultPromptPresetId = null;
            _s.Settings.Save();
        }
        Save();
        Items.Remove(p);
        Selected = Items.FirstOrDefault();
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        _s.Prompts.RestoreDefaults(L.IsRussian);
        var keep = Selected?.Id;
        Reload();
        Selected = Items.FirstOrDefault(p => p.Id == keep) ?? Items.FirstOrDefault();
    }
}
