using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OfflineChatter.Theming;

namespace OfflineChatter.Controls;

/// <summary>
/// Сравнение «до / после» в координатах вписанной результирующей картинки.
/// </summary>
public sealed class CompareView : FrameworkElement
{
    private readonly Brush _checker;

    private static readonly DependencyProperty DividerBrushProperty = DependencyProperty.Register(
        "DividerBrush", typeof(Brush), typeof(CompareView), new FrameworkPropertyMetadata(SystemColors.WindowTextBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty GripBrushProperty = DependencyProperty.Register(
        "GripBrush", typeof(Brush), typeof(CompareView), new FrameworkPropertyMetadata(SystemColors.WindowBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public CompareView()
    {
        Focusable = true;
        Cursor = Cursors.SizeWE;
        _checker = CheckerBrush.Create(this);
        SetResourceReference(DividerBrushProperty, ThemeKeys.TextPrimary);
        SetResourceReference(GripBrushProperty, ThemeKeys.CardBackground);
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(
        nameof(Before), typeof(BitmapSource), typeof(CompareView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource? Before
    {
        get => (BitmapSource?)GetValue(BeforeProperty);
        set => SetValue(BeforeProperty, value);
    }

    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(
        nameof(After), typeof(BitmapSource), typeof(CompareView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => d.SetCurrentValue(PositionProperty, 0.5)));

    public BitmapSource? After
    {
        get => (BitmapSource?)GetValue(AfterProperty);
        set => SetValue(AfterProperty, value);
    }

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(CompareView), new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Math.Max(0, Math.Min(1, (double)value))), value => !double.IsNaN((double)value));

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    private Rect Frame => After is { } image ? Fit(image, new Rect(RenderSize)) : Rect.Empty;

    private static Rect Fit(BitmapSource source, Rect bounds)
    {
        var scale = Math.Min(bounds.Width / source.PixelWidth, bounds.Height / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var frame = Frame;
        if (frame.IsEmpty || frame.Width <= 0 || frame.Height <= 0 || After is not { } after) return;

        var x = frame.Left + frame.Width * Position;
        drawingContext.DrawRectangle(_checker, null, frame);
        if (Before is { } before && Position > 0)
        {
            drawingContext.PushClip(new RectangleGeometry(new Rect(frame.Left, frame.Top, x - frame.Left, frame.Height)));
            drawingContext.DrawImage(before, Fit(before, frame));
            drawingContext.Pop();
        }
        if (Position < 1)
        {
            drawingContext.PushClip(new RectangleGeometry(new Rect(x, frame.Top, frame.Right - x, frame.Height)));
            drawingContext.DrawImage(after, frame);
            drawingContext.Pop();
        }

        var divider = (Brush)GetValue(DividerBrushProperty);
        var grip = (Brush)GetValue(GripBrushProperty);
        var center = new Point(x, frame.Top + frame.Height / 2);
        var top = new Point(x, frame.Top);
        var bottom = new Point(x, frame.Bottom);
        drawingContext.DrawLine(new Pen(grip, 4), top, bottom);
        var line = new Pen(divider, 2);
        drawingContext.DrawLine(line, top, bottom);
        var outline = IsKeyboardFocused && TryFindResource(ThemeKeys.Accent) is Brush accent ? new Pen(accent, 2) : line;
        drawingContext.DrawEllipse(grip, outline, center, 12, 12);
        DrawChevron("IconChevronLeft", x - 4);
        DrawChevron("IconChevronRight", x + 4);

        void DrawChevron(string key, double offset)
        {
            if (TryFindResource(key) is not Geometry geometry) return;
            drawingContext.PushTransform(new TranslateTransform(offset - 8, center.Y - 8));
            drawingContext.DrawGeometry(null, line, geometry);
            drawingContext.Pop();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        Move(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (e.LeftButton == MouseButtonState.Pressed) Move(e.GetPosition(this));
        else ReleaseMouseCapture();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Move(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: SetCurrentValue(PositionProperty, Position - 0.05); break;
            case Key.Right: SetCurrentValue(PositionProperty, Position + 0.05); break;
            case Key.Home: SetCurrentValue(PositionProperty, 0.0); break;
            case Key.End: SetCurrentValue(PositionProperty, 1.0); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Move(Point p)
    {
        var frame = Frame;
        if (!frame.IsEmpty && frame.Width > 0) SetCurrentValue(PositionProperty, (p.X - frame.Left) / frame.Width);
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
