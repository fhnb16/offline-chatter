using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineChatter.Core.Chat;

/// <summary>
/// Разбор Server-Sent Events: события разделены пустой строкой, поля "data:" склеиваются через \n,
/// комментарии (":ping") игнорируются. Поддерживаются CRLF/LF и разрывы посреди строки.
/// </summary>
public sealed class SseReader
{
    private readonly StreamReader _reader;

    public SseReader(Stream stream) => _reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192);

    /// <summary>Следующее событие: (event, data) или null в конце потока.</summary>
    public async Task<(string? Event, string Data)?> ReadAsync(CancellationToken ct)
    {
        var data = new StringBuilder();
        string? evt = null;
        var hasData = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) return hasData ? (evt, data.ToString()) : null;
            if (line.Length == 0)
            {
                if (hasData) return (evt, data.ToString());
                evt = null;
                continue;
            }
            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line.Substring(0, colon);
            var value = colon < 0 ? string.Empty : line.Substring(colon + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);

            switch (field)
            {
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                case "event":
                    evt = value;
                    break;
            }
        }
    }
}
