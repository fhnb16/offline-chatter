using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Images;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.ViewModels;

public sealed partial class ImagesViewModel
{
    private Task? _promptHistoryLoad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPromptHistory))]
    private IReadOnlyList<ImagePromptEntry> _promptHistory = Array.Empty<ImagePromptEntry>();

    [ObservableProperty] private bool _isPromptHistoryOpen;

    public bool HasPromptHistory => PromptHistory.Count > 0;

    private Task EnsurePromptHistoryLoadedAsync() => _promptHistoryLoad ??= LoadPromptHistoryAsync();

    private async Task LoadPromptHistoryAsync()
    {
        var last = ImagePromptEntry.FromParams(_s.Settings.Current.Images.LastParams);
        var path = _s.Paths.ImagePromptHistoryFile;
        var entries = await Task.Run(() =>
        {
            try { return JsonFile.Read<List<ImagePromptEntry>>(path); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warn("Images", "Cannot read prompt history");
                return null;
            }
        }, CancellationToken.None);
        PromptHistory = ImagePromptHistory.Recent(entries ?? new[] { last }.AsEnumerable());
    }

    private async Task RememberPromptAsync(ImageGenParams parameters, CancellationToken ct)
    {
        await EnsurePromptHistoryLoadedAsync();
        ct.ThrowIfCancellationRequested();
        var entries = ImagePromptHistory.Recent(PromptHistory.Prepend(ImagePromptEntry.FromParams(parameters)));
        PromptHistory = entries;
        var path = _s.Paths.ImagePromptHistoryFile;
        try
        {
            await Task.Run(() => JsonFile.WriteAtomic(path, entries), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Images", "Cannot save prompt history");
        }
    }

    [RelayCommand]
    private void RestorePrompt(ImagePromptEntry? entry)
    {
        if (entry == null) return;
        Prompt = entry.Prompt;
        NegativePrompt = entry.NegativePrompt;
        IsPromptHistoryOpen = false;
    }
}
