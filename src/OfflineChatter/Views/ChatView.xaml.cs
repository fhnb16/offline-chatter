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
        SizeChanged += OnSizeChanged;
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
        if (e.Key == Key.Enter)
        {
            // Отправка: Enter (Shift+Enter — перенос) или Ctrl+Enter (Enter — перенос), по настройке
            var ctrlMode = App.Settings.Current.Window.SendWithCtrlEnter;
            var mods = Keyboard.Modifiers;
            var send = ctrlMode ? mods == ModifierKeys.Control : mods == ModifierKeys.None;
            if (send)
            {
                if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
                e.Handled = true;
            }
            else if (ctrlMode && mods == ModifierKeys.None || !ctrlMode && mods == ModifierKeys.Shift)
            {
                // AcceptsReturn сам вставит перевод строки только для «чистого» Enter — делаем это явно
                var box = InputBox;
                var caret = box.CaretIndex;
                box.SelectedText = Environment.NewLine;
                box.CaretIndex = caret + Environment.NewLine.Length;
                box.SelectionLength = 0;
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    _vm.AddFiles(Clipboard.GetFileDropList().Cast<string>());
                    e.Handled = true;
                }
                // Текст из Word/Excel приходит вместе с картинкой-превью — тогда вставляем текст, как обычно
                else if (!Clipboard.ContainsText() && Rendering.ImageAttachments.GetClipboardImage() is { } img)
                {
                    _vm.AddImage(img);
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

    // Правая панель на узком окне занимала половину ленты: прячем её автоматически
    // и возвращаем, когда места снова хватает (если пользователь её не закрыл сам).
    private const double InspectorBreakpoint = 880;
    private bool _autoHidden;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm == null || !e.WidthChanged) return;
        var narrow = e.NewSize.Width < InspectorBreakpoint;
        // Только при переходе через границу: если на узком окне панель открыли вручную — не закрываем её снова
        var crossedToNarrow = narrow && (e.PreviousSize.Width >= InspectorBreakpoint || e.PreviousSize.Width == 0);
        if (crossedToNarrow && _vm.IsInspectorVisible)
        {
            _autoHidden = true;
            _vm.SetInspectorVisible(false, persist: false);
        }
        else if (!narrow && _autoHidden)
        {
            _autoHidden = false;
            _vm.SetInspectorVisible(true, persist: false);
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
