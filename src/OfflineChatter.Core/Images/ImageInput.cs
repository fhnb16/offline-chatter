using System;
using System.Globalization;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineChatter.Core.Images;

/// <summary>Как вписать картинку в холст другого размера.</summary>
public enum ImageFit
{
    /// <summary>Заполнить холст, обрезав лишнее по центру (как делает sd-server по умолчанию).</summary>
    CropResize,
    /// <summary>Вписать целиком, остаток — поля.</summary>
    FitPad,
    /// <summary>Растянуть без сохранения пропорций.</summary>
    Stretch,
}

/// <summary>Прямоугольник в пикселях (double — для точного масштаба при отрисовке).</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height);

/// <summary>
/// Правила для входных картинок на 8 ГБ видеопамяти. Большие исходники раздувают сразу две вещи: число патчей зрения
/// кодировщика (у Qwen-VL есть жёсткий предел) и длину последовательности внимания диффузионной модели — каждый референс
/// добавляет свои латентные токены. Поэтому длинная сторона референсов ограничивается тем сильнее, чем их больше.
/// </summary>
public static class ImageInputPolicy
{
    /// <summary>Выше этого размера (вход или выход) VAE кодирует и декодирует плитками.</summary>
    public const int TilingThreshold = 1280;

    /// <summary>Предел длинной стороны референса: 1 → 1536 (2048 для правки в 2K), 2–3 → 1024, 4 и больше → 768.</summary>
    public static int RefMaxSide(int count, bool nativeSize = false) => count switch
    {
        <= 1 => nativeSize ? 2048 : 1536,
        <= 3 => 1024,
        _ => 768,
    };

    /// <summary>Округлить до кратного (не меньше одного шага).</summary>
    public static int Align(double value, int multiple)
    {
        multiple = Math.Max(1, multiple);
        return Math.Max(multiple, (int)Math.Round(value / multiple) * multiple);
    }

    /// <summary>Вниз до кратного: размеры из полей ввода не должны вырасти.</summary>
    public static int AlignDown(int value, int multiple)
    {
        multiple = Math.Max(1, multiple);
        return Math.Max(multiple, value / multiple * multiple);
    }

    /// <summary>
    /// Уменьшить (не увеличивать) до <paramref name="maxSide"/> по длинной стороне с сохранением пропорций;
    /// при <paramref name="multiple"/> &gt; 1 обе стороны выравниваются. Второе значение — изменился ли размер.
    /// </summary>
    public static (int width, int height, bool changed) FitWithin(int width, int height, int maxSide, int multiple = 1)
    {
        if (width <= 0 || height <= 0) return (width, height, false);
        var longest = Math.Max(width, height);
        var k = maxSide > 0 && longest > maxSide ? (double)maxSide / longest : 1.0;
        var w = width * k;
        var h = height * k;
        int nw, nh;
        if (multiple > 1)
        {
            nw = Align(w, multiple);
            nh = Align(h, multiple);
            // округление вверх могло вывести за предел — шаг назад
            while (maxSide > 0 && Math.Max(nw, nh) > maxSide && Math.Max(nw, nh) > multiple)
            {
                if (nw >= nh) nw -= multiple;
                else nh -= multiple;
            }
        }
        else
        {
            nw = Math.Max(1, (int)Math.Round(w));
            nh = Math.Max(1, (int)Math.Round(h));
        }
        return (nw, nh, nw != width || nh != height);
    }

    /// <summary>
    /// Размер холста по пропорциям картинки при площади <paramref name="side"/>² (1024 — 1K, 2048 — 2K), кратно модели.
    /// </summary>
    public static (int width, int height) SizeForSource(int srcWidth, int srcHeight, int side, int multiple)
    {
        if (srcWidth <= 0 || srcHeight <= 0) return (side, side);
        var area = (double)side * side;
        var w = Math.Sqrt(area * srcWidth / srcHeight);
        var h = area / w;
        return (Align(w, multiple), Align(h, multiple));
    }

