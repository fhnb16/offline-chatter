using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed partial class ChatPageViewModel
{
    private readonly Dictionary<ChatSession, CancellationTokenSource> _titleRequests = new();
    private TaskCompletionSource<bool>? _generationDone;
    private ChatSession? _generatingChat;
    private string? _generationStatus;
    private string? _generationStats;

    [ObservableProperty] private bool _hasSaveError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _preparingForShutdown;

    internal async Task<bool> PersistAsync(ChatSession chat)
    {
        var saved = await _s.Chats.SaveAsync(chat);
        if (!saved && Chats.Any(item => ReferenceEquals(item.Session, chat))) HasSaveError = true;
        return saved;
    }

    [RelayCommand]
    private async Task RetrySaveAsync()
    {
        await FlushChangesAsync();
    }

    internal async Task<bool> FlushChangesAsync()
    {
        FlushPromptSave();
        var saved = await _s.Chats.FlushAsync();
        HasSaveError = !saved;
        return saved;
    }

    internal async Task<bool> PrepareForShutdownAsync(bool cancelGeneration)
    {
        if (PreparingForShutdown) return false;
        PreparingForShutdown = true;
        foreach (var title in _titleRequests.Values.ToArray()) title.Cancel();
        if (cancelGeneration) Stop();
        if (_generationDone is { } generation) await generation.Task;
        if (await FlushChangesAsync()) return true;
        PreparingForShutdown = false;
        return false;
    }

    private void SetGenerationStatus(string? value)
    {
        _generationStatus = value;
        if (IsSelected(_generatingChat)) Status = value;
    }

    private bool IsSelected(ChatSession? chat) => chat != null && ReferenceEquals(SelectedChat?.Session, chat);

    private void RefreshSelectedGeneration()
    {
        var active = IsSelected(_generatingChat);
        Status = active ? _generationStatus : null;
        LiveStats = (active ? _generationStats : null)
            ?? Messages.LastOrDefault(m => m.StatsText != null)?.StatsText
            ?? L.Get("Chat_StatsEmpty");
    }
}
