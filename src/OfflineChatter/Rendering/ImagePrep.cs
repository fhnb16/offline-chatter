using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OfflineChatter.Core.Images;

namespace OfflineChatter.Rendering;

/// <summary>Картинка для генерации: исходные байты (PNG/JPEG), размер и наличие прозрачности.</summary>
public sealed record GenImage(byte[] Data, int Width, int Height, bool HasAlpha);

/// <summary>
/// Подготовка картинок для stable-diffusion.cpp. В отличие от вложений чата (<see cref="ImageAttachments"/>), здесь
/// ничего не сжимается в JPEG и не теряет альфа-канал: Qwen-Image 2.1 читает RGBA исходника и референсов. Размеры
/// меняются только по правилам <see cref="ImageInputPolicy"/>; маска масштабируется без сглаживания (иначе края
/// расплываются в полутона), фото — с качественной интерполяцией. Все объекты замораживаются — методы можно вызывать
/// из фонового потока.
/// </summary>
public static class ImagePrep
{
    /// <summary>Файл как есть: PNG и JPEG не перекодируются, остальное (WebP, BMP, GIF) — в PNG.</summary>
    public static GenImage FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    public static GenImage FromBytes(byte[] data)
    {
        var frame = Decode(data) ?? throw new NotSupportedException("Unsupported image");
        var isJpeg = data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8;
        var keep = PngMeta.IsPng(data) || isJpeg;
        var bytes = keep ? data : EncodePng(frame);
        return new GenImage(bytes, frame.PixelWidth, frame.PixelHeight, HasAlpha(frame));
    }

    /// <summary>Из буфера обмена или окна просмотра: всегда PNG (без потерь, с альфой).</summary>
    public static GenImage FromBitmap(BitmapSource src) => new(EncodePng(src), src.PixelWidth, src.PixelHeight, HasAlpha(src));

    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Миниатюра или крупный просмотр (декодирование сразу в нужную ширину).</summary>
    public static BitmapSource? Thumbnail(byte[] data, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bmp.DecodePixelWidth = width;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or COMException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public static bool HasAlpha(BitmapSource img) =>
        img.Format == PixelFormats.Bgra32 || img.Format == PixelFormats.Pbgra32 || img.Format == PixelFormats.Prgba64 || img.Format == PixelFormats.Rgba64 ||
        img.Format == PixelFormats.Rgba128Float || img.Format == PixelFormats.Prgba128Float;

    /// <summary>
    /// Уменьшить по длинной стороне (кратно <paramref name="multiple"/>); если уменьшать не нужно — исходные байты.
    /// JPEG остаётся JPEG (качество 95), PNG и картинки с альфой — PNG.
    /// </summary>
    public static GenImage Downscale(GenImage img, int maxSide, int multiple)
    {
        var (w, h, changed) = ImageInputPolicy.FitWithin(img.Width, img.Height, maxSide, multiple);
        if (!changed) return img;
        var src = Decode(img.Data);
        if (src == null) return img;
        var scaled = Draw(src, w, h, new PixelRect(0, 0, src.PixelWidth, src.PixelHeight), new PixelRect(0, 0, w, h), background: null);
        var isJpeg = !PngMeta.IsPng(img.Data) && !img.HasAlpha;
        return new GenImage(isJpeg ? EncodeJpeg(scaled) : EncodePng(scaled), w, h, img.HasAlpha);
    }

    /// <summary>Исходник img2img на холст выхода: обрезка, поля или растяжение.</summary>
    public static byte[] FitToCanvas(GenImage img, int width, int height, ImageFit fit)
    {
        var src = Decode(img.Data) ?? throw new NotSupportedException("Unsupported image");
        if (src.PixelWidth == width && src.PixelHeight == height) return img.Data;
        var (s, d) = ImageInputPolicy.Placement(src.PixelWidth, src.PixelHeight, width, height, fit);
        // Поля: прозрачные у картинки с альфой, чёрные у фото
        var bmp = Draw(src, width, height, s, d, background: img.HasAlpha ? null : Colors.Black);
        return EncodePng(bmp);
    }

    /// <summary>Контуры исходника (Canny) на холсте выхода: белые линии на чёрном, RGB.</summary>
    public static byte[] CannyOnCanvas(GenImage img, int width, int height, ImageFit fit)
    {
        var src = Decode(img.Data) ?? throw new NotSupportedException("Unsupported image");
        var (s, d) = ImageInputPolicy.Placement(src.PixelWidth, src.PixelHeight, width, height, fit);
        var bmp = Draw(src, width, height, s, d, background: Colors.Black);
        var bgra = Pixels(bmp);
        var edges = Canny.Detect(bgra, width, height);
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < edges.Length; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = edges[i];
        var outBmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, rgb, width * 3);
        outBmp.Freeze();
        return EncodePng(outBmp);
    }