    /// <summary>Нужны ли плитки VAE: выход или любой вход больше порога.</summary>
    public static bool NeedsVaeTiling(int width, int height, IEnumerable<(int width, int height)> inputs, int threshold = TilingThreshold) =>
        width > threshold || height > threshold || inputs.Any(i => i.width > threshold || i.height > threshold);

    /// <summary>
    /// Куда и какую часть исходника рисовать на холсте <paramref name="dstW"/> × <paramref name="dstH"/>:
    /// первая рамка — участок исходника, вторая — место на холсте.
    /// </summary>
    public static (PixelRect source, PixelRect target) Placement(int srcW, int srcH, int dstW, int dstH, ImageFit fit)
    {
        var full = new PixelRect(0, 0, srcW, srcH);
        var canvas = new PixelRect(0, 0, dstW, dstH);
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || fit == ImageFit.Stretch) return (full, canvas);
        var srcRatio = (double)srcW / srcH;
        var dstRatio = (double)dstW / dstH;
        if (fit == ImageFit.CropResize)
        {
            if (srcRatio > dstRatio)
            {
                var w = srcH * dstRatio;
                return (new PixelRect((srcW - w) / 2, 0, w, srcH), canvas);
            }
            var h = srcW / dstRatio;
            return (new PixelRect(0, (srcH - h) / 2, srcW, h), canvas);
        }
        // FitPad
        if (srcRatio > dstRatio)
        {
            var h = dstW / srcRatio;
            return (full, new PixelRect(0, (dstH - h) / 2, dstW, h));
        }
        var ww = dstH * srcRatio;
        return (full, new PixelRect((dstW - ww) / 2, 0, ww, dstH));
    }

    /// <summary>
    /// Медленный шаг: больше <paramref name="limitSeconds"/> на мегапиксель 1024². На 8 ГБ это почти всегда значит, что
    /// драйвер NVIDIA ушёл в общую (системную) память, или референсов слишком много.
    /// </summary>
    public static bool IsSlowStep(double secondsPerStep, int width, int height, double limitSeconds = 12.0)
    {
        if (secondsPerStep <= 0 || width <= 0 || height <= 0) return false;
        var mp = width * (double)height / (1024.0 * 1024.0);
        return secondsPerStep / Math.Max(0.25, mp) > limitSeconds;
    }
}

/// <summary>Картинки внутри промпта: &lt;image:ID&gt; (поддерживают кодировщики Qwen-VL со зрением).</summary>
public static class PromptImageTags
{
    // Маркер экранируется нечётным числом обратных слешей перед ним (правило sd.cpp)
    private static readonly Regex Rx = new(@"(?<!\\)(?<slashes>(?:\\\\)*)<image:(?<id>[^>]+)>", RegexOptions.CultureInvariant);

    public static string Tag(string id) => "<image:" + id + ">";

