using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Net;
using OfflineChatter.Localization;
using OfflineChatter.Theming;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

/// <summary>Быстрый вопрос: одно поле, ответ текущей моделью, перенос в полноценный чат.</summary>
public partial class QuickAskWindow : Window
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private string _question = string.Empty;
    private string _answer = string.Empty;

    public QuickAskWindow(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        InitializeComponent();
        ThemeManager.Attach(this);
        Loaded += (_, _) => Input.Focus();
        Closed += (_, _) => _cts?.Cancel();
        Status.Text = main.Chat.SelectedModel?.Title ?? L.Get("Model_NotSelected");
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnAsk(sender, e);
        }
    }

    private async void OnAsk(object sender, RoutedEventArgs e)
    {
        var q = Input.Text.Trim();
        if (q.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _question = q;
        _answer = string.Empty;
        Answer.IsStreaming = true;
        Answer.Markdown = string.Empty;
        OpenInChat.IsEnabled = false;
        var modelRef = _main.Chat.SelectedModel?.Ref;
        try
        {
            var (client, model, error) = await _s.ResolveChatClientAsync(modelRef, s => Status.Text = s, cts.Token);
            if (client == null)
            {
                Status.Text = error;
                return;
            }
            Status.Text = _main.Chat.SelectedModel?.Title;
            var sb = new StringBuilder();
            var req = new ChatRequest { Model = model, Thinking = false };
            req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = q });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                lock (sb) Answer.Markdown = sb.ToString();
                Scroll.ScrollToEnd();
            };
            timer.Start();
            try
            {
                var result = await client.StreamAsync(req, d => { if (d.Content != null) lock (sb) sb.Append(d.Content); }, cts.Token);
                _answer = result.Content;
                Status.Text = result.Stats.TokensPerSecond is { } tps ? $"{Status.Text} · {tps:0.0} tok/s" : Status.Text;
            }
            finally
            {
                timer.Stop();
            }
            Answer.Markdown = _answer;
            OpenInChat.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException)
        {
            Status.Text = ex is HttpStatusException hs ? $"HTTP {(int)hs.Status}: {hs.Body}" : ex.GetBaseException().Message;
        }
        finally
        {
            Answer.IsStreaming = false;
        }
    }

    /// <summary>Перенести вопрос и ответ в новый чат и открыть главное окно.</summary>
    private void OnOpenInChat(object sender, RoutedEventArgs e)
    {
        _main.NewChatCommand.Execute(null);
        var item = _main.Chat.SelectedChat;
        if (item != null)
        {
            var chat = item.Session;
            chat.Append(new ChatMessage { Role = ChatRole.User, Content = _question });
            chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = _answer });
            item.Title = _question.Length > 48 ? _question.Substring(0, 48) + "…" : _question;
            _s.Chats.Save(chat);
            _main.Chat.SelectedChat = null;
            _main.Chat.SelectedChat = item;
        }
        App.ShowMainWindow();
        Close();
    }

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
