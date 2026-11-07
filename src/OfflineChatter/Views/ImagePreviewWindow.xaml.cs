using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.Views;

/// <summary>Просмотр изображения из вложения: масштаб, сдвиг, копирование и сохранение.</summary>
public partial class ImagePreviewWindow : Window
{
    private const double MinZoom = 0.05, MaxZoom = 16;
    private readonly PreviewModel _model;
    private Point _dragStart;
    private Point _scrollStart;
    private bool _dragging;

    public ImagePreviewWindow(ChatAttachment attachment)
    {
        InitializeComponent();
        _model = new PreviewModel(attachment);
        DataContext = _model;
        Title = attachment.Name;
        Info.Text = _model.Image is { } img ? $"{img.PixelWidth} × {img.PixelHeight}" : string.Empty;
        ThemeManager.Attach(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) _model.CopyCommand.Execute(null);
            else if (e.Key is Key.OemPlus or Key.Add) Zoom(1.25, null);
            else if (e.Key is Key.OemMinus or Key.Subtract) Zoom(0.8, null);
            else if (e.Key is Key.D0 or Key.NumPad0) Fit();
        };
        ContentRendered += (_, _) => Fit();
    }

    /// <summary>Открыть просмотр поверх главного окна.</summary>
    public static void Show(ChatAttachment attachment)
    {
        if (attachment.Kind != AttachmentKind.Image || string.IsNullOrEmpty(attachment.Data)) return;
        var w = new ImagePreviewWindow(attachment) { Owner = Application.Current?.MainWindow };
        w.Show();
    }

    private double CurrentZoom => Scale.ScaleX;

    private void SetZoom(double zoom)
    {
        zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
        Scale.ScaleX = Scale.ScaleY = zoom;
        ZoomText.Text = Math.Round(zoom * 100) + " %";
    }

    /// <summary>Масштаб с сохранением точки под курсором (или центра).</summary>
    private void Zoom(double factor, Point? anchor)
    {
        var a = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var contentX = (Scroller.HorizontalOffset + a.X) / CurrentZoom;
        var contentY = (Scroller.VerticalOffset + a.Y) / CurrentZoom;
        SetZoom(CurrentZoom * factor);
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(contentX * CurrentZoom - a.X);
        Scroller.ScrollToVerticalOffset(contentY * CurrentZoom - a.Y);
    }

    /// <summary>Вписать в окно (без увеличения маленьких картинок).</summary>
    private void Fit()
    {
        if (_model.Image is not { } img || Scroller.ViewportWidth <= 0) return;
        var k = Math.Min(Scroller.ViewportWidth / img.Width, Scroller.ViewportHeight / img.Height);
        SetZoom(Math.Min(1, k * 0.98));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(Scroller));
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(1.25, null);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(0.8, null);
    private void OnFit(object sender, RoutedEventArgs e) => Fit();
    private void OnActualSize(object sender, RoutedEventArgs e) => SetZoom(1);

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ScrollBar || IsInsideScrollBar(e.OriginalSource as DependencyObject)) return;
        _dragging = true;
        _dragStart = e.GetPosition(Scroller);
        _scrollStart = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Scroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_scrollStart.X - (p.X - _dragStart.X));
        Scroller.ScrollToVerticalOffset(_scrollStart.Y - (p.Y - _dragStart.Y));
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Scroller.ReleaseMouseCapture();
    }

    private static bool IsInsideScrollBar(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private sealed partial class PreviewModel : ObservableObject
    {
        private readonly ChatAttachment _attachment;

        public PreviewModel(ChatAttachment attachment)
        {
            _attachment = attachment;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(Convert.FromBase64String(attachment.Data));
                bmp.EndInit();
                bmp.Freeze();
                Image = bmp;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
            {
                Status = L.F("Data_Failed", ex.Message);
            }
        }

        public BitmapSource? Image { get; }

        [ObservableProperty] private string? _status;

        /// <summary>В буфер — и PNG (браузеры, мессенджеры), и обычный рисунок (Office, Paint).</summary>
        [RelayCommand]
        private void Copy()
        {
            if (Image == null) return;
            try
            {
                var data = new DataObject();
                data.SetImage(Image);
                var png = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Image));
                encoder.Save(png);
                data.SetData("PNG", png);
                Clipboard.SetDataObject(data, copy: true);
                Status = L.Get("Preview_Copied");
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                Status = L.F("Data_Failed", ex.Message);
            }
        }

        [RelayCommand]
        private void Save()
        {
            var ext = _attachment.MimeType == "image/png" ? ".png" : ".jpg";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_attachment.Name) + ext,
                Filter = (ext == ".png" ? "PNG|*.png" : "JPEG|*.jpg") + "|*.*|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, Convert.FromBase64String(_attachment.Data));
                Status = L.F("Preview_Saved", dlg.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = L.F("Data_Failed", ex.Message);
            }
        }
    }
}
