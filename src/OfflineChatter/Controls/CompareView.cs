using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OfflineChatter.Theming;

namespace OfflineChatter.Controls;

/// <summary>
/// Сравнение «до / после»: обе картинки вписаны в одно место, левая часть показывает «до», правая — «после».
/// Граница двигается мышью и стрелками. Под картинками — шахматный фон: так видна прозрачность RGBA.
/// </summary>
public sealed class CompareView : Grid
{
    private readonly Image _after = new() { Stretch = Stretch.Uniform };
    private readonly Image _before = new() { Stretch = Stretch.Uniform };
    private readonly Border _line = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly RectangleGeometry _clip = new();

    public CompareView()
    {
        Focusable = true;
        Cursor = Cursors.SizeWE;
        Background = CheckerBrush.Create(this);
        _before.Clip = _clip;
        _line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.TextPrimary);
        Children.Add(_after);
        Children.Add(_before);
        Children.Add(_line);
        SizeChanged += (_, _) => Layout();
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            CaptureMouse();
            Move(e.GetPosition(this));
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (IsMouseCaptured) Move(e.GetPosition(this));
        };
        MouseLeftButtonUp += (_, _) => ReleaseMouseCapture();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) Position = Math.Max(0, Position - 0.05);
            else if (e.Key == Key.Right) Position = Math.Min(1, Position + 0.05);
            else return;
            e.Handled = true;
        };
    }

    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(
        nameof(Before), typeof(ImageSource), typeof(CompareView), new PropertyMetadata(null, (d, e) => ((CompareView)d)._before.Source = (ImageSource?)e.NewValue));

    public ImageSource? Before
    {
        get => (ImageSource?)GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(
        nameof(After), typeof(ImageSource), typeof(CompareView), new PropertyMetadata(null, (d, e) => ((CompareView)d)._after.Source = (ImageSource?)e.NewValue));

    public ImageSource? After
    {
        get => (ImageSource?)GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(CompareView), new PropertyMetadata(0.5, (d, e) => ((CompareView)d).Layout()));

    /// <summary>Доля ширины, занятая «до» (0…1).</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    private void Move(Point p)
    {
        if (ActualWidth > 0) Position = Math.Max(0, Math.Min(1, p.X / ActualWidth));
    }

    private void Layout()
    {
        var x = ActualWidth * Position;
        _clip.Rect = new Rect(0, 0, Math.Max(0, x), Math.Max(0, ActualHeight));
        _line.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
    }
}

/// <summary>Шахматный фон из цветов темы (карточка и разделитель): прозрачные места картинки видны в любой теме.</summary>
public static class CheckerBrush
{
    public static Brush Create(FrameworkElement owner)
    {
        var light = new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) };
        var dark = new GeometryDrawing { Geometry = Geometry.Parse("M0,0 H8 V8 H0 Z M8,8 H16 V16 H8 Z") };
        var group = new DrawingGroup();
        group.Children.Add(light);
        group.Children.Add(dark);
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        void Apply()
        {
            light.Brush = owner.TryFindResource(ThemeKeys.CardBackground) as Brush;
            dark.Brush = owner.TryFindResource(ThemeKeys.Divider) as Brush;
        }
        void OnTheme(object? s, EventArgs e) => owner.Dispatcher.BeginInvoke(new Action(Apply));
        // Подписка на смену темы — только пока элемент на экране (событие статическое)
        owner.Loaded += (_, _) =>
        {
            Apply();
            ThemeManager.Changed -= OnTheme;
            ThemeManager.Changed += OnTheme;
        };
        owner.Unloaded += (_, _) => ThemeManager.Changed -= OnTheme;
        return brush;
    }
}

/// <summary>Рамка с шахматным фоном — подложка под результат и превью.</summary>
public sealed class CheckerBorder : Border
{
    public CheckerBorder() => Background = CheckerBrush.Create(this);
}
