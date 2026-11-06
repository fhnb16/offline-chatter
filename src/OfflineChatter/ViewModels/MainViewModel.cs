using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public enum MessageRole
{
    User,
    Assistant,
    System,
}

public sealed partial class MessageViewModel : ObservableObject
{
    public MessageViewModel(MessageRole role, string text)
    {
        Role = role;
        _text = text;
    }

    public MessageRole Role { get; }
    public bool IsUser => Role == MessageRole.User;

    [ObservableProperty] private string _text;
}

public sealed partial class ChatListItem : ObservableObject
{
    public ChatListItem(string title) => _title = title;

    [ObservableProperty] private string _title;

    public ObservableCollection<MessageViewModel> Messages { get; } = new();
}

/// <summary>
/// Главное окно. На этапе 1 — каркас: список чатов, сообщения в памяти, оформление.
/// Провайдеры и хранилище подключаются на этапах 5–6.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    public MainViewModel(SettingsStore settings, AppPaths paths)
    {
        _settings = settings;
        Appearance = new AppearanceViewModel(settings);
        _isInspectorVisible = settings.Current.Window.InspectorVisible;

        OsName = OsInfo.DisplayName;
        RuntimeInfo = $".NET Framework CLR {Environment.Version}, процесс {(Environment.Is64BitProcess ? "x64" : "x86")}"; // TODO(l10n)
        DataLocation = (paths.IsPortable ? "Портативный режим: " : "Данные: ") + paths.Root; // TODO(l10n)

        NewChat();
    }

    public AppearanceViewModel Appearance { get; }

    public ObservableCollection<ChatListItem> Chats { get; } = new();

    public string OsName { get; }
    public string RuntimeInfo { get; }
    public string DataLocation { get; }

    // TODO(l10n) + заменить реальным состоянием движка (этап 3)
    public string EngineStatus => "Движок не установлен";
    public string ModelName => "Модель не выбрана";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessages))]
    private ChatListItem? _selectedChat;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _isInspectorVisible;

    public bool HasMessages => SelectedChat?.Messages.Count > 0;

    partial void OnIsInspectorVisibleChanged(bool value) => _settings.Current.Window.InspectorVisible = value;

    partial void OnSelectedChatChanged(ChatListItem? oldValue, ChatListItem? newValue)
    {
        if (oldValue != null) oldValue.Messages.CollectionChanged -= OnMessagesChanged;
        if (newValue != null) newValue.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasMessages));

    [RelayCommand]
    private void NewChat()
    {
        var chat = new ChatListItem("Новый чат"); // TODO(l10n)
        Chats.Insert(0, chat);
        SelectedChat = chat;
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    [RelayCommand]
    private void UseSuggestion(string text) => InputText = text;

    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (SelectedChat == null) NewChat();
        var chat = SelectedChat!;
        var text = InputText.Trim();
        InputText = string.Empty;

        if (chat.Messages.Count == 0)
            chat.Title = text.Length > 40 ? text.Substring(0, 40).TrimEnd() + "…" : text;

        chat.Messages.Add(new MessageViewModel(MessageRole.User, text));
        // Заглушка до этапа 6 (провайдеры). TODO(l10n)
        chat.Messages.Add(new MessageViewModel(MessageRole.Assistant,
            "Движок ещё не подключён. Загрузка llama.cpp и моделей появится на этапах 3–4 (см. TODO.md)."));
    }
}
