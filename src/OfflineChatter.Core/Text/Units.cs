using System;
using System.Globalization;

namespace OfflineChatter.Core.Text;

/// <summary>Форматирование размеров, скоростей и длительностей для интерфейса и отчётов.</summary>
public static class Units
{
    /// <summary>Подписи единиц. По умолчанию английские; UI подставляет локализованные при старте.</summary>
    public static string[] ByteSuffixes { get; set; } = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatBytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) return "—";
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < ByteSuffixes.Length - 1)
        {
            v /= 1024;
            i++;
        }
        var fmt = i == 0 ? "0" : v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##";
        return v.ToString(fmt, culture) + " " + ByteSuffixes[i];
    }

    public static string FormatSpeed(double bytesPerSecond, CultureInfo? culture = null) =>
        FormatBytes((long)Math.Max(0, bytesPerSecond), culture) + "/s";

    public static string FormatDuration(TimeSpan t, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (t.TotalSeconds < 1) return ((int)t.TotalMilliseconds).ToString(culture) + " ms";
        if (t.TotalMinutes < 1) return t.TotalSeconds.ToString("0.0", culture) + " s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
