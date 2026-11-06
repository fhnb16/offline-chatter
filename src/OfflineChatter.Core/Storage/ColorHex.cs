using System.Globalization;

namespace OfflineChatter.Core.Storage;

/// <summary>Разбор цветов #RGB / #RRGGBB без зависимости от WPF.</summary>
public static class ColorHex
{
    public static bool TryParse(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text!.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);

        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });

        if (s.Length != 6) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;

        r = (byte)((v >> 16) & 0xFF);
        g = (byte)((v >> 8) & 0xFF);
        b = (byte)(v & 0xFF);
        return true;
    }

    public static string Format(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