    /// <summary>
    /// Маска (белое — перерисовать) из нарисованного слоя: та же подгонка, что у исходника, без сглаживания,
    /// порог по яркости, по желанию — инверсия. Одноканальный PNG.
    /// </summary>
    public static byte[] MaskOnCanvas(byte[] maskPng, int width, int height, ImageFit fit, bool invert)
    {
        var src = Decode(maskPng) ?? throw new NotSupportedException("Unsupported mask");
        var (s, d) = ImageInputPolicy.Placement(src.PixelWidth, src.PixelHeight, width, height, fit);
        var bmp = Draw(src, width, height, s, d, background: Colors.Black);
        var bgra = Pixels(bmp);
        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var o = i * 4;
            var on = bgra[o] + bgra[o + 1] + bgra[o + 2] > 3 * 127;
            gray[i] = on ^ invert ? (byte)255 : (byte)0;
        }
        var outBmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
        outBmp.Freeze();
        return EncodePng(outBmp);
    }

    /// <summary>Есть ли в маске хоть один закрашенный пиксель.</summary>
    public static bool MaskHasInk(byte[] maskPng)
    {
        var src = Decode(maskPng);
        if (src == null) return false;
        var px = Pixels(src);
        for (var i = 0; i < px.Length; i += 4)
            if (px[i] + px[i + 1] + px[i + 2] > 3 * 127) return true;
        return false;
    }

    /// <summary>
    /// Участок <paramref name="s"/> исходника в рамку <paramref name="d"/> холста. Только WIC (обрезка, масштаб Fant,
    /// смена формата) и копирование пикселей: DrawingVisual и RenderTargetBitmap требуют STA-поток, а подготовка идёт в фоне.
    /// Маска масштабируется так же, но затем проходит порог — края остаются чёткими.
    /// </summary>
    private static BitmapSource Draw(BitmapSource src, int width, int height, PixelRect s, PixelRect d, Color? background)
    {
        BitmapSource part = src;
        var full = s.X <= 0 && s.Y <= 0 && Math.Abs(s.Width - src.PixelWidth) < 0.5 && Math.Abs(s.Height - src.PixelHeight) < 0.5;
        if (!full)
        {
            var rect = new Int32Rect((int)Math.Round(s.X), (int)Math.Round(s.Y),
                Math.Max(1, Math.Min(src.PixelWidth, (int)Math.Round(s.Width))), Math.Max(1, Math.Min(src.PixelHeight, (int)Math.Round(s.Height))));
            if (rect.X + rect.Width > src.PixelWidth) rect.X = src.PixelWidth - rect.Width;
            if (rect.Y + rect.Height > src.PixelHeight) rect.Y = src.PixelHeight - rect.Height;
            part = new CroppedBitmap(src, rect);
        }
        var dw = Math.Max(1, (int)Math.Round(d.Width));
        var dh = Math.Max(1, (int)Math.Round(d.Height));
        BitmapSource scaled = part.PixelWidth == dw && part.PixelHeight == dh
            ? part
            : new TransformedBitmap(part, new ScaleTransform((double)dw / part.PixelWidth, (double)dh / part.PixelHeight));
        var px = Pixels(scaled);
        var sw = scaled.PixelWidth;
        var sh = scaled.PixelHeight;
        var dx = (int)Math.Round(d.X);
        var dy = (int)Math.Round(d.Y);

        var canvas = new byte[width * height * 4];
        if (background is { } bg)
        {
            for (var i = 0; i < canvas.Length; i += 4)
            {
                canvas[i] = bg.B;
                canvas[i + 1] = bg.G;
                canvas[i + 2] = bg.R;
                canvas[i + 3] = bg.A;
            }
        }
        // Копируем построчно; у TransformedBitmap размер мог разойтись на пиксель — обрезаем по холсту
        var copyW = Math.Min(sw, width - Math.Max(0, dx));
        for (var y = 0; y < sh; y++)
        {
            var ty = dy + y;
            if (ty < 0 || ty >= height || copyW <= 0) continue;
            if (background == null)
            {
                Buffer.BlockCopy(px, y * sw * 4, canvas, (ty * width + Math.Max(0, dx)) * 4, copyW * 4);
                continue;
            }
            // С фоном: смешиваем по альфе, чтобы прозрачные места исходника не стали «дырами»
            for (var x = 0; x < copyW; x++)
            {
                var si = (y * sw + x) * 4;
                var di = (ty * width + Math.Max(0, dx) + x) * 4;
                var a = px[si + 3];
                if (a == 255)
                {
                    canvas[di] = px[si];
                    canvas[di + 1] = px[si + 1];
                    canvas[di + 2] = px[si + 2];
                    canvas[di + 3] = 255;
                }
                else if (a > 0)
                {
                    canvas[di] = (byte)((px[si] * a + canvas[di] * (255 - a)) / 255);
                    canvas[di + 1] = (byte)((px[si + 1] * a + canvas[di + 1] * (255 - a)) / 255);
                    canvas[di + 2] = (byte)((px[si + 2] * a + canvas[di + 2] * (255 - a)) / 255);
                    canvas[di + 3] = 255;
                }
            }
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Пиксели BGRA (без премультипликации) построчно без выравнивания.</summary>
    public static byte[] Pixels(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);
        return px;
    }

    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeJpeg(BitmapSource src)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(HasAlpha(src) ? new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0) : src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
