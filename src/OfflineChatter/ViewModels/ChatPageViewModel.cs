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
    [NotifyPropertyChangedFor(nameof(IsThinking))]
    private string _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ReasoningHeader), nameof(IsThinking))]
    private string _reasoning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThinking), nameof(ReasoningHeader))]
    private bool _isStreaming;

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
        if (s.TokensPerSecond is { } tps) parts.Add(L.F("Stats_Gen", tps.ToString("0.0")));
        if (s.CompletionTokens > 0) parts.Add(L.F("Stats_Tokens", s.CompletionTokens));
        if (s.PromptTokensPerSecond is { } pps) parts.Add(L.F("Stats_Prompt", s.PromptTokens, pps.ToString("0")));
        parts.Add(L.F("Stats_Ttft", Units.FormatDuration(TimeSpan.FromMilliseconds(s.TimeToFirstTokenMs))));
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

public sealed record ThinkingOption(bool? Value, string Title);

/// <summary>Страница чата: лента сообщений, ввод, генерация, ветвление, вложения, параметры беседы.</summary>
public sealed partial class ChatPageViewModel : PageViewModel
{
    private readonly AppServices _s;
    private ChatListItem? _lastChat;
    private CancellationTokenSource? _genCts;
    private readonly ICollectionView _filtered;

