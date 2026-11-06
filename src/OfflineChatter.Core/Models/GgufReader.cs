using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Net;

namespace OfflineChatter.Core.Models;

/// <summary>Метаданные GGUF, нужные приложению (без тензоров и словаря токенизатора).</summary>
public sealed class GgufInfo
{
    public uint Version { get; set; }
    public ulong TensorCount { get; set; }
    public Dictionary<string, object> Metadata { get; } = new(StringComparer.Ordinal);

    public string? Architecture => Get<string>("general.architecture");
    public string? Name => Get<string>("general.name");
    public string? SizeLabel => Get<string>("general.size_label");
    public int? FileType => GetInt("general.file_type");
    public string? ChatTemplate => Get<string>("tokenizer.chat_template");
    public bool IsMmproj => Architecture == "clip" || Metadata.ContainsKey("clip.has_vision_encoder") || Metadata.ContainsKey("clip.vision.image_size");

    public int? ContextLength => ArchInt("context_length");
    public int? BlockCount => ArchInt("block_count");
    public int? EmbeddingLength => ArchInt("embedding_length");
    public int? HeadCount => ArchInt("attention.head_count");
    public int? HeadCountKv => ArchInt("attention.head_count_kv") ?? HeadCount;
    public int? KeyLength => ArchInt("attention.key_length");
    public int? ValueLength => ArchInt("attention.value_length");
    public int? ExpertCount => ArchInt("expert_count");

    /// <summary>Каждый N-й слой — полное внимание (гибридные модели, например Qwen3.5/Qwen3-Next), остальные без KV-кэша.</summary>
    public int? FullAttentionInterval => ArchInt("full_attention_interval");

    /// <summary>Скользящее окно внимания (Gemma и др.).</summary>
    public int? SlidingWindow => ArchInt("attention.sliding_window");

    public T? Get<T>(string key) where T : class => Metadata.TryGetValue(key, out var v) ? v as T : null;

    public int? GetInt(string key) => Metadata.TryGetValue(key, out var v) ? ToInt(v) : null;

    private int? ArchInt(string suffix) => Architecture != null ? GetInt(Architecture + "." + suffix) : null;

    private static int? ToInt(object v) => v switch
    {
        int i => i,
        uint u => (int)Math.Min(int.MaxValue, u),
        long l => (int)Math.Min(int.MaxValue, l),
        ulong ul => (int)Math.Min(int.MaxValue, ul),
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        // Массив по слоям (например, head_count_kv у моделей с переменным числом голов) — берём максимум
        GgufArray { IntValues: { Count: > 0 } vals } => (int)Math.Min(int.MaxValue, MaxOf(vals)),
        _ => null,
    };

    private static long MaxOf(List<long> v)
    {
        long m = long.MinValue;
        foreach (var x in v) if (x > m) m = x;
        return m;
    }
}

/// <summary>Массив из метаданных: числа сохраняются (если их немного), строки — только количество.</summary>
public sealed class GgufArray
{
    public uint ElementType { get; set; }
    public ulong Count { get; set; }
    public List<long>? IntValues { get; set; }
}

/// <summary>
/// Читатель заголовка GGUF (v1–v3). Работает потоково: удалённый файл читается HTTP-запросом с начала
/// и закрывается, как только метаданные прочитаны (словарь токенизатора пропускается без хранения).
/// </summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const int MaxStoredArray = 4096;

    public static GgufInfo ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Read(fs);
    }

    /// <summary>Чтение заголовка удалённого файла (HF): запрос без скачивания всего файла.</summary>
    public static async Task<GgufInfo> ReadRemoteAsync(HttpService http, string url, IDictionary<string, string>? headers, CancellationToken outer)
    {
        // Общий предел: заголовки + чтение метаданных (несколько МБ) — не дольше 4× сетевого таймаута
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(http.Settings.TimeoutSeconds * 4));
        var ct = cts.Token;
        using var response = await http.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            // Метаданные обычно занимают 2–15 МБ (словарь токенизатора); просим с запасом
            r.Headers.Range = new RangeHeaderValue(0, 64L * 1024 * 1024);
            if (headers != null) foreach (var h in headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return r;
        }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, url, string.Empty);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffered = new BufferedStream(stream, 1 << 16);
        return await Task.Run(() => Read(buffered), ct).ConfigureAwait(false);
    }

    public static GgufInfo Read(Stream s)
    {
        var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a GGUF file");
        var info = new GgufInfo { Version = r.ReadUInt32() };
        if (info.Version is < 1 or > 3) throw new InvalidDataException("Unsupported GGUF version " + info.Version);
        var v1 = info.Version == 1;
        info.TensorCount = v1 ? r.ReadUInt32() : r.ReadUInt64();
        var kvCount = v1 ? r.ReadUInt32() : r.ReadUInt64();
        if (kvCount > 100_000) throw new InvalidDataException("Implausible metadata count");

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(r, v1);
            var type = r.ReadUInt32();
            info.Metadata[key] = ReadValue(r, type, v1);
        }
        return info;
    }

    private static string ReadString(BinaryReader r, bool v1)
    {
        var len = v1 ? r.ReadUInt32() : r.ReadUInt64();
        if (len > 64 * 1024 * 1024) throw new InvalidDataException("String too long");
        var bytes = r.ReadBytes((int)len);
        if (bytes.Length != (int)len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static object ReadValue(BinaryReader r, uint type, bool v1)
    {
        switch (type)
        {
            case 0: return r.ReadByte();
            case 1: return r.ReadSByte();
            case 2: return r.ReadUInt16();
            case 3: return r.ReadInt16();
            case 4: return r.ReadUInt32();
            case 5: return r.ReadInt32();
            case 6: return r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8: return ReadString(r, v1);
            case 9: return ReadArray(r, v1);
            case 10: return r.ReadUInt64();
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            default: throw new InvalidDataException("Unknown GGUF value type " + type.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static GgufArray ReadArray(BinaryReader r, bool v1)
    {
        var arr = new GgufArray { ElementType = r.ReadUInt32() };
        arr.Count = v1 ? r.ReadUInt32() : r.ReadUInt64();
        var isInt = arr.ElementType is 0 or 1 or 2 or 3 or 4 or 5 or 10 or 11;
        if (isInt && arr.Count <= MaxStoredArray) arr.IntValues = new List<long>((int)arr.Count);

        for (ulong i = 0; i < arr.Count; i++)
        {
            switch (arr.ElementType)
            {
                case 8:
                    // строки (словарь токенизатора) — пропускаем без хранения
                    var len = v1 ? r.ReadUInt32() : r.ReadUInt64();
                    Skip(r, (long)len);
                    break;
                case 9:
                    ReadArray(r, v1);
                    break;
                default:
                    var value = ReadValue(r, arr.ElementType, v1);
                    if (arr.IntValues != null) arr.IntValues.Add(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
            }
        }
        return arr;
    }

    private static void Skip(BinaryReader r, long count)
    {
        var s = r.BaseStream;
        if (s.CanSeek)
        {
            s.Seek(count, SeekOrigin.Current);
            return;
        }
        var buf = new byte[Math.Min(count, 64 * 1024)];
        while (count > 0)
        {
            var n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            count -= n;
        }
    }
}
