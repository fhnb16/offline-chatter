using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OfflineChatter.Theming;

namespace OfflineChatter.Controls;

/// <summary>
/// Кисть маски поверх исходника img2img. Штрихи хранятся в координатах пикселей картинки (холст рисования —
/// её точного размера внутри Viewbox), поэтому маска рендерится в тот же размер без пересчёта и затем подгоняется
/// под холст генерации тем же способом, что и исходник. Белое на маске — перерисовать.
/// Чисто визуальная логика: модель представления получает готовый PNG через <see cref="MaskPng"/>.
/// </summary>
public sealed class MaskCanvas : Viewbox
{
    private readonly Grid _surface = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly InkCanvas _ink = new();
    private readonly DispatcherTimer _debounce;

    public MaskCanvas()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.Both;
        _ink.Background = Brushes.Transparent;
        _ink.UseCustomCursor = false;
        _surface.Children.Add(_image);
        _surface.Children.Add(_ink);
        Child = _surface;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            PublishMask();
        };
        _ink.StrokeCollected += (_, _) => _debounce.Start();
        _ink.StrokeErased += (_, _) => _debounce.Start();
        _ink.Strokes.StrokesChanged += (_, _) => _debounce.Start();
        Loaded += (_, _) => ApplyBrush();
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(MaskCanvas), new PropertyMetadata(null, (d, e) => ((MaskCanvas)d).OnSourceChanged()));

    /// <summary>Исходник; смена картинки стирает маску.</summary>
    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Пиксельный размер исходника (миниатюра для показа бывает меньше).</summary>
    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize), typeof(Size), typeof(MaskCanvas), new PropertyMetadata(Size.Empty, (d, e) => ((MaskCanvas)d).OnSourceChanged()));

    public Size PixelSize
    {
        get => (Size)GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    public static readonly DependencyProperty BrushFractionProperty = DependencyProperty.Register(
        nameof(BrushFraction), typeof(double), typeof(MaskCanvas), new PropertyMetadata(0.05, (d, e) => ((MaskCanvas)d).ApplyBrush()));

    /// <summary>Диаметр кисти как доля длинной стороны картинки: одинаковый вид на любом разрешении.</summary>
    public double BrushFraction
    {
        get => (double)GetValue(BrushFractionProperty);
        set => SetValue(BrushFractionProperty, value);
    }

    public static readonly DependencyProperty IsEraserProperty = DependencyProperty.Register(
        nameof(IsEraser), typeof(bool), typeof(MaskCanvas), new PropertyMetadata(false, (d, e) => ((MaskCanvas)d).ApplyBrush()));

    public bool IsEraser
    {
        get => (bool)GetValue(IsEraserProperty);
        set => SetValue(IsEraserProperty, value);
    }

    public static readonly DependencyProperty MaskPngProperty = DependencyProperty.Register(
        nameof(MaskPng), typeof(byte[]), typeof(MaskCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((MaskCanvas)d).OnMaskPngChanged((byte[]?)e.NewValue)));

    /// <summary>Готовая маска (PNG в размер исходника, белое на чёрном); null — маски нет. Сброс в null снаружи стирает штрихи.</summary>
    public byte[]? MaskPng
    {
        get => (byte[]?)GetValue(MaskPngProperty);
        set => SetValue(MaskPngProperty, value);
    }

    private bool _publishing;

    private void OnMaskPngChanged(byte[]? value)
    {
        if (_publishing || value != null) return;
        _ink.Strokes.Clear();
    }

    private Size Pixels => PixelSize.IsEmpty || PixelSize.Width <= 0 ? Source is { } s ? new Size(s.PixelWidth, s.PixelHeight) : Size.Empty : PixelSize;

    private void OnSourceChanged()
    {
        _image.Source = Source;
        var px = Pixels;
        if (!px.IsEmpty)
        {
            _surface.Width = px.Width;
            _surface.Height = px.Height;
        }
        _ink.Strokes.Clear();
        ApplyBrush();
    }

    private void ApplyBrush()
    {
        var px = Pixels;
        var longest = px.IsEmpty ? 1024 : Math.Max(px.Width, px.Height);
        var size = Math.Max(2, BrushFraction * longest);
        var color = TryFindResource(ThemeKeys.Accent) is SolidColorBrush accent ? accent.Color : SystemColors.HighlightColor;
        color.A = 0x99;
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = color,
            Width = size,
            Height = size,
            FitToCurve = false,
            IgnorePressure = true,
            StylusTip = StylusTip.Ellipse,
        };
        _ink.EraserShape = new EllipseStylusShape(size, size);
        _ink.EditingMode = IsEraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
    }

    private void PublishMask()
    {
        var px = Pixels;
        _publishing = true;
        try
        {
            if (_ink.Strokes.Count == 0 || px.IsEmpty)
            {
                MaskPng = null;
                return;
            }
            var w = (int)px.Width;
            var h = (int)px.Height;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
                foreach (var stroke in _ink.Strokes)
                {
                    var copy = stroke.Clone();
                    copy.DrawingAttributes.Color = Colors.White;
                    copy.Draw(dc);
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            MaskPng = ms.ToArray();
        }
        finally
        {
            _publishing = false;
        }
    }
}
