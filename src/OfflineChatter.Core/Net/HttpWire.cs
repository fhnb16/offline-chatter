using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace OfflineChatter.Core.Net;

/// <summary>Минимальный разбор HTTP/1.1 поверх произвольного потока (для собственного TLS-стека).</summary>
internal static class HttpWire
{
    public sealed class ResponseHead
    {
        public int StatusCode { get; set; }
        public string Reason { get; set; } = string.Empty;
        public List<KeyValuePair<string, string>> Headers { get; } = new();

        public string? Get(string name)
        {
            foreach (var h in Headers)
                if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)) return h.Value;
            return null;
        }
    }

    /// <summary>Читает строку до CRLF (заголовки HTTP — ASCII/Latin-1). Ограничение длины — защита от мусора.</summary>
    public static string? ReadLine(Stream s, int maxLength = 16 * 1024)
    {
        var sb = new StringBuilder(64);
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
            if (sb.Length > maxLength) throw new IOException("HTTP header line too long");
        }
    }

    public static ResponseHead ReadResponseHead(Stream s)
    {
        while (true)
        {
            var status = ReadLine(s) ?? throw new IOException("Connection closed before HTTP response");
            // "HTTP/1.1 200 OK"
            var parts = status.Split(new[] { ' ' }, 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new IOException("Malformed HTTP status line: " + Truncate(status));

            var head = new ResponseHead { StatusCode = code, Reason = parts.Length > 2 ? parts[2] : string.Empty };
            string? line;
            while (!string.IsNullOrEmpty(line = ReadLine(s)))
            {
                var colon = line!.IndexOf(':');
                if (colon <= 0) continue;
                head.Headers.Add(new KeyValuePair<string, string>(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()));
            }

            if (code is >= 100 and < 200 && code != 101) continue; // 100 Continue и прочие информационные
            return head;
        }
    }

    public static Stream CreateBodyStream(Stream connection, ResponseHead head, bool isHead, IDisposable owner)
    {
        if (isHead || head.StatusCode is 204 or 304)
            return new OwnedStream(new MemoryStream(Array.Empty<byte>(), false), owner);

        var te = head.Get("Transfer-Encoding");
        if (te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            return new OwnedStream(new ChunkedReadStream(connection), owner);

        var cl = head.Get("Content-Length");
        if (cl != null && long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            return new OwnedStream(new LengthLimitedStream(connection, length), owner);

        return new OwnedStream(connection, owner); // до закрытия соединения
    }

    private static string Truncate(string s) => s.Length > 80 ? s.Substring(0, 80) + "…" : s;

    // ---------------- потоки тела ----------------

    internal abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Тело с Content-Length.</summary>
    internal sealed class LengthLimitedStream : ReadOnlyStream
    {
        private readonly Stream _inner;
        private long _remaining;

        public LengthLimitedStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (n == 0) throw new IOException("Connection closed: response body truncated");
            _remaining -= n;
            return n;
        }
    }

    /// <summary>Transfer-Encoding: chunked.</summary>
    internal sealed class ChunkedReadStream : ReadOnlyStream
    {
        private readonly Stream _inner;
        private long _chunkRemaining;
        private bool _done;

        public ChunkedReadStream(Stream inner) => _inner = inner;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_done) return 0;
            if (_chunkRemaining == 0)
            {
                var sizeLine = ReadLine(_inner) ?? throw new IOException("Connection closed in chunked body");
                var semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                if (!long.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _chunkRemaining))
                    throw new IOException("Malformed chunk size");
                if (_chunkRemaining == 0)
                {
                    // трейлеры до пустой строки
                    while (!string.IsNullOrEmpty(ReadLine(_inner))) { }
                    _done = true;
                    return 0;
                }
            }

            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _chunkRemaining));
            if (n == 0) throw new IOException("Connection closed in chunk");
            _chunkRemaining -= n;
            if (_chunkRemaining == 0) ReadLine(_inner); // CRLF после блока
            return n;
        }
    }

    /// <summary>Поток, который при закрытии освобождает соединение.</summary>
    internal sealed class OwnedStream : ReadOnlyStream
    {
        private readonly Stream _inner;
        private IDisposable? _owner;

        public OwnedStream(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch (IOException) { }
                _owner?.Dispose();
                _owner = null;
            }
            base.Dispose(disposing);
        }
    }

    public static string FormatHostHeader(Uri uri) => uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

    public static HttpStatusCode Status(int code) => (HttpStatusCode)code;
}
