using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OfflineChatter.Core.Net;

/// <summary>
/// Проверка имени хоста по сертификату (RFC 6125): SAN dNSName, wildcard только в левой метке.
/// Нужна для собственного TLS (уровень 3) — SChannel делает это сам.
/// </summary>
public static class HostnameMatcher
{
    private const string SanOid = "2.5.29.17";

    public static bool Matches(X509Certificate2 cert, string host)
    {
        var names = GetDnsNames(cert);
        if (names.Count == 0)
        {
            var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
            if (!string.IsNullOrEmpty(cn)) names.Add(cn);
        }
        return names.Any(n => MatchesPattern(n, host));
    }

    public static bool MatchesPattern(string pattern, string host)
    {
        pattern = pattern.TrimEnd('.').ToLowerInvariant();
        host = host.TrimEnd('.').ToLowerInvariant();
        if (pattern.Length == 0 || host.Length == 0) return false;
        if (!pattern.StartsWith("*.", StringComparison.Ordinal)) return pattern == host;

        // "*.example.com" совпадает с "a.example.com", но не с "example.com" и не с "a.b.example.com"
        var suffix = pattern.Substring(1);
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var label = host.Substring(0, host.Length - suffix.Length);
        return label.Length > 0 && label.IndexOf('.') < 0;
    }

    /// <summary>Разбор расширения subjectAltName (DER) без зависимости от локали Windows.</summary>
    public static List<string> GetDnsNames(X509Certificate2 cert)
    {
        var result = new List<string>();
        var ext = cert.Extensions[SanOid];
        if (ext == null) return result;
        var raw = ext.RawData;
        var pos = 0;
        if (!ReadTag(raw, ref pos, out var tag, out var len) || tag != 0x30) return result;
        var end = Math.Min(raw.Length, pos + len);
        while (pos < end)
        {
            if (!ReadTag(raw, ref pos, out tag, out len) || pos + len > raw.Length) break;
            if (tag == 0x82) // [2] dNSName, IA5String
                result.Add(Encoding.ASCII.GetString(raw, pos, len));
            pos += len;
        }
        return result;
    }

    private static bool ReadTag(byte[] data, ref int pos, out int tag, out int length)
    {
        tag = 0;
        length = 0;
        if (pos + 2 > data.Length) return false;
        tag = data[pos++];
        int b = data[pos++];
        if (b < 0x80)
        {
            length = b;
            return true;
        }
        var count = b & 0x7F;
        if (count is 0 or > 4 || pos + count > data.Length) return false;
        for (var i = 0; i < count; i++) length = (length << 8) | data[pos++];
        return length >= 0;
    }
}
