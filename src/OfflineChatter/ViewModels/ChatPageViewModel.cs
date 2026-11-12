using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Export;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Rendering;

namespace OfflineChatter.ViewModels;

/// <summary>Чат в боковой панели.</summary>
public sealed partial class ChatListItem : ObservableObject
{
    public ChatListItem(ChatSession session)
    {
        Session = session;
        _title = string.IsNullOrWhiteSpace(session.Title) ? L.Get("Chat_NewTitle") : session.Title;
        _pinned = session.Pinned;
    }

    public ChatSession Session { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _pinned;

    partial void OnTitleChanged(string value) => Session.Title = value;
    partial void OnPinnedChanged(bool value) => Session.Pinned = value;
}

public sealed class AttachmentView
{
    public AttachmentView(ChatAttachment a)
    {
        Attachment = a;
        Thumbnail = ImageAttachments.Thumbnail(a);
    }

    public ChatAttachment Attachment { get; }
    public BitmapImage? Thumbnail { get; }
    public bool IsImage => Attachment.Kind == AttachmentKind.Image;
    public string Name => Attachment.Name;
}

/// <summary>Сообщение в ленте (узел активной ветки).</summary>
public sealed partial class MessageViewModel : ObservableObject
{
    public MessageViewModel(ChatMessage message, int variantIndex, int variantCount)
    {
        Message = message;
        _content = message.Content;
        _reasoning = message.Reasoning ?? string.Empty;
        _error = message.Error;
        VariantIndex = variantIndex;
        VariantCount = variantCount;
        Attachments = message.Attachments.Select(a => new AttachmentView(a)).ToList();
        UpdateStats();
        RefreshTools();
    }

    /// <summary>Вызовы инструментов в этом ответе (карточки под текстом).</summary>
    public ObservableCollection<ToolCallView> ToolCalls { get; } = new();
    public bool HasToolCalls => ToolCalls.Count > 0;

    public void RefreshTools()
    {
        ToolCalls.Clear();
        foreach (var t in Message.ToolCalls) ToolCalls.Add(new ToolCallView(t));
        OnPropertyChanged(nameof(HasToolCalls));
    }

    public ChatMessage Message { get; }
    public ChatRole Role => Message.Role;
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public IReadOnlyList<AttachmentView> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;
    public int VariantIndex { get; }
    public int VariantCount { get; }
    public bool HasVariants => VariantCount > 1;
    public string VariantText => $"{VariantIndex + 1} / {VariantCount}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThinking), nameof(ReasoningTail), nameof(HasContent))]
    private string _content;

    /// <summary>У шага с одними вызовами инструментов текста нет — пустое место под него в пузыре не оставляем.</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ReasoningHeader), nameof(IsThinking), nameof(ReasoningTail))]
    private string _reasoning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThinking), nameof(ReasoningHeader), nameof(ReasoningTail))]
    private bool _isStreaming;

    [ObservableProperty] private bool _isPreparing;
    [ObservableProperty] private string? _preparingText;

    /// <summary>
    /// Пока модель размышляет — последние строки рассуждений: при долгом размышлении или медленном выводе
    /// видно, что работа идёт, без раскрытия блока. После размышлений — null.
    /// </summary>
    public string? ReasoningTail
    {
        get
        {
            if (!IsThinking) return null;
            var r = Reasoning.TrimEnd();
            const int max = 280;
            if (r.Length <= max) return r;
            var tail = r.Substring(r.Length - max);
            var cut = tail.IndexOfAny(new[] { ' ', '\n' });
            return "…" + (cut > 0 && cut < 40 ? tail.Substring(cut + 1) : tail);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty] private string? _statsText;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasReasoning => !string.IsNullOrEmpty(Reasoning);
    public bool IsThinking => IsStreaming && string.IsNullOrEmpty(Content) && HasReasoning;

    public string ReasoningHeader
    {
        get
        {
            if (IsThinking) return L.Get("Chat_Thinking");
            var ms = Message.Stats?.ReasoningMs;
            return ms is > 0 ? L.F("Chat_ThoughtFor", Units.FormatDuration(TimeSpan.FromMilliseconds(ms.Value))) : L.Get("Chat_Reasoning");
        }
    }

    public void UpdateStats()
    {
        var s = Message.Stats;
        if (s == null || !IsAssistant)
        {
            StatsText = null;
            return;
        }
        var parts = new List<string>();
        var approx = s.Estimated ? "≈" : string.Empty;
        if (s.TokensPerSecond is { } tps) parts.Add(L.F("Stats_Gen", approx + tps.ToString("0.0")));
        if (s.CompletionTokens > 0) parts.Add(L.F("Stats_Out", approx + s.CompletionTokens.ToString("N0")));
        if (s.PromptTokens > 0)
            parts.Add(s.CachedTokens is > 0
                ? L.F("Stats_InCached", approx + s.PromptTokens.ToString("N0"), s.CachedTokens.Value.ToString("N0"))
                : L.F("Stats_In", approx + s.PromptTokens.ToString("N0")));
        // Скорость промпта по нескольким токенам (почти всё взято из кэша) — шум, а не измерение
        if (s.PromptTokensPerSecond is { } pps && (s.PromptEvalTokens ?? s.PromptTokens) >= 16) parts.Add(L.F("Stats_PromptSpeed", pps.ToString("0")));
        if (s.TimeToFirstTokenMs > 0) parts.Add(L.F("Stats_Ttft", Units.FormatDuration(TimeSpan.FromMilliseconds(s.TimeToFirstTokenMs))));
        if (s.DraftTotal is > 0 && s.DraftAccepted is { } acc) parts.Add(L.F("Stats_Draft", (100.0 * acc / s.DraftTotal.Value).ToString("0")));
        if (!string.IsNullOrEmpty(s.Model)) parts.Add(s.Model!);
        if (s.FinishReason == "length") parts.Add(L.Get("Stats_Truncated"));
        if (s.FinishReason == "cancelled") parts.Add(L.Get("Stats_Stopped"));
        StatsText = string.Join(" · ", parts);
    }
}

