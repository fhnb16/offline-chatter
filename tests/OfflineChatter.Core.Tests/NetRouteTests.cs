using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class NetRouteTests
{
    [Theory]
    [InlineData("socks5h://user:p%40ss@127.0.0.1:9050", ProxyKind.Socks5h, "127.0.0.1", 9050, "user", "p@ss")]
    [InlineData("socks5://proxy.local:1080", ProxyKind.Socks5, "proxy.local", 1080, null, null)]
    [InlineData("socks://h", ProxyKind.Socks5, "h", 1080, null, null)]
    [InlineData("http://10.0.0.1:3128", ProxyKind.Http, "10.0.0.1", 3128, null, null)]
    [InlineData("10.0.0.1:3128", ProxyKind.Http, "10.0.0.1", 3128, null, null)]
    public void Parses_Proxy_Urls(string url, ProxyKind kind, string host, int port, string? user, string? pass)
    {
        var p = ProxyEndpoint.Parse(url)!;
        Assert.Equal(kind, p.Kind);
        Assert.Equal(host, p.Host);
        Assert.Equal(port, p.Port);
        Assert.Equal(user, p.Credentials?.UserName);
        Assert.Equal(pass, p.Credentials?.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x:1")]
    public void Rejects_Bad_Proxy(string url) => Assert.Null(ProxyEndpoint.Parse(url));

    [Fact]
    public async Task Offline_Mode_Blocks_All_Requests()
    {
        using var http = new HttpService(new NetworkSettings { OfflineMode = true }, new TrustedRoots(), "t");
        await Assert.ThrowsAsync<OfflineModeException>(() => http.GetStringAsync("https://example.com/", CancellationToken.None));
        var check = await http.CheckAsync("https://example.com/", CancellationToken.None);
        Assert.False(check.Ok);
        Assert.Contains("Offline", check.Message);
    }

    [Fact]
    public void Lists_Adapters() => Assert.All(NetworkRoute.ListAdapters(), a => Assert.False(string.IsNullOrEmpty(a.Id)));

    /// <summary>SOCKS5 с логином: проверяем рукопожатие, имя хоста (socks5h) и HTTP поверх туннеля.</summary>
    [Fact]
    public async Task Socks5h_With_Auth_Tunnels_Http()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? requestedHost = null;
        string? requestLine = null;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            byte[] Read(int n)
            {
                var b = new byte[n];
                var r = 0;
                while (r < n) r += s.Read(b, r, n - r);
                return b;
            }
            var greet = Read(2);
            Read(greet[1]);
            s.Write(new byte[] { 5, 2 }, 0, 2); // требуем логин/пароль
            Read(1);
            var user = Encoding.UTF8.GetString(Read(Read(1)[0]));
            var pass = Encoding.UTF8.GetString(Read(Read(1)[0]));
            s.Write(new byte[] { 1, (byte)(user == "u" && pass == "p" ? 0 : 1) }, 0, 2);
            var head = Read(4);
            Assert.Equal(3, head[3]); // доменное имя
            requestedHost = Encoding.ASCII.GetString(Read(Read(1)[0]));
            Read(2);
            s.Write(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, 0, 10);
            var reader = new StreamReader(s, Encoding.ASCII);
            requestLine = reader.ReadLine();
            while (!string.IsNullOrEmpty(reader.ReadLine())) { }
            var body = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            s.Write(body, 0, body.Length);
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var settings = new NetworkSettings { ProxyMode = ProxyMode.Manual, ProxyAddress = $"socks5h://u:p@127.0.0.1:{port}", TimeoutSeconds = 10 };
        using var http = new HttpService(settings, new TrustedRoots(), "t");
        Assert.True(http.ForcesManagedStack);
        var text = await http.GetStringAsync("http://example.invalid/test", CancellationToken.None);
        await server;
        listener.Stop();

        Assert.Equal("ok", text);
        Assert.Equal("example.invalid", requestedHost); // DNS на стороне прокси
        Assert.Equal("GET /test HTTP/1.1", requestLine);
    }
}
