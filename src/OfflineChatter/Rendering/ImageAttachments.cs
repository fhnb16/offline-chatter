using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OfflineChatter.Core.Chat;

namespace OfflineChatter.Rendering;

/// <summary>
/// Подготовка изображений для vision-моделей: уменьшение до 1536 px по длинной стороне
/// (больше модели всё равно не используют, а токенов и памяти уходит меньше), JPEG 90 или PNG при прозрачности.
/// </summary>
public static class ImageAttachments
{
    public const int MaxSide = 1536;

    public static ChatAttachment FromFile(string path)
    {
        var bmp = new BitmapImage();
        using (var fs = File.OpenRead(path))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
        }
        bmp.Freeze();
        return FromBitmap(bmp, Path.GetFileName(path));
    }

    public static ChatAttachment FromBitmap(BitmapSource source, string name)
    {
        BitmapSource img = source;
        var longest = Math.Max(img.PixelWidth, img.PixelHeight);
        if (longest > MaxSide)
        {
            var k = (double)MaxSide / longest;
            img = new TransformedBitmap(img, new ScaleTransform(k, k));
        }

        var hasAlpha = img.Format == PixelFormats.Bgra32 || img.Format == PixelFormats.Pbgra32 || img.Format == PixelFormats.Prgba64;
        BitmapEncoder encoder;
        string mime;
        if (hasAlpha && img.PixelWidth * img.PixelHeight <= 512 * 512)
        {
            encoder = new PngBitmapEncoder();
            mime = "image/png";
        }
        else
        {
            // JPEG не умеет прозрачность — подкладываем белый фон
            if (hasAlpha) img = Flatten(img);
            encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            mime = "image/jpeg";
        }
        encoder.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return new ChatAttachment { Kind = AttachmentKind.Image, Name = name, MimeType = mime, Data = Convert.ToBase64String(ms.ToArray()) };
    }

    private static BitmapSource Flatten(BitmapSource src)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Миниатюра для показа в чате (из data URI вложения).</summary>
    public static BitmapImage? Thumbnail(ChatAttachment a, int decodeWidth = 320)
    {
        if (a.Kind != AttachmentKind.Image || string.IsNullOrEmpty(a.Data)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(Convert.FromBase64String(a.Data));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