    public ChatPageViewModel(AppServices services)
    {
        _s = services;
        _isInspectorVisible = services.Settings.Current.Window.InspectorVisible;
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
        _s.Engine.Server.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(EngineStatus))));

        if (Chats.Count == 0) NewChat();
        else SelectedChat = Chats[0];

        // Нагрузка llama-server (CPU, ОЗУ, видеопамять) — раз в секунду, пока открыта правая панель
        _resourceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _resourceTimer.Tick += (_, _) => SampleResources();
        _resourceTimer.Start();
    }

    private readonly DispatcherTimer _resourceTimer;
    private readonly ResourceMonitor _monitor = new();

    [ObservableProperty] private string? _resourceText;

    private void SampleResources()
    {
        if (!IsInspectorVisible) return;
        var srv = _s.Engine.Server;
        if (srv.State is Core.Engine.ServerState.Stopped or Core.Engine.ServerState.Failed)
        {
            ResourceText = L.Get("Res_ServerStopped");
            return;
        }
        var r = _monitor.Sample(srv.Pid);
        var parts = new List<string>();
        if (r.CpuPercent is { } cpu) parts.Add(L.F("Res_Cpu", cpu.ToString("0")));
        if (r.WorkingSet is { } ws) parts.Add(L.F("Res_Ram", Units.FormatBytes(ws)));
        if (r.VramUsed is { } vu && r.VramTotal is { } vt) parts.Add(L.F("Res_Vram", Units.FormatBytes(vu), Units.FormatBytes(vt)));
        ResourceText = string.Join(Environment.NewLine, parts);
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
        new ThinkingOption(null, L.Get("Chat_ThinkingAuto")),
        new ThinkingOption(true, L.Get("Chat_ThinkingOn")),
        new ThinkingOption(false, L.Get("Chat_ThinkingOff")),
    };

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

    public string EngineStatus => L.Get("Enum_ServerState_" + _s.Engine.Server.State);
    public string ModelName => SelectedModel?.Title ?? L.Get("Model_NotSelected");

    /// <summary>Подсказка в поле ввода — по выбранной клавише отправки.</summary>
    public string InputHint => L.Get(_s.Settings.Current.Window.SendWithCtrlEnter ? "Chat_InputPlaceholderCtrl" : "Chat_InputPlaceholder");

    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(InputHint));
        SelectedChat ??= _lastChat ?? Chats.FirstOrDefault();
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
        if (value == null || SelectedChat == null) return;
        SelectedChat.Session.ModelRef = value.Ref;
        if (value.Ref.StartsWith("local:", StringComparison.Ordinal))
        {
            _s.Settings.Current.Models.ActiveModelId = value.Ref.Substring(6);
            _s.Settings.Save();
        }
    }

    // ---------------- чаты ----------------

    partial void OnSelectedChatChanged(ChatListItem? value)
    {
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
        Thinking = ThinkingOptions.FirstOrDefault(t => t.Value == s.Thinking) ?? ThinkingOptions[0];
        OverrideTemperature = s.Temperature.HasValue;
        ToolsEnabled = s.Tools ?? _s.Settings.Current.Tools.EnabledByDefault;
        Temperature = s.Temperature ?? 0.7;
        if (s.ModelRef != null && ModelOptions.FirstOrDefault(o => o.Ref == s.ModelRef) is { } opt) SelectedModel = opt;
        else if (SelectedModel != null) s.ModelRef = SelectedModel.Ref;
        RebuildMessages();
        UpdateContext(s.ActivePath().LastOrDefault(m => m.Stats != null)?.Stats);
    }

    partial void OnSystemPromptChanged(string value)
    {
        SyncPreset();
        if (SelectedChat == null) return;
        SelectedChat.Session.SystemPrompt = string.IsNullOrWhiteSpace(value) ? null : value;
        SaveChat();
    }

    partial void OnThinkingChanged(ThinkingOption? value)
    {
        if (SelectedChat == null || value == null) return;
        SelectedChat.Session.Thinking = value.Value;
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

    private void RebuildMessages()
    {
        Messages.Clear();
        var s = SelectedChat?.Session;
        if (s != null)
        {
            foreach (var m in s.ActivePath().Where(m => m.Role is ChatRole.User or ChatRole.Assistant))
            {
                var siblings = s.Siblings(m);
                Messages.Add(new MessageViewModel(m, Math.Max(0, siblings.ToList().IndexOf(m)), siblings.Count));
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
        var session = new ChatSession
        {
            Title = L.Get("Chat_NewTitle"),
            ModelRef = SelectedModel?.Ref,
            SystemPrompt = _s.Prompts.Find(_s.Settings.Current.DefaultPromptPresetId)?.Text,
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
        var vm = Messages.LastOrDefault(m => m.Message.Id == answer.Id);
        if (vm != null) vm.IsStreaming = true;

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var gate = new object();
        var deltas = 0;
        var started = DateTime.UtcNow;
        DateTime? firstToken = null;
        var flush = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        void Flush()
        {
            string c, r;
            lock (gate)
            {
                c = content.ToString();
                r = reasoning.ToString();
            }
            if (r.Length == 0 && c.StartsWith("<think>", StringComparison.Ordinal))
            {
                var split = ThinkSplitter.Split(c);
                r = split.reasoning;
                c = split.content;
            }
            if (vm != null)
            {
                vm.Reasoning = r;
                vm.Content = c;
            }
            if (firstToken is { } ft)
            {
                var sec = (DateTime.UtcNow - ft).TotalSeconds;
                LiveStats = L.F("Chat_LiveStats", deltas, sec > 0.2 ? (deltas / sec).ToString("0.0") : "…", Units.FormatDuration(ft - started));
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
                Thinking = chat.Thinking,
                Tools = useTools ? _s.Tools.ToOpenAiTools() : null,
            };
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
            }, ct);

            var (r, c) = (result.Reasoning, result.Content);
            if (r.Length == 0 && c.StartsWith("<think>", StringComparison.Ordinal))
            {
                var split = ThinkSplitter.Split(c);
                r = split.reasoning;
                c = split.content;
            }
            answer.Content = c;
            answer.Reasoning = r.Length > 0 ? r : null;
            answer.ToolCalls = result.ToolCalls;
            answer.Stats = result.Stats;
            Log.Info("Chat", "Generated", new
            {
                provider = result.Stats.Provider, model = result.Stats.Model, result.Stats.PromptTokens, result.Stats.CompletionTokens,
                tps = result.Stats.TokensPerSecond, ttftMs = (int)result.Stats.TimeToFirstTokenMs, totalMs = (int)result.Stats.TotalMs,
                tools = result.ToolCalls.Select(t => t.Name),
            });
            UpdateContext(result.Stats);
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                answer.Content = content.ToString();
                answer.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
            }
            answer.Stats ??= new GenerationStats { CompletionTokens = deltas };
            answer.Stats.FinishReason = "cancelled";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            var message = ex is HttpStatusException hs ? $"HTTP {(int)hs.Status}: {hs.Body}" : ex.GetBaseException().Message;
            lock (gate) answer.Content = content.ToString();
            answer.Error = message;
            Log.Error("Chat", "Generation failed", ex);
        }
        finally
        {
            flush.Stop();
            Flush();
            if (vm != null)
            {
                vm.Content = answer.Content;
                vm.Reasoning = answer.Reasoning ?? string.Empty;
                vm.Error = answer.Error;
                vm.IsStreaming = false;
                vm.UpdateStats();
                vm.RefreshTools();
            }
            _s.Chats.Save(chat);
            LiveStats = vm?.StatsText ?? L.Get("Chat_StatsEmpty");
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
        if (mode == Core.Tools.ConsentMode.Ask)
        {
            var answer = Views.ToolConsentDialog.Ask(tool.Name, tool.Source == "builtin" ? L.Get("Tools_Builtin") : "MCP: " + tool.Source,
                call.Arguments, tool.Source != "builtin");
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
            var r = await _s.Tools.ExecuteAsync(tool, call.Arguments, ct);
            Log.Info("Tools", "Called " + tool.Id, new { error = r.isError, length = r.text.Length });
            return r;
        }
        finally
        {
            Status = null;
        }
    }
    private void UpdateContext(GenerationStats? stats)
    {
        var ctx = _s.Engine.Server.State == Core.Engine.ServerState.Ready && SelectedModel?.Ref.StartsWith("local:", StringComparison.Ordinal) == true
            ? _s.Engine.Server.Options?.ContextSize
            : null;
        if (stats == null)
        {
            ContextRatio = 0;
            ContextText = null;
            return;
        }
        var used = stats.PromptTokens + stats.CompletionTokens;
        ContextRatio = ctx is > 0 ? Math.Min(1, used / (double)ctx.Value) : 0;
        ContextText = ctx is > 0 ? L.F("Chat_Context", used.ToString("N0"), ctx.Value.ToString("N0")) : L.F("Chat_ContextNoLimit", used.ToString("N0"));
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
