using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Rendering;

/// <summary>
/// Подготовка изображений для vision-моделей: уменьшение по длинной стороне (больше модели всё равно не используют,
/// а токенов и памяти уходит меньше) и сжатие в JPEG; PNG — только для небольших картинок с прозрачностью.
/// Параметры — в настройках (раздел «Чат»).
/// </summary>
public static class ImageAttachments
{
    public static AttachmentSettings Settings { get; set; } = new();

    public static ChatAttachment FromFile(string path)
    {
        var bmp = new BitmapImage();
        using (var fs = File.OpenRead(path))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = fs;
            bmp.EndInit();
        }
        bmp.Freeze();
        return FromBitmap(bmp, Path.GetFileName(path));
    }

    public static ChatAttachment FromBitmap(BitmapSource source, string name)
    {
        BitmapSource img = source;
        var maxSide = Settings.ImageMaxSide;
        var longest = Math.Max(img.PixelWidth, img.PixelHeight);
        if (maxSide > 0 && longest > maxSide)
        {
            var k = (double)maxSide / longest;
            img = new TransformedBitmap(img, new ScaleTransform(k, k));
        }

        var hasAlpha = HasAlpha(img);
        BitmapEncoder encoder;
        string mime;
        if (hasAlpha && Settings.KeepPngWithAlpha && img.PixelWidth * img.PixelHeight <= 512 * 512)
        {
            encoder = new PngBitmapEncoder();
            mime = "image/png";
        }
        else
        {
            // JPEG не умеет прозрачность — подкладываем белый фон
            if (hasAlpha) img = Flatten(img);
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Max(40, Math.Min(100, Settings.ImageQuality)) };
            mime = "image/jpeg";
        }
        encoder.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var ext = mime == "image/png" ? ".png" : ".jpg";
        return new ChatAttachment
        {
            Kind = AttachmentKind.Image,
            Name = Path.GetFileNameWithoutExtension(name) + ext,
            MimeType = mime,
            Data = Convert.ToBase64String(ms.ToArray()),
        };
    }

    private static bool HasAlpha(BitmapSource img) =>
        img.Format == PixelFormats.Bgra32 || img.Format == PixelFormats.Pbgra32 || img.Format == PixelFormats.Prgba64 || img.Format == PixelFormats.Rgba64;

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

    // ---------------- Буфер обмена ----------------

    /// <summary>
    /// Изображение из буфера обмена. Clipboard.GetImage() в WPF читает CF_DIB с альфа-каналом, которого там нет
    /// (браузеры, «Ножницы», Office кладут 32-битный DIB с нулевой альфой) — получается прозрачная, то есть
    /// белая после сжатия, картинка. Поэтому: сначала формат «PNG» (у него честная прозрачность), затем DIB
    /// с игнорированием альфы, если она вся нулевая, и только потом штатный путь.
    /// </summary>
    public static BitmapSource? GetClipboardImage()
    {
        var data = Clipboard.GetDataObject();
        if (data == null) return null;

        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream png)
            {
                var img = Decode(png);
                if (img != null) return img;
            }
        }

        if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib)
        {
            var img = FromDib(dib.ToArray());
            if (img != null) return img;
        }

        return Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
    }

    private static BitmapSource? Decode(Stream s)
    {
        try
        {
            s.Position = 0;
            var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException or IOException)
        {
            return null;
        }
    }

    /// <summary>CF_DIB → BMP-файл (дописываем BITMAPFILEHEADER) → декодер WIC; нулевая альфа → Bgr32.</summary>
    internal static BitmapSource? FromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var clrUsed = BitConverter.ToInt32(dib, 32);
        // BI_BITFIELDS со старым заголовком: после него идут три маски
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var palette = bitCount <= 8 ? (clrUsed == 0 ? 1 << bitCount : clrUsed) * 4 : clrUsed * 4;
        var pixelOffset = 14 + headerSize + masks + palette;

        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

        BitmapSource? img;
        try
        {
            var decoder = new BmpBitmapDecoder(new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            img = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException or IOException)
        {
            return null;
        }

        if (bitCount == 32 && HasAlpha(img) && AlphaIsEmpty(img))
            img = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
        img.Freeze();
        return img;
    }

    private static bool AlphaIsEmpty(BitmapSource img)
    {
        var src = img.Format == PixelFormats.Bgra32 ? img : new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var stride = src.PixelWidth * 4;
        var row = new byte[stride];
        // Проверяем каждую 8-ю строку — для решения «альфа пустая или нет» этого достаточно
        for (var y = 0; y < src.PixelHeight; y += 8)
        {
            src.CopyPixels(new Int32Rect(0, y, src.PixelWidth, 1), row, stride, 0);
            for (var i = 3; i < row.Length; i += 4)
                if (row[i] != 0) return false;
        }
        return true;
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
