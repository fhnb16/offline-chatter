using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace OfflineChatter.Core.Storage;

/// <summary>Чтение и атомарная запись JSON-файлов.</summary>
public static class JsonFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static JsonSerializerSettings Settings { get; } = CreateSettings();

    private static JsonSerializerSettings CreateSettings()
    {
        var s = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };
        s.Converters.Add(new StringEnumConverter());
        return s;
    }

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonConvert.DeserializeObject<T>(text, Settings);
    }

    /// <summary>
    /// Пишет во временный файл рядом, сбрасывает на диск и подменяет целевой.
    /// При сбое питания остаётся либо старая, либо новая версия, но не обрезанная.
    /// </summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        var json = JsonConvert.SerializeObject(value, Settings);
        WriteAllTextAtomic(path, json);
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);

        var tmp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(content);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
                File.Replace(tmp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, full);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* best effort */ }
        }
    }
}