public sealed record ModelOption(string Ref, string Title, string Group, bool Vision = false);

/// <summary>Карточка вызова инструмента.</summary>
public sealed class ToolCallView
{
    public ToolCallView(ToolCall call) => Call = call;
    public ToolCall Call { get; }
    public string Header => Call.Name + (Call.Result == null ? " …" : Call.IsError ? " — " + L.Get("Download_Failed") : string.Empty);
    public string Arguments => Call.Arguments;
    public string Result => Call.Result == null ? string.Empty : Call.Result.Length > 3000 ? Call.Result.Substring(0, 3000) + "…" : Call.Result;
    public bool IsError => Call.IsError;
}

public sealed record ThinkingOption(ReasoningLevel Value, string Title);

public sealed record ReasoningStyleOption(ReasoningStyle Value, string Title);

/// <summary>Страница чата: лента сообщений, ввод, генерация, ветвление, вложения, параметры беседы.</summary>
public sealed partial class ChatPageViewModel : PageViewModel
{
    private readonly AppServices _s;
    private ChatListItem? _lastChat;
    private CancellationTokenSource? _genCts;
    private readonly ICollectionView _filtered;

    public ChatPageViewModel(AppServices services, ServerInsightViewModel server, InspectorLayoutViewModel layout)
    {
        _s = services;
        Server = server;
        Layout = layout;
        Endpoint = new EndpointStatusViewModel(services, server);
        TokensGroup = new InsightGroup(L.Get("Inspector_Section_Tokens"));
        Layout.Changed += (_, _) => RebuildStatGroups();
        RebuildStatGroups();
        // Размер контекста известен только у запущенного сервера — строка «Контекст» обновляется вместе с ним
        Server.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerInsightViewModel.State)) RefreshTokens();
        };
        _isInspectorVisible = services.Settings.Current.Window.InspectorVisible;
        if (System.Windows.Application.Current is { } app) app.Exit += (_, _) => FlushPromptSave();
        foreach (var c in services.Chats.LoadAll()) Chats.Add(new ChatListItem(c));
        _filtered = CollectionViewSource.GetDefaultView(Chats);
        _filtered.Filter = o => o is ChatListItem c && ChatStore.Matches(c.Session, SearchText);
        _filtered.SortDescriptions.Add(new SortDescription(nameof(ChatListItem.Pinned), ListSortDirection.Descending));
        RefreshModels();
        _s.Providers.Changed += (_, _) => RefreshModels();
        _s.Prompts.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PromptPresets));
            SyncPreset();
        };

        if (Chats.Count == 0) NewChat();
        else SelectedChat = Chats[0];
    }

    /// <summary>Состояние llama-server, сборка и модель (шапка и правая панель).</summary>
    public ServerInsightViewModel Server { get; }

    /// <summary>Какие блоки показывает правая панель.</summary>
    public InspectorLayoutViewModel Layout { get; }

    /// <summary>Подключение к эндпоинту выбранной модели: локальный сервер или API-провайдер.</summary>
    public EndpointStatusViewModel Endpoint { get; }

    /// <summary>Блок «Токены»: контекст, последний ответ, всего за чат.</summary>
    public InsightGroup TokensGroup { get; }

    /// <summary>Блоки статистики беседы в правой панели («Подключение», «Токены») — те, что включены.</summary>
    public ObservableCollection<InsightGroup> StatGroups { get; } = new();

    private void RebuildStatGroups()
    {
        StatGroups.Clear();
        if (Layout.Has(Core.Storage.InspectorSection.Connection)) StatGroups.Add(Endpoint.Group);
        if (Layout.Has(Core.Storage.InspectorSection.Tokens)) StatGroups.Add(TokensGroup);
    }

    public override PageKind Kind => PageKind.Chat;
    public override string Title => SelectedChat?.Title ?? L.Get("Nav_Chat");

    public ObservableCollection<ChatListItem> Chats { get; } = new();
    public ICollectionView FilteredChats => _filtered;
    public ObservableCollection<MessageViewModel> Messages { get; } = new();
    public ObservableCollection<AttachmentView> PendingAttachments { get; } = new();
    public ObservableCollection<ModelOption> ModelOptions { get; } = new();

    public IReadOnlyList<ThinkingOption> ThinkingOptions { get; } = new[]
    {
        new ThinkingOption(ReasoningLevel.Auto, L.Get("Chat_ThinkingAuto")),
        new ThinkingOption(ReasoningLevel.Off, L.Get("Chat_ThinkingOff")),
        new ThinkingOption(ReasoningLevel.Low, L.Get("Chat_ThinkingLow")),
        new ThinkingOption(ReasoningLevel.Medium, L.Get("Chat_ThinkingMedium")),
        new ThinkingOption(ReasoningLevel.High, L.Get("Chat_ThinkingHigh")),
    };

    /// <summary>Как модель размышляет: «Авто» определяет по шаблону чата, названию модели и провайдеру.</summary>
    public IReadOnlyList<ReasoningStyleOption> ReasoningStyles { get; } =
        Enum.GetValues(typeof(ReasoningStyle)).Cast<ReasoningStyle>()
            .Select(s => new ReasoningStyleOption(s, L.Get("Enum_ReasoningStyle_" + s))).ToList();

    [ObservableProperty] private ReasoningStyleOption? _reasoningStyle;

    /// <summary>Что определилось в последнем запросе (при «Авто»).</summary>
    [ObservableProperty] private string? _reasoningHint;

    partial void OnReasoningStyleChanged(ReasoningStyleOption? value)
    {
        if (SelectedChat == null || value == null) return;
        SelectedChat.Session.ReasoningStyle = value.Value == Core.Chat.ReasoningStyle.Auto ? null : value.Value;
        SaveChat();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessages), nameof(Title))]
    private ChatListItem? _selectedChat;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _isInspectorVisible;

    /// <summary>Компактный режим: правая панель и второстепенные кнопки скрыты.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _isCompact;

    public bool ShowInspector => IsInspectorVisible && !IsCompact;
    [ObservableProperty] private ModelOption? _selectedModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isGenerating;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _liveStats = L.Get("Chat_StatsEmpty");
    [ObservableProperty] private double _contextRatio;
    [ObservableProperty] private string? _contextText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private MessageViewModel? _editing;

    // ----- параметры беседы (инспектор) -----
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private ThinkingOption? _thinking;
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private bool _overrideTemperature;
    [ObservableProperty] private bool _toolsEnabled;

    public bool IsEditing => Editing != null;
    public bool HasMessages => Messages.Count > 0;
    public bool HasPending => PendingAttachments.Count > 0;

    public string ModelName => SelectedModel?.Title ?? L.Get("Model_NotSelected");

    /// <summary>Подсказка в поле ввода — по выбранной клавише отправки.</summary>
    public string InputHint => L.Get(_s.Settings.Current.Window.SendWithCtrlEnter ? "Chat_InputPlaceholderCtrl" : "Chat_InputPlaceholder");

    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(InputHint));
        SelectedChat ??= _lastChat ?? Chats.FirstOrDefault();
        RefreshTokens();
    }

    partial void OnSearchTextChanged(string value) => _filtered.Refresh();
    private bool _persistInspector = true;

    partial void OnIsInspectorVisibleChanged(bool value)
    {
        if (_persistInspector) _s.Settings.Current.Window.InspectorVisible = value;
    }

    /// <summary>Показать/скрыть правую панель без записи в настройки (автоскрытие на узком окне).</summary>
    public void SetInspectorVisible(bool visible, bool persist)
    {
        _persistInspector = persist;
        IsInspectorVisible = visible;
        _persistInspector = true;
    }

    // ---------------- модели ----------------

    /// <summary>Локальные модели + модели подключённых API.</summary>
    public void RefreshModels()
    {
        var current = SelectedModel?.Ref ?? SelectedChat?.Session.ModelRef;
        ModelOptions.Clear();
        foreach (var m in _s.Models.Models.Where(m => m.Exists))
            ModelOptions.Add(new ModelOption("local:" + m.Id, m.DisplayName, L.Get("Chat_LocalModels"), m.HasVision));
        foreach (var p in _s.Providers.Items.Where(p => p.Enabled))
        {
            var models = p.PinnedModels.Count > 0 ? p.PinnedModels : p.Models.Take(40).ToList();
            foreach (var m in models) ModelOptions.Add(new ModelOption($"provider:{p.Id}:{m}", m, p.Name));
        }
        SelectedModel = ModelOptions.FirstOrDefault(o => o.Ref == current)
                        ?? ModelOptions.FirstOrDefault(o => o.Ref == "local:" + _s.ActiveModel?.Id)
                        ?? ModelOptions.FirstOrDefault();
    }

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        OnPropertyChanged(nameof(ModelName));
        Endpoint.SetTarget(value?.Ref, value?.Title);
        RefreshTokens();
        if (value == null || SelectedChat == null) return;
        SelectedChat.Session.ModelRef = value.Ref;
        if (value.Ref.StartsWith("local:", StringComparison.Ordinal))
        {
            _s.Settings.Current.Models.ActiveModelId = value.Ref.Substring(6);
            _s.Settings.Save();
            Server.OnActiveModelChanged();
        }
    }

    // ---------------- чаты ----------------

    partial void OnSelectedChatChanged(ChatListItem? value)
    {
        FlushPromptSave();
        CancelEdit();
        if (value == null)
        {
            Messages.Clear();
            OnPropertyChanged(nameof(HasMessages));
            return;
        }
        _lastChat = value;
        var s = value.Session;
        SystemPrompt = s.SystemPrompt ?? string.Empty;
        Thinking = ThinkingOptions.FirstOrDefault(t => t.Value == s.EffectiveReasoning) ?? ThinkingOptions[0];
        ReasoningStyle = ReasoningStyles.FirstOrDefault(r => r.Value == (s.ReasoningStyle ?? Core.Chat.ReasoningStyle.Auto)) ?? ReasoningStyles[0];
        ReasoningHint = null;
        OverrideTemperature = s.Temperature.HasValue;
        ToolsEnabled = s.Tools ?? _s.Settings.Current.Tools.EnabledByDefault;
        Temperature = s.Temperature ?? 0.7;
        if (s.ModelRef != null && ModelOptions.FirstOrDefault(o => o.Ref == s.ModelRef) is { } opt) SelectedModel = opt;
        else if (SelectedModel != null) s.ModelRef = SelectedModel.Ref;
        RebuildMessages();
        // Строка под полем ввода — итог последнего ответа открытого чата, а не прочерки
        if (!IsGenerating) LiveStats = Messages.LastOrDefault(m => m.StatsText != null)?.StatsText ?? L.Get("Chat_StatsEmpty");
        RefreshTokens();
    }

    partial void OnSystemPromptChanged(string value)
    {
        SyncPreset();
        if (SelectedChat == null) return;
        // Значение — в сессию сразу (отправка сразу после правки должна уйти с новым промптом),
        // запись файла — с задержкой, чтобы не сохранять чат на каждую букву
        SelectedChat.Session.SystemPrompt = string.IsNullOrWhiteSpace(value) ? null : value;
        QueuePromptSave(SelectedChat.Session);
    }

    private System.Windows.Threading.DispatcherTimer? _promptSaveTimer;
    private ChatSession? _promptSaveSession;

    private void QueuePromptSave(ChatSession session)
    {
        if (_promptSaveSession != null && !ReferenceEquals(_promptSaveSession, session)) FlushPromptSave();
        _promptSaveSession = session;
        if (_promptSaveTimer == null)
        {
            _promptSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _promptSaveTimer.Tick += (_, _) => FlushPromptSave();
        }
        _promptSaveTimer.Stop();
        _promptSaveTimer.Start();
    }

    private void FlushPromptSave()
    {
        _promptSaveTimer?.Stop();
        if (_promptSaveSession is { } s) _s.Chats.Save(s);
        _promptSaveSession = null;
    }

    partial void OnThinkingChanged(ThinkingOption? value)
    {
        if (SelectedChat == null || value == null) return;
        SelectedChat.Session.Reasoning = value.Value;
        SelectedChat.Session.Thinking = null;
        SaveChat();
    }

    partial void OnToolsEnabledChanged(bool value)
    {
        if (SelectedChat == null) return;
        SelectedChat.Session.Tools = value;
        SaveChat();
    }

    partial void OnTemperatureChanged(double value) => ApplyTemperature();
    partial void OnOverrideTemperatureChanged(bool value) => ApplyTemperature();

    private void ApplyTemperature()
    {
        if (SelectedChat == null) return;
        SelectedChat.Session.Temperature = OverrideTemperature ? Temperature : null;
        SaveChat();
    }

    /// <summary>Сообщения, которые сейчас генерируются (по id): переживают пересборку ленты.</summary>
    private readonly Dictionary<string, MessageViewModel> _live = new();

    private void RebuildMessages()
    {
        Messages.Clear();
        var s = SelectedChat?.Session;
        if (s != null)
        {
            foreach (var m in s.ActivePath().Where(m => m.Role is ChatRole.User or ChatRole.Assistant))
            {
                var siblings = s.Siblings(m);
                // Ответ, который ещё генерируется, — тот же объект, что обновляет поток: иначе после возврата
                // из настроек или другого чата пузырь оставался пустым до конца генерации
                Messages.Add(_live.TryGetValue(m.Id, out var live) ? live : new MessageViewModel(m, Math.Max(0, siblings.ToList().IndexOf(m)), siblings.Count));
            }
        }
        OnPropertyChanged(nameof(HasMessages));
    }

    private void SaveChat()
    {
        if (SelectedChat != null) _s.Chats.Save(SelectedChat.Session);
    }

    [RelayCommand]
    private void NewChat()
    {
        // Пустой чат не плодим: если текущий ещё пуст — просто остаёмся в нём
        if (SelectedChat != null && SelectedChat.Session.Messages.Count == 0)
        {
            _filtered.Refresh();
            return;
        }
        // Чат не выбран (первый запуск, чат удалён), а в панели уже введён системный промпт — его и берём:
        // раньше он заменялся пресетом по умолчанию и до модели не доходил
        var typed = SelectedChat == null && !string.IsNullOrWhiteSpace(SystemPrompt) ? SystemPrompt : null;
        var session = new ChatSession
        {
            Title = L.Get("Chat_NewTitle"),
            ModelRef = SelectedModel?.Ref,
            SystemPrompt = typed ?? _s.Prompts.Find(_s.Settings.Current.DefaultPromptPresetId)?.Text,
        };
        var item = new ChatListItem(session);
        Chats.Insert(0, item);
        SelectedChat = item;
    }

    /// <summary>Чат из импорта (настройки → «Данные»): уже сохранён, добавляем в список.</summary>
    public void AddImported(ChatSession chat)
    {
        Chats.Insert(0, new ChatListItem(chat));
        _filtered.Refresh();
    }

    /// <summary>Пресеты системного промпта для выпадающего списка в правой панели.</summary>
    public IReadOnlyList<PromptPreset> PromptPresets => _s.Prompts.Items.ToList();

    private bool _syncingPreset;

    /// <summary>Пресет, текст которого совпадает с системным промптом чата; выбор подставляет его текст.</summary>
    [ObservableProperty] private PromptPreset? _selectedPreset;

    partial void OnSelectedPresetChanged(PromptPreset? value)
    {
        if (_syncingPreset || value == null) return;
        SystemPrompt = value.Text;
    }

    private void SyncPreset()
    {
        _syncingPreset = true;
        SelectedPreset = _s.Prompts.Items.FirstOrDefault(p => string.Equals(p.Text.Trim(), SystemPrompt.Trim(), StringComparison.Ordinal) && SystemPrompt.Trim().Length > 0);
        _syncingPreset = false;
    }

    [RelayCommand]
    private void TogglePin(ChatListItem item)
    {
        item.Pinned = !item.Pinned;
        _s.Chats.Save(item.Session);
        _filtered.Refresh();
    }

    [RelayCommand]
    private void DeleteChat(ChatListItem item)
    {
        if (MessageBox.Show(L.F("Chat_ConfirmDelete", item.Title), "Offline Chatter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (ReferenceEquals(item, SelectedChat)) Stop();
        _s.Chats.Delete(item.Session);
        Chats.Remove(item);
        if (ReferenceEquals(SelectedChat, item) || SelectedChat == null) SelectedChat = Chats.FirstOrDefault();
        if (Chats.Count == 0) NewChat();
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    [RelayCommand]
    private void UseSuggestion(string text) => InputText = text;

    // ---------------- вложения ----------------

    [RelayCommand]
    private void Attach()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.Get("Chat_Attach"),
            Multiselect = true,
            Filter = L.Get("Chat_AttachFilter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.log;*.cs;*.py;*.js;*.ts;*.c;*.cpp;*.h;*.java;*.go;*.rs;*.sql;*.sh;*.ps1;*.html;*.css;*.docx|*.*|*.*",
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                if (AttachmentReader.IsImage(p)) PendingAttachments.Add(new AttachmentView(ImageAttachments.FromFile(p)));
                else if (AttachmentReader.ReadText(p) is { } t) PendingAttachments.Add(new AttachmentView(t));
                else Status = L.F("Chat_AttachUnsupported", Path.GetFileName(p));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Status = L.F("Chat_AttachFailed", Path.GetFileName(p), ex.Message);
            }
        }
        OnPropertyChanged(nameof(HasPending));
    }

    public void AddImage(BitmapSource bitmap)
    {
        PendingAttachments.Add(new AttachmentView(ImageAttachments.FromBitmap(bitmap, "clipboard.png")));
        OnPropertyChanged(nameof(HasPending));
    }

    /// <summary>Открыть изображение во весь размер: масштаб, копирование, сохранение.</summary>
    [RelayCommand]
    private void PreviewAttachment(AttachmentView? a)
    {
        if (a != null) Views.ImagePreviewWindow.Show(a.Attachment);
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentView a)
    {
        PendingAttachments.Remove(a);
        OnPropertyChanged(nameof(HasPending));
    }

    // ---------------- отправка и генерация ----------------

    private bool CanSend() => !IsGenerating && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (SelectedChat == null) NewChat();
        var item = SelectedChat!;
        var chat = item.Session;
        var text = InputText.Trim();
        var user = new ChatMessage { Role = ChatRole.User, Content = text, Attachments = PendingAttachments.Select(a => a.Attachment).ToList() };

        if (Editing != null)
        {
            // Правка: новая версия сообщения — сосед исходного (ветвление)
            user.ParentId = Editing.Message.ParentId;
            chat.Messages.Add(user);
            chat.ActiveLeafId = user.Id;
            Editing = null;
        }
        else
        {
            chat.Append(user);
        }

        if (chat.Messages.Count(m => m.Role == ChatRole.User) == 1 && !string.IsNullOrEmpty(text))
            item.Title = text.Length > 48 ? text.Substring(0, 48).TrimEnd() + "…" : text;
        InputText = string.Empty;
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPending));
        SaveChat();
        RebuildMessages();
        OnPropertyChanged(nameof(Title));
        await GenerateAsync(item, user);
    }

    private bool CanStop() => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _genCts?.Cancel();

    /// <summary>
    /// Ответ на <paramref name="parent"/>. Агентный цикл: если модель вызывает инструменты — выполняем их
    /// (с подтверждением), добавляем результаты и продолжаем, пока модель не ответит текстом или не кончатся шаги.
    /// </summary>
    private async Task GenerateAsync(ChatListItem item, ChatMessage parent)
    {
        var chat = item.Session;
        var cts = _genCts = new CancellationTokenSource();
        IsGenerating = true;
        Endpoint.Busy = true;
        Status = null;
        try
        {
            var (client, model, error) = await _s.ResolveChatClientAsync(chat.ModelRef ?? SelectedModel?.Ref, s => Status = s, cts.Token);
            if (client == null)
            {
                var failed = new ChatMessage { Role = ChatRole.Assistant, ParentId = parent.Id, Error = error };
                chat.Messages.Add(failed);
                chat.ActiveLeafId = failed.Id;
                RebuildMessages();
                return;
            }
            Status = null;

            var useTools = (chat.Tools ?? _s.Settings.Current.Tools.EnabledByDefault) && _s.Tools.All().Count > 0;
            var maxSteps = Math.Max(1, _s.Settings.Current.Tools.MaxSteps);
            var current = parent;
            for (var step = 0; step < maxSteps && !cts.IsCancellationRequested; step++)
            {
                var answer = await StreamStepAsync(chat, current, client, model, useTools, cts.Token);
                if (answer.ToolCalls.Count == 0 || answer.Error != null || cts.IsCancellationRequested) break;

                // Выполнить вызовы инструментов и записать результаты сообщениями роли tool
                current = answer;
                foreach (var call in answer.ToolCalls)
                {
                    var (text, isError) = await RunToolAsync(call, cts.Token);
                    call.Result = text;
                    call.IsError = isError;
                    current = new ChatMessage { Role = ChatRole.Tool, ParentId = current.Id, ToolCallId = call.Id, Content = text };
                    chat.Messages.Add(current);
                }
                chat.ActiveLeafId = current.Id;
                _s.Chats.Save(chat);
                Messages.LastOrDefault(m => m.Message.Id == answer.Id)?.RefreshTools();
                if (step == maxSteps - 1) Status = L.Get("Tools_MaxSteps");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsGenerating = false;
            Endpoint.Busy = false;
            if (ReferenceEquals(_genCts, cts)) _genCts = null;
            cts.Dispose();
            chat.UpdatedAt = DateTime.UtcNow;
            _s.Chats.Save(chat);
        }
    }

    /// <summary>Один запрос к модели со стримингом в новое сообщение ассистента.</summary>
    private async Task<ChatMessage> StreamStepAsync(ChatSession chat, ChatMessage parent, ChatClient client, string? model, bool useTools, CancellationToken ct)
    {
        var answer = new ChatMessage { Role = ChatRole.Assistant, ParentId = parent.Id };
        chat.Messages.Add(answer);
        chat.ActiveLeafId = answer.Id;
        RebuildMessages();
        // Объект ответа существует, даже если чат сейчас не открыт: при возврате лента возьмёт именно его
        var vm = Messages.LastOrDefault(m => m.Message.Id == answer.Id) ?? new MessageViewModel(answer, 0, 1);
        _live[answer.Id] = vm;
        vm.IsStreaming = true;
        vm.IsPreparing = true;
        var modelRef = chat.ModelRef ?? SelectedModel?.Ref;
        vm.PreparingText = modelRef?.StartsWith("local:", StringComparison.Ordinal) == true
            ? L.Get("Chat_Prefilling")
            : L.Get("Chat_WaitingToken");

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var gate = new object();
        var deltas = 0;
        var started = DateTime.UtcNow;
        DateTime? firstToken = null;
        // Ответ и статистика копятся здесь: числа токенов видны во время генерации и не пропадают при остановке или ошибке
        var live = new ChatResult();
        var tokensShownAt = DateTime.MinValue;
        var flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        void Flush()
        {
            string c, r;
            int deltaCount;
            DateTime? first;
            lock (gate)
            {
                c = content.ToString();
                r = reasoning.ToString();
                deltaCount = deltas;
                first = firstToken;
            }
            (r, c) = AnswerRecovery.Separate(r, c);
            vm.Reasoning = r;
            vm.Content = c;
            if (first is { } ft)
            {
                vm.IsPreparing = false;
                var sec = (DateTime.UtcNow - ft).TotalSeconds;
                // llama-server сообщает точное число токенов и скорость в каждом чанке; у остальных — число порций потока
                var exact = live.Stats.CompletionTokens > 0;
                var tokens = exact ? live.Stats.CompletionTokens : deltaCount;
                var speed = live.Stats.TokensPerSecond is { } tps ? tps.ToString("0.0") : sec > 0.2 ? (deltaCount / sec).ToString("0.0") : "…";
                LiveStats = L.F("Chat_LiveStats", (exact ? string.Empty : "≈") + tokens.ToString("N0"), speed, Units.FormatDuration(ft - started));
                if ((DateTime.UtcNow - tokensShownAt).TotalMilliseconds >= 250 && ReferenceEquals(SelectedChat?.Session, chat))
                {
                    tokensShownAt = DateTime.UtcNow;
                    RefreshTokens(live.Stats, exact ? null : deltaCount);
                }
            }
        }
        flush.Tick += (_, _) => Flush();
        flush.Start();

        try
        {
            var profile = chat.ModelRef?.StartsWith("local:", StringComparison.Ordinal) != false && _s.ActiveModel != null ? _s.ProfileFor(_s.ActiveModel) : null;
            var sampling = profile?.Sampling ?? new Core.Engine.SamplingSettings();
            var request = new ChatRequest
            {
                Model = model,
                Temperature = chat.Temperature ?? sampling.Temperature,
                TopP = sampling.TopP,
                TopK = sampling.TopK,
                MinP = sampling.MinP,
                RepeatPenalty = sampling.RepeatPenalty,
                MaxTokens = chat.MaxTokens ?? (sampling.MaxTokens > 0 ? sampling.MaxTokens : null),
                Seed = sampling.Seed,
                Reasoning = chat.EffectiveReasoning,
                ReasoningStyle = chat.ReasoningStyle ?? Core.Chat.ReasoningStyle.Auto,
                Tools = useTools ? _s.Tools.ToOpenAiTools() : null,
            };
            if (chat.ModelRef?.StartsWith("provider:", StringComparison.Ordinal) != true)
                request.ChatTemplate = await _s.GetLocalChatTemplateAsync(ct);
            if (!string.IsNullOrWhiteSpace(chat.SystemPrompt))
                request.Messages.Add(new ChatMessage { Role = ChatRole.System, Content = chat.SystemPrompt! });
            request.Messages.AddRange(chat.ActivePath().Where(m => m.Id != answer.Id && m.Error == null &&
                                                                   (m.Role != ChatRole.Assistant || m.Content.Length > 0 || m.ToolCalls.Count > 0)));

            if (_s.Settings.Current.Logging.LogMessageContent)
                Log.Info("Chat", "Request", new { chat = chat.Id, messages = request.Messages.Select(m => new { m.Role, m.Content }) });

            var result = await client.StreamAsync(request, d =>
            {
                lock (gate)
                {
                    if (d.Reasoning != null) reasoning.Append(d.Reasoning);
                    if (d.Content != null) content.Append(d.Content);
                    deltas++;
                    firstToken ??= DateTime.UtcNow;
                }
            }, ct, live);
            // Подсказка в правой панели: как программа поняла модель при «Авто»
            ReasoningHint = (chat.ReasoningStyle ?? Core.Chat.ReasoningStyle.Auto) == Core.Chat.ReasoningStyle.Auto
                ? L.F("Chat_ReasoningDetected", L.Get("Enum_ReasoningStyle_" + client.AppliedReasoningStyle))
                : null;

            // Ответ мог целиком уйти в рассуждения (шаблон сам открыл <think>, а модель после инструмента ответила сразу)
            // или вызов инструмента остался текстом — приводим к виду «текст + рассуждения + вызовы»
            var toolNames = request.Tools?.Select(t => (string?)t["function"]?["name"]).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            var fixedUp = AnswerRecovery.Finish(result.Reasoning, result.Content, result.ToolCalls, result.Stats.FinishReason, toolNames);
            answer.Content = fixedUp.Content;
            answer.Reasoning = fixedUp.Reasoning.Length > 0 ? fixedUp.Reasoning : null;
            answer.ToolCalls = fixedUp.ToolCalls;
            answer.Stats = result.Stats;
            if (fixedUp.Promoted) answer.Stats.ReasoningMs = null;
            Endpoint.ReportRequest(modelRef, null, result.Stats.TimeToFirstTokenMs);
            Log.Info("Chat", "Generated", new
            {
                provider = result.Stats.Provider, model = result.Stats.Model, result.Stats.PromptTokens, result.Stats.CompletionTokens,
                cached = result.Stats.CachedTokens, estimated = result.Stats.Estimated,
                tps = result.Stats.TokensPerSecond, ttftMs = (int)result.Stats.TimeToFirstTokenMs, totalMs = (int)result.Stats.TotalMs,
                tools = answer.ToolCalls.Select(t => t.Name), finish = result.Stats.FinishReason,
                answerFromReasoning = fixedUp.Promoted, toolCallsFromText = fixedUp.ToolCallsFromText,
            });
        }
        catch (OperationCanceledException)
        {
            // Остановлено: оставляем всё, что успело прийти, вместе со статистикой
            var (r, c) = AnswerRecovery.Separate(live.Reasoning, live.Content);
            answer.Content = c;
            answer.Reasoning = r.Length > 0 ? r : null;
            if (firstToken != null)
            {
                answer.Stats = live.Stats;
                answer.Stats.FinishReason = "cancelled";
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            var message = ex is HttpStatusException hs ? $"HTTP {(int)hs.Status}: {hs.Body}" : ex.GetBaseException().Message;
            var (r, c) = AnswerRecovery.Separate(live.Reasoning, live.Content);
            answer.Content = c;
            answer.Reasoning = r.Length > 0 ? r : null;
            if (firstToken != null) answer.Stats = live.Stats;
            answer.Error = message;
            Endpoint.ReportRequest(modelRef, ex, null);
            Log.Error("Chat", "Generation failed", ex);
        }
        finally
        {
            flush.Stop();
            vm.Content = answer.Content;
            vm.Reasoning = answer.Reasoning ?? string.Empty;
            vm.Error = answer.Error;
            vm.IsPreparing = false;
            vm.IsStreaming = false;
            vm.UpdateStats();
            vm.RefreshTools();
            _live.Remove(answer.Id);
            _s.Chats.Save(chat);
            // Строка под полем ввода: итог этого ответа; после ошибки без единого токена — итог предыдущего, а не прочерки
            LiveStats = vm.StatsText
                        ?? Messages.LastOrDefault(m => m.StatsText != null)?.StatsText
                        ?? L.Get("Chat_StatsEmpty");
            if (ReferenceEquals(SelectedChat?.Session, chat)) RefreshTokens();
        }
        return answer;
    }

    /// <summary>Выполнить вызов инструмента с учётом согласия пользователя.</summary>
    private async Task<(string text, bool isError)> RunToolAsync(ToolCall call, CancellationToken ct)
    {
        var tool = _s.Tools.Find(call.Name);
        if (tool == null) return ($"Unknown tool: {call.Name}", true);
        var mode = _s.Tools.ConsentFor(tool);
        if (mode == Core.Tools.ConsentMode.Disabled) return ("Tool is disabled by the user", true);
        // Аргументы с учётом настроек (город по умолчанию): в запросе согласия видно то, что действительно уйдёт
        var arguments = _s.Tools.PrepareArguments(tool, call.Arguments);
        if (mode == Core.Tools.ConsentMode.Ask)
        {
            var answer = Views.ToolConsentDialog.Ask(tool.Name, tool.Source == "builtin" ? L.Get("Tools_Builtin") : "MCP: " + tool.Source,
                arguments, tool.Source != "builtin");
            if (answer == Views.ConsentAnswer.Deny) return ("The user denied this tool call", true);
            if (answer == Views.ConsentAnswer.Always)
            {
                _s.Settings.Current.Tools.Consent[tool.Id] = Core.Tools.ConsentMode.Always;
                _s.Settings.Save();
            }
        }
        Status = L.F("Tools_Running", tool.Name);
        try
        {
            var r = await _s.Tools.ExecuteAsync(tool, arguments, ct);
            Log.Info("Tools", "Called " + tool.Id, new { error = r.isError, length = r.text.Length });
            return r;
        }
        finally
        {
            Status = null;
        }
    }
    /// <summary>
    /// Токены беседы: заполнение контекста (строка под полем ввода и блок «Токены» правой панели), последний ответ,
    /// всего за чат. Считается по статистике сообщений, поэтому одинаково после генерации, остановки, ошибки и
    /// при возврате в чат; во время генерации — по живой статистике ответа.
    /// </summary>
    /// <param name="live">Статистика ответа, который сейчас генерируется.</param>
    /// <param name="approxCompletion">Число порций потока, пока сервер не сообщил точное число токенов.</param>
    private void RefreshTokens(GenerationStats? live = null, int? approxCompletion = null)
    {
        var session = SelectedChat?.Session;
        var last = live ?? session?.ActivePath().LastOrDefault(m => m.Stats != null)?.Stats;
        var local = SelectedModel?.Ref.StartsWith("local:", StringComparison.Ordinal) == true;
        var limit = local ? Server.ContextLimit : null;
        var rows = new List<InsightRowData>();

        if (last == null)
        {
            ContextRatio = 0;
            ContextText = null;
            rows.Add(new InsightRowData(L.Get("Tok_Context"), limit is > 0 ? "— / " + limit.Value.ToString("N0") : "—"));
        }
        else
        {
            var output = last.CompletionTokens > 0 ? last.CompletionTokens : approxCompletion ?? 0;
            var approx = last.Estimated || last.CompletionTokens <= 0 && output > 0 ? "≈" : string.Empty;
            var used = last.PromptTokens + output;
            var ratio = limit is > 0 ? Math.Min(1, used / (double)limit.Value) : 0;
            ContextRatio = ratio;
            ContextText = limit is > 0
                ? L.F("Chat_Context", approx + used.ToString("N0"), limit.Value.ToString("N0"))
                : L.F("Chat_ContextNoLimit", approx + used.ToString("N0"));

            rows.Add(new InsightRowData(L.Get("Tok_Context"),
                limit is > 0 ? $"{approx}{used:N0} / {limit.Value:N0} · {ratio * 100:0} %" : approx + used.ToString("N0"), true, ratio >= 0.9));
            rows.Add(new InsightRowData(L.Get("Tok_In"),
                last.PromptTokens > 0
                    ? approx + last.PromptTokens.ToString("N0") + (last.CachedTokens is > 0 ? " · " + L.F("Tok_Cached", last.CachedTokens.Value.ToString("N0")) : string.Empty)
                    : "—"));
            rows.Add(new InsightRowData(L.Get("Tok_Out"),
                output > 0
                    ? approx + output.ToString("N0") + (last.ReasoningTokens is > 0 ? " · " + L.F("Tok_Reasoning", last.ReasoningTokens.Value.ToString("N0")) : string.Empty)
                    : "—"));
            if (last.TokensPerSecond is { } tps)
                rows.Add(new InsightRowData(L.Get("Tok_Speed"), L.F("Stats_Gen", approx + tps.ToString("0.0"))));
        }

        if (session != null)
        {
            long totalIn = 0, totalOut = 0;
            var answers = 0;
            var estimated = false;
            foreach (var m in session.Messages)
            {
                if (m.Stats is not { } s || ReferenceEquals(s, live)) continue;
                totalIn += s.PromptTokens;
                totalOut += s.CompletionTokens;
                estimated |= s.Estimated;
                answers++;
            }
            if (live != null)
            {
                totalIn += live.PromptTokens;
                totalOut += live.CompletionTokens > 0 ? live.CompletionTokens : approxCompletion ?? 0;
                estimated |= live.Estimated || live.CompletionTokens <= 0;
                answers++;
            }
            if (answers > 0)
                rows.Add(new InsightRowData(L.Get("Tok_Chat"), L.F("Tok_ChatValue", (estimated ? "≈" : string.Empty) + totalIn.ToString("N0"), totalOut.ToString("N0"), answers)));
        }
        TokensGroup.Set(rows);
    }

    // ---------------- экспорт и печать ----------------

    [RelayCommand]
    private void ExportChat()
    {
        if (SelectedChat == null) return;
        var path = ChatExportService.SaveWithDialog(ExportDocument.FromSession(SelectedChat.Session), SelectedChat.Session);
        if (path != null) Status = L.F("Bench_Exported", path);
    }

    [RelayCommand]
    private void ExportMessage(MessageViewModel m)
    {
        if (SelectedChat == null) return;
        var ids = new System.Collections.Generic.HashSet<string> { m.Message.Id };
        // Для ответа экспортируем и вопрос, на который он отвечает — так документ понятнее
        if (m.IsAssistant && m.Message.ParentId != null) ids.Add(m.Message.ParentId);
        var doc = ExportDocument.FromSession(SelectedChat.Session, ids);
        var path = ChatExportService.SaveWithDialog(doc, null);
        if (path != null) Status = L.F("Bench_Exported", path);
    }

    [RelayCommand]
    private void Print()
    {
        if (SelectedChat != null) ChatExportService.Print(ExportDocument.FromSession(SelectedChat.Session));
    }

    // ---------------- действия с сообщениями ----------------

    [RelayCommand]
    private void Speak(MessageViewModel m) => Shell.Speech.Toggle(m.Content);

    [RelayCommand]
    private void Copy(MessageViewModel m)
    {
        try { Clipboard.SetText(m.Content); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private async Task RegenerateAsync(MessageViewModel m)
    {
        if (IsGenerating || SelectedChat == null) return;
        var parent = SelectedChat.Session.Find(m.IsUser ? m.Message.Id : m.Message.ParentId);
        if (parent == null) return;
        await GenerateAsync(SelectedChat, parent);
    }

    [RelayCommand]
    private void Edit(MessageViewModel m)
    {
        if (!m.IsUser || IsGenerating) return;
        Editing = m;
        InputText = m.Content;
        PendingAttachments.Clear();
        foreach (var a in m.Message.Attachments) PendingAttachments.Add(new AttachmentView(a));
        OnPropertyChanged(nameof(HasPending));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        if (Editing == null) return;
        Editing = null;
        InputText = string.Empty;
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPending));
    }

    [RelayCommand]
    private void Delete(MessageViewModel m)
    {
        if (IsGenerating || SelectedChat == null) return;
        SelectedChat.Session.Remove(m.Message);
        SaveChat();
        RebuildMessages();
    }

    [RelayCommand]
    private void PrevVariant(MessageViewModel m) => SwitchVariant(m, -1);

    [RelayCommand]
    private void NextVariant(MessageViewModel m) => SwitchVariant(m, +1);

    private void SwitchVariant(MessageViewModel m, int step)
    {
        if (IsGenerating || SelectedChat == null) return;
        var s = SelectedChat.Session;
        var siblings = s.Siblings(m.Message);
        var idx = siblings.ToList().IndexOf(m.Message) + step;
        if (idx < 0 || idx >= siblings.Count) return;
        s.ActiveLeafId = s.DeepestLeaf(siblings[idx]).Id;
        SaveChat();
        RebuildMessages();
    }
}
