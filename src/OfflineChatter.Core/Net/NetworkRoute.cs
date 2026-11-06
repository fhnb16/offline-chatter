using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OfflineChatter.Core.Net;

public enum ProxyKind
{
    Http,
    /// <summary>SOCKS5, имена хостов разрешаются локально.</summary>
    Socks5,
    /// <summary>SOCKS5h, имена хостов разрешает прокси (DNS не утекает мимо прокси/VPN).</summary>
    Socks5h,
}

/// <summary>Прокси для собственного сетевого стека.</summary>
public sealed record ProxyEndpoint(ProxyKind Kind, string Host, int Port, NetworkCredential? Credentials)
{
    /// <summary>
    /// Разбор «socks5h://user:pass@host:1080», «socks5://host:1080», «http://host:3128», «host:port» (http).
    /// </summary>
    public static ProxyEndpoint? Parse(string? text, NetworkCredential? fallbackCredentials = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text!.Trim();
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
        var kind = u.Scheme.ToLowerInvariant() switch
        {
            "socks5h" => ProxyKind.Socks5h,
            "socks5" or "socks" => ProxyKind.Socks5,
            "http" or "https" => ProxyKind.Http,
            _ => (ProxyKind?)null,
        };
        if (kind == null) return null;
        var port = u.IsDefaultPort || u.Port <= 0 ? (kind == ProxyKind.Http ? 8080 : 1080) : u.Port;
        NetworkCredential? creds = fallbackCredentials;
        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            var parts = u.UserInfo.Split(new[] { ':' }, 2);
            creds = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }
        return new ProxyEndpoint(kind.Value, u.Host, port, creds);
    }

    /// <summary>Каноническая ссылка без пароля (для отображения и хранения).</summary>
    public string ToUrl(bool withUser = false)
    {
        var scheme = Kind switch { ProxyKind.Socks5h => "socks5h", ProxyKind.Socks5 => "socks5", _ => "http" };
        var user = withUser && Credentials != null && !string.IsNullOrEmpty(Credentials.UserName) ? Uri.EscapeDataString(Credentials.UserName) + "@" : string.Empty;
        return $"{scheme}://{user}{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";
    }
}

public sealed record NetworkAdapter(string Id, string Name, string Description, string? IPv4, string Type)
{
    public string Title => $"{Name} — {Description}" + (IPv4 != null ? $" ({IPv4})" : string.Empty);
}

/// <summary>Сетевые адаптеры и выбор локального адреса для «выхода в сеть через конкретный интерфейс».</summary>
public static class NetworkRoute
{
    public static IReadOnlyList<NetworkAdapter> ListAdapters()
    {
        var list = new List<NetworkAdapter>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ip = FirstIPv4(ni);
                list.Add(new NetworkAdapter(ni.Id, ni.Name, ni.Description, ip?.ToString(), ni.NetworkInterfaceType.ToString()));
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>Локальный адрес выбранного интерфейса (по Id или имени); null — интерфейс не найден или без IPv4.</summary>
    public static IPAddress? LocalAddressFor(string? interfaceId)
    {
        if (string.IsNullOrWhiteSpace(interfaceId)) return null;
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, interfaceId, StringComparison.OrdinalIgnoreCase) || string.Equals(n.Name, interfaceId, StringComparison.OrdinalIgnoreCase));
            return ni == null ? null : FirstIPv4(ni);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static IPAddress? FirstIPv4(NetworkInterface ni) =>
        ni.GetIPProperties().UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
}

/// <summary>Запрос не отправлен: включён офлайн-режим.</summary>
public sealed class OfflineModeException : System.Net.Http.HttpRequestException
{
    public OfflineModeException(string host) : base("Offline mode: network access is disabled (" + host + ")") { }
}
