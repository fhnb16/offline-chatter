using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OfflineChatter.Core.Text;
using OfflineChatter.Theming;

namespace OfflineChatter.Rendering;

/// <summary>
/// Отображение Markdown в RichTextBox (выделение и копирование работают).
/// При стриминге перерисовывается только «хвост» после последней устоявшейся границы блока,
/// обновления батчатся (~30 раз в секунду); по завершении — один полный перерендер.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).Schedule()));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, e) =>
        {
            var view = (MarkdownView)d;
            if (!(bool)e.NewValue) view.RenderFull();
            else view.RestartIncremental();
        }));

    private readonly MarkdownRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly List<Block> _tail = new();
    private int _stableLength;
    private string _rendered = string.Empty;

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Cursor = Cursors.IBeam;
        FocusVisualStyle = null;
        Document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 14 };
        // Шрифт, размер и интервал сообщений — из настройки «Шрифты»; интервал работает как минимум высоты строки,
        // поэтому заголовки и формулы крупнее текста не обрезаются
        Document.SetResourceReference(FlowDocument.FontFamilyProperty, ThemeKeys.ChatFontFamily);
        Document.SetResourceReference(FlowDocument.LineHeightProperty, ThemeKeys.ChatLineHeight);
        SetResourceReference(TextSizeProperty, ThemeKeys.ChatFontSize);
        // Цвет текста — от самого контрола (наследуется документом), чтобы шаблон сообщения мог его переопределить
        SetResourceReference(ForegroundProperty, ThemeKeys.TextPrimary);
        SetResourceReference(SelectionBrushProperty, ThemeKeys.Selection);
        Template = CreateTemplate();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            RenderIncremental();
        };
        PreviewMouseWheel += ScrollForwarding.ForwardToParent;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(
        nameof(TextSize), typeof(double), typeof(MarkdownView), new PropertyMetadata(14.0, (d, e) => ((MarkdownView)d).OnTextSizeChanged((double)e.NewValue)));

    /// <summary>Размер текста; заголовки и встроенный код считаются от него, поэтому смена размера перерисовывает документ.</summary>
    public double TextSize
    {
        get => (double)GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    private void OnTextSizeChanged(double value)
    {
        if (double.IsNaN(value) || value <= 0) return;
        _renderer.FontSize = value;
        Document.FontSize = value;
        if (Document.Blocks.Count == 0) return;
        Document.Blocks.Clear();
        _tail.Clear();
        _stableLength = 0;
        _rendered = string.Empty;
        Schedule();
    }

    private static ControlTemplate CreateTemplate()
    {
        var t = new ControlTemplate(typeof(RichTextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(FocusableProperty, false);
        t.VisualTree = sv;
        return t;
    }

    private void Schedule()
    {
        if (!IsStreaming)
        {
            RenderFull();
            return;
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void RenderFull()
    {
        _timer.Stop();
        var text = Markdown ?? string.Empty;
        if (text == _rendered && _tail.Count == 0 && Document.Blocks.Count > 0) return;
        Document.Blocks.Clear();
        _tail.Clear();
        foreach (var b in _renderer.Render(text)) Document.Blocks.Add(b);
        _stableLength = text.Length;
        _rendered = text;
    }

    /// <summary>
    /// Генерация уже идёт, а представление создано заново (возврат в чат из настроек): привязка текста срабатывает
    /// раньше привязки флага, и первый кусок рисовался как «готовый». Начинаем инкрементальную отрисовку с нуля.
    /// </summary>
    private void RestartIncremental()
    {
        Document.Blocks.Clear();
        _tail.Clear();
        _stableLength = 0;
        _rendered = string.Empty;
        if (!string.IsNullOrEmpty(Markdown)) _timer.Start();
    }

    private void RenderIncremental()
    {
        var text = Markdown ?? string.Empty;
        if (text == _rendered) return;
        if (!text.StartsWith(_rendered.Substring(0, Math.Min(_stableLength, _rendered.Length)), StringComparison.Ordinal))
        {
            // Текст заменён целиком (редактирование, новый вариант) — рисуем заново
            Document.Blocks.Clear();
            _tail.Clear();
            _stableLength = 0;
        }

        var boundary = MarkdownText.StableBoundary(text);
        foreach (var b in _tail) Document.Blocks.Remove(b);
        _tail.Clear();

        if (boundary > _stableLength)
        {
            foreach (var b in _renderer.Render(text.Substring(_stableLength, boundary - _stableLength))) Document.Blocks.Add(b);
            _stableLength = boundary;
        }

        if (_stableLength < text.Length)
        {
            foreach (var b in _renderer.Render(text.Substring(_stableLength)))
            {
                Document.Blocks.Add(b);
                _tail.Add(b);
            }
        }
        _rendered = text;
    }
}

/// <summary>Вложенные ScrollViewer не должны «съедать» колесо мыши — передаём прокрутку родителю.</summary>
public static class ScrollForwarding
{
    public static void ForwardToParent(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers == ModifierKeys.Shift) return;
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender };
        var parent = VisualTreeHelper.GetParent((DependencyObject)sender) as UIElement;
        parent?.RaiseEvent(args);
    }
}