    /// <summary>ID картинок, на которые ссылается промпт, по порядку и без повторов.</summary>
    public static IReadOnlyList<string> Ids(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return Array.Empty<string>();
        return Rx.Matches(prompt!).Cast<Match>().Select(m => m.Groups["id"].Value.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Qwen Image 2.1 сам нумерует визуальные референсы как &lt;image1&gt;, &lt;image2&gt; и т. д.</summary>
    public static string ToNumberedReferences(string prompt) => Rx.Replace(prompt, match =>
        int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? match.Groups["slashes"].Value + "<image" + number.ToString(CultureInfo.InvariantCulture) + ">"
            : match.Value);
}

/// <summary>Прозрачный фон Qwen-Image 2.1: модель решает по тексту, поэтому промпт оборачивается в форму из README авторов.</summary>
public static class TransparentPrompt
{
    public const string Prefix = "This is an RGBA image with transparency.";
    public const string Suffix = "The image has alpha channel and the background is transparent.";

    public static bool IsWrapped(string prompt) =>
        prompt.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && prompt.TrimEnd().EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    public static string Wrap(string prompt)
    {
        var p = prompt.Trim();
        if (IsWrapped(p)) return p;
        if (p.Length > 0 && !".!?".Contains(p[p.Length - 1])) p += ".";
        return Prefix + " " + p + " " + Suffix;
    }
}

/// <summary>Предупреждения о сочетании параметров, которые портят результат или память.</summary>
public enum ImageParamWarning
{
    /// <summary>Turbo/дистиллят с CFG выше 1: пересвет и артефакты, вдвое медленнее.</summary>
    DistilledCfg,
    /// <summary>Кэш шагов на 4–8 шагах: пропуск даже одного шага заметно портит картинку.</summary>
    DistilledStepCache,
    /// <summary>Число шагов не совпадает с расписанием сигм — sd.cpp возьмёт шаги из сигм.</summary>
    SigmasOverrideSteps,
    /// <summary>Правка Qwen-Image 2.1 меньше родного 2K: правки бывают пережаренными, стоит дожать «Финалом в 2K».</summary>
    EditBelowNative,
    /// <summary>Режим правки без mmproj у GGUF-кодировщика.</summary>
    VisionMissing,
}

public static class ImageParamChecks
{
    public static IReadOnlyList<ImageParamWarning> Check(ImagePreset preset, ImageGenParams p, bool visionMissing = false)
    {
        var list = new List<ImageParamWarning>();
        if (preset.IsDistilled && p.Cfg > 1.0 + 1e-6) list.Add(ImageParamWarning.DistilledCfg);
        if (preset.IsDistilled && !string.IsNullOrEmpty(p.CacheMode)) list.Add(ImageParamWarning.DistilledStepCache);
        if (ImageSigmas.Parse(p.Sigmas) is { } s && ImageSigmas.Steps(s) != p.Steps) list.Add(ImageParamWarning.SigmasOverrideSteps);
        var usesRefs = p.Mode == ImageMode.Edit;
        if (usesRefs && preset.NativeEditSide is { } native && Math.Max(p.Width, p.Height) < native * 3 / 4) list.Add(ImageParamWarning.EditBelowNative);
        if (usesRefs && visionMissing) list.Add(ImageParamWarning.VisionMissing);
        return list;
    }
}

/// <summary>
/// Предсказание попадания в кэш кондиционирования sd-server: тот же промпт и те же байты картинок для той же модели
/// не кодируются заново (зрение и Qwen3-VL не запускаются). Сервер держит LRU на N записей — здесь та же логика,
/// чтобы показать «промпт уже закодирован» до запуска. Сбрасывается при перезапуске сервера.
/// </summary>
public sealed class ConditioningCacheTracker
{
    private readonly LinkedList<string> _keys = new();
    private readonly object _gate = new();

    public int Capacity { get; set; } = 4;

    public static string Key(string session, string prompt, string negative, IEnumerable<byte[]> images)
    {
        using var sha = SHA256.Create();
        void Add(byte[] b)
        {
            var len = BitConverter.GetBytes(b.Length);
            sha.TransformBlock(len, 0, len.Length, null, 0);
            sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        Add(Encoding.UTF8.GetBytes(session));
        Add(Encoding.UTF8.GetBytes(prompt));
        Add(Encoding.UTF8.GetBytes(negative));
        foreach (var img in images) Add(img);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty);
    }

    /// <summary>Есть ли ключ в кэше (без изменения порядка).</summary>
    public bool Contains(string key)
    {
        lock (_gate) return _keys.Contains(key);
    }

    /// <summary>Запомнить ключ после удачной генерации; возвращает, был ли он уже в кэше.</summary>
    public bool Touch(string key)
    {
        lock (_gate)
        {
            var hit = _keys.Remove(key);
            if (Capacity <= 0) return false;
            _keys.AddFirst(key);
            while (_keys.Count > Capacity) _keys.RemoveLast();
            return hit;
        }
    }

    public void Clear()
    {
        lock (_gate) _keys.Clear();
    }
}

/// <summary>
/// Размер PNG/JPEG без декодирования и параметры программы в PNG (чанк tEXt «offline-chatter»): перетащите готовую
/// картинку обратно — параметры восстановятся, даже если соседний .json потерялся.
/// </summary>
public static class PngMeta
{
    public const string Keyword = "offline-chatter";
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(byte[] b) => b.Length >= 8 && Signature.SequenceEqual(b.Take(8));

    /// <summary>Ширина и высота PNG (IHDR) или JPEG (маркер SOFn); null — не узнали.</summary>
    public static (int width, int height)? ReadSize(byte[] b)
    {
        if (IsPng(b) && b.Length >= 24) return (ReadInt32BE(b, 16), ReadInt32BE(b, 20));
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var len = (b[i + 2] << 8) | b[i + 3];
                // SOF0..SOF15, кроме DHT (C4), JPG (C8), DAC (CC)
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }

    /// <summary>Вставить (или заменить) tEXt-чанк с ключом <see cref="Keyword"/> перед IEND.</summary>
    public static byte[] WithText(byte[] png, string text)
    {
        if (!IsPng(png)) return png;
        var chunks = Chunks(png).Where(c => !(c.type == "tEXt" && TextKeyword(png, c) == Keyword)).ToList();
        var payload = Encoding.GetEncoding("ISO-8859-1").GetBytes(Keyword).Concat(new byte[] { 0 }).Concat(Latin1Safe(text)).ToArray();
        using var ms = new MemoryStream(png.Length + payload.Length + 12);
        ms.Write(Signature, 0, Signature.Length);
        foreach (var c in chunks)
        {
            if (c.type == "IEND") WriteChunk(ms, "tEXt", payload);
            ms.Write(png, c.offset, c.length + 12);
        }
        return ms.ToArray();
    }

    /// <summary>Текст из tEXt-чанка программы; null — его нет.</summary>
    public static string? ReadText(byte[] png)
    {
        if (!IsPng(png)) return null;
        foreach (var c in Chunks(png))
        {
            if (c.type != "tEXt" || TextKeyword(png, c) != Keyword) continue;
            var start = c.offset + 8 + Keyword.Length + 1;
            var len = c.length - Keyword.Length - 1;
            if (len < 0) return null;
            return Encoding.GetEncoding("ISO-8859-1").GetString(png, start, len);
        }
        return null;
    }

    // tEXt хранит Latin-1; промпты бывают на русском — не-ASCII пишется как \uXXXX (внутри строк JSON это та же строка)
    private static byte[] Latin1Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 0x80 && ch != '\0') sb.Append(ch);
            else sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
        }
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string? TextKeyword(byte[] png, (string type, int offset, int length) c)
    {
        var start = c.offset + 8;
        var end = Array.IndexOf(png, (byte)0, start, Math.Min(80, c.length));
        return end < 0 ? null : Encoding.GetEncoding("ISO-8859-1").GetString(png, start, end - start);
    }

    private static IEnumerable<(string type, int offset, int length)> Chunks(byte[] png)
    {
        var i = 8;
        while (i + 12 <= png.Length)
        {
            var len = ReadInt32BE(png, i);
            if (len < 0 || i + 12 + len > png.Length) yield break;
            var type = Encoding.ASCII.GetString(png, i + 4, 4);
            yield return (type, i, len);
            if (type == "IEND") yield break;
            i += 12 + len;
        }
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);
        s.Write(len, 0, 4);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        var crc = Crc32(typeBytes.Concat(data));
        var crcBytes = BitConverter.GetBytes(crc);
        if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
        s.Write(crcBytes, 0, 4);
    }

    private static int ReadInt32BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    private static uint[]? _crcTable;

    internal static uint Crc32(IEnumerable<byte> data)
    {
        var table = _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
