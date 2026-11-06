using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public partial class ChatView : UserControl
{
    private ChatPageViewModel? _vm;
    private bool _stickToBottom = true;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => InputBox.Focus();
        MessagesScroll.ScrollChanged += (_, e) =>
        {
            // Пользователь прокрутил вверх — не дёргаем ленту; вернулся вниз — снова следуем за ответом
            if (e.ExtentHeightChange == 0) _stickToBottom = MessagesScroll.VerticalOffset >= MessagesScroll.ScrollableHeight - 30;
            else if (_stickToBottom) MessagesScroll.ScrollToEnd();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as ChatPageViewModel;
        if (_vm != null)
        {
            _vm.Messages.CollectionChanged += OnMessagesChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        ScrollToEndLater();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatPageViewModel.Editing) && _vm?.Editing != null)
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || e.Action == NotifyCollectionChangedAction.Add)
        {
            _stickToBottom = true;
            ScrollToEndLater();
        }
    }

    private void ScrollToEndLater() =>
        Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()), DispatcherPriority.Background);

    /// <summary>Ctrl+V с изображением или файлами — во вложения.</summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                {
                    _vm.AddImage(img);
                    e.Handled = true;
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    _vm.AddFiles(Clipboard.GetFileDropList().Cast<string>());
                    e.Handled = true;
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        else if (e.Key == Key.Up && string.IsNullOrEmpty(InputBox.Text))
        {
            // Стрелка вверх в пустом поле — редактировать последнее своё сообщение
            var last = _vm.Messages.LastOrDefault(m => m.IsUser);
            if (last != null)
            {
                _vm.EditCommand.Execute(last);
                e.Handled = true;
            }
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm != null && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _vm.AddFiles(files);
    }
}
